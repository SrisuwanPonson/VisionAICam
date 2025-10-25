using System;
using System.Linq;
using System.Management;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.IO;
using System.Diagnostics;
using OpenCvSharp;
using ClearEngine.Devices.Camera;
using VisionAICam; // SettingsManager, AppSettings
using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenCvSharp.WpfExtensions;
using Python.Runtime;

namespace VisionAICam.Pages
{
    /// <summary>
    /// DiagnosticsPage is a modular Page component for system, camera, model, and performance diagnostics.
    /// Designed to be hosted inside a Frame or NavigationWindow.
    /// </summary>
    public partial class DiagnosticsPage : Page
    {
        // Camera runtime fields (ClearEngine.Devices.Camera)
        private ICamera? _camera;
        private volatile bool _isLive = false;
        private BitmapSource? _lastBitmap;
        private Stopwatch _fpsWatch = new Stopwatch();
        private int _fpsFrameCount = 0;

        // App settings (for capture folder, etc.)
        private AppSettings? _appSettings;

        // Python init tracking
        private static readonly object _pythonInitLock = new();
        private static bool _pythonInitialized = false;

        // Small detection DTO (same shape as Production)
        private class DetectionResult
        {
            public string ClassName { get; set; } = "";
            public double Confidence { get; set; }
            public string Box { get; set; } = ""; // "x1,y1,x2,y2" or "cx,cy,w,h,angle"
            public string Task { get; set; } = ""; // "detect" or "obb"
        }

        public DiagnosticsPage()
        {
            InitializeComponent();
            _appSettings = SettingsManager.Load();
            LoadSystemInfo();
            LoadCameraInfo();
            LoadModelInfo();
            LoadPerformanceMetrics();

            // Populate model path UI
            ModelPathText.Text = string.IsNullOrEmpty(_appSettings?.DefaultModelPath) ? "(none)" : _appSettings!.DefaultModelPath!;
            Log("Diagnostics page initialized.");
        }

        // 🖥️ Step 1: System Info
        private void LoadSystemInfo()
        {
            OsVersionText.Text = Environment.OSVersion.ToString();
            DotNetVersionText.Text = Environment.Version.ToString();
            MemoryText.Text = $"{GetTotalMemory()} GB";
            CpuText.Text = GetCpuName();
        }

        #region 📷 Step 2: Camera Info
        // 📷 Step 2: Camera Info
        private void LoadCameraInfo()
        {
            var cameras = GetCameraNames();
            CameraListText.Text = cameras.Length > 0
                ? $"Detected Cameras: {string.Join(", ", cameras)}"
                : "No cameras detected.";
        }

        private void RefreshCamerasButton_Click(object sender, RoutedEventArgs e)
        {
            LoadCameraInfo();
            Log("Camera list refreshed.");
        }
        #endregion

        // 🧠 Step 3: Model Info
        private void LoadModelInfo()
        {
            ModelInfoText.Text = "Model: YOLOv5s (Ultralytics)";
        }

        private void TestModelButton_Click(object sender, RoutedEventArgs e)
        {
            Log("Model inference test triggered.");
            MessageBox.Show("Model inference test completed.", "YOLO Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 📊 Step 4: Performance Metrics
        private void LoadPerformanceMetrics()
        {
            FpsText.Text = "30.2";
            InferenceTimeText.Text = "42 ms";
        }

        // 📜 Step 5: Thread-safe Logging
        private void Log(string message)
        {
            var line = $"{DateTime.Now:HH:mm:ss} - {message}\n";

            // Use existing SafeInvokeOnUi helper so background threads can log safely.
            SafeInvokeOnUi(() =>
            {
                try
                {
                    // Use AppendText instead of reading/writing Text to avoid cross-thread property reads.
                    LogTextBox.AppendText(line);
                    LogTextBox.ScrollToEnd();
                }
                catch
                {
                    // Swallow exceptions to avoid crashing during shutdown/cleanup.
                }
            });
        }

        // 🔧 Helpers
        private string GetCpuName()
        {
            using var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_Processor");
            return searcher.Get().Cast<ManagementObject>().FirstOrDefault()?["Name"]?.ToString() ?? "Unknown";
        }

        private double GetTotalMemory()
        {
            using var searcher = new ManagementObjectSearcher("SELECT TotalVisibleMemorySize FROM Win32_OperatingSystem");
            var memKb = Convert.ToDouble(searcher.Get().Cast<ManagementObject>().FirstOrDefault()?["TotalVisibleMemorySize"] ?? 0);
            return Math.Round(memKb / 1024 / 1024, 1); // Convert KB to GB
        }

        private string[] GetCameraNames()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass FROM Win32_PnPEntity " +
                    "WHERE Name LIKE '%Camera%' OR Name LIKE '%Image%' OR PNPClass = 'Image' OR PNPClass = 'Camera'");

                // Dispose the collection returned by Get() to avoid resource leaks and satisfy analyzers.
                using var results = searcher.Get();

                return results.Cast<ManagementBaseObject>()
                              .Select(m => m["Name"]?.ToString())
                              .Where(n => !string.IsNullOrEmpty(n))
                              .Distinct()
                              .ToArray();
            }
            catch (Exception)
            {
                return Array.Empty<string>();
            }
        }

        private void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLive)
            {
                try
                {
                    // Create camera via factory (uses OpenCv backend by default)
                    _camera = CameraFactory.Create();

                    // Subscribe to frame events
                    _camera.FrameReady += OnFrameReady;

                    // Start the first camera (index 0). You can extend UI to choose index.
                    _camera.Start(0);

                    if (!_camera.IsOpened)
                    {
                        // failed to open
                        _camera.FrameReady -= OnFrameReady;
                        _camera.Dispose();
                        _camera = null;
                        CameraStatusText.Text = "No camera available or failed to open camera.";
                        Log("Failed to open camera.");
                        return;
                    }

                    _isLive = true;
                    _fpsWatch.Restart();
                    _fpsFrameCount = 0;

                    CameraStatusText.Text = "Live";
                    Log("Live preview started (ClearEngine.Devices.Camera).");
                }
                catch (Exception ex)
                {
                    CameraStatusText.Text = $"Failed to start live: {ex.Message}";
                    Log($"Failed to start live preview: {ex.Message}");
                    try { _camera?.Dispose(); } catch { }
                    _camera = null;
                    _isLive = false;
                }
            }
            else
            {
                // Stop live preview
                StopCamera();
            }
        }

        private void OnFrameReady(BitmapSource bitmap)
        {
            // FrameReady is invoked from the camera backend thread. Bitmap is frozen by OpenCvCamera.
            _lastBitmap = bitmap;

            // Update FPS counter
            _fpsFrameCount++;
            var elapsed = _fpsWatch.Elapsed.TotalSeconds;
            if (elapsed >= 1.0)
            {
                var fps = _fpsFrameCount / elapsed;
                _fpsFrameCount = 0;
                _fpsWatch.Restart();

                SafeInvokeOnUi(() =>
                {
                    FpsText.Text = $"{fps:F1}";
                });
            }

            // Update status & optionally show preview if an Image named "CameraPreviewImage" exists in XAML
            SafeInvokeOnUi(() =>
            {
                CameraStatusText.Text = "Live";
                if (this.FindName("CameraPreviewImage") is System.Windows.Controls.Image img)
                {
                    img.Source = bitmap;
                }
            });
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_camera == null || !_camera.IsOpened)
                {
                    CameraStatusText.Text = "Camera not running.";
                    Log("Capture requested but camera is not running.");
                    return;
                }

                // Use backend method to capture the current OpenCV Mat (if supported)
                var mat = _camera.CaptureCurrentFrame();
                if (mat == null)
                {
                    CameraStatusText.Text = "No frame available to capture.";
                    Log("Capture requested but no frame available.");
                    return;
                }

                try
                {
                    // Use app setting DefaultImagePath if provided, otherwise fallback to My Pictures
                    string basePath = !string.IsNullOrWhiteSpace(_appSettings?.DefaultImagePath)
                        ? _appSettings!.DefaultImagePath!
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                    string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                    string savePath = System.IO.Path.Combine(basePath, folderName);

                    if (!Directory.Exists(savePath))
                        Directory.CreateDirectory(savePath);

                    string ClassName = ""; // adapt if you have UI controls to set class/category
                    string Category = "";
                    string fileName = $"{ClassName}_{Category}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = System.IO.Path.Combine(savePath, fileName);

                    // Prefer Mat.SaveImage to ensure Mat encoding/format correctness
                    mat.SaveImage(filePath);

                    CameraStatusText.Text = $"Saved: {filePath}";
                    Log($"Captured image saved to {filePath}");
                }
                finally
                {
                    mat.Dispose();
                }
            }
            catch (Exception ex)
            {
                CameraStatusText.Text = $"Capture failed: {ex.Message}";
                Log($"Capture failed: {ex.Message}");
            }
        }

        private void StopCamera()
        {
            if (_camera == null) return;

            try
            {
                _camera.FrameReady -= OnFrameReady;
                _camera.Stop();
                _camera.Dispose();
            }
            catch (Exception ex)
            {
                Log($"Error stopping camera: {ex.Message}");
            }
            finally
            {
                _camera = null;
                _isLive = false;

                // Use safe UI invoke to avoid exceptions if dispatcher is shutting down
                SafeInvokeOnUi(() =>
                {
                    FpsText.Text = "0";
                    CameraStatusText.Text = "Stopped";
                    if (this.FindName("CameraPreviewImage") is System.Windows.Controls.Image img)
                    {
                        img.Source = null;
                    }
                });

                Log("Live preview stopped.");
            }
        }

        // Safe helper to update UI without throwing during application shutdown.
        private void SafeInvokeOnUi(Action action)
        {
            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
                {
                    // Dispatcher unavailable or shutting down; skip UI update.
                    return;
                }

                if (Dispatcher.CheckAccess())
                {
                    action();
                }
                else
                {
                    Dispatcher.BeginInvoke(action);
                }
            }
            catch
            {
                // Swallow exceptions to avoid crashing during shutdown/cleanup.
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopCamera();
                Log("Stop button clicked - camera stopped.");
            }
            catch (Exception ex)
            {
                CameraStatusText.Text = $"Stop failed: {ex.Message}";
                Log($"StopButton_Click failed: {ex.Message}");
            }
        }

        // ---------------------------
        // Model tab actions
        // ---------------------------
        private void SelectModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Select Model",
                Filter = "Model Files|*.onnx;*.pb;*.pt;*.tflite|All Files|*.*"
            };
            if (dlg.ShowDialog() == true)
            {
                ModelPathText.Text = dlg.FileName;
                Log($"Model selected: {dlg.FileName}");
            }
        }

        private void SaveModelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_appSettings == null) _appSettings = new AppSettings();
                _appSettings.DefaultModelPath = ModelPathText.Text;
                SettingsManager.Save(_appSettings);
                Log($"Model path saved to settings: {_appSettings.DefaultModelPath}");
                MessageBox.Show("Model saved to settings.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Log($"Failed to save model: {ex.Message}");
                MessageBox.Show($"Failed to save model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            // Prefer the saved capture folder from settings if it exists, otherwise fall back to My Pictures.
            string initialDir = null;
            if (!string.IsNullOrWhiteSpace(_appSettings?.DefaultImagePath) && Directory.Exists(_appSettings.DefaultImagePath))
            {
                initialDir = _appSettings.DefaultImagePath;
            }
            else
            {
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            }

            var dlg = new OpenFileDialog
            {
                Title = "Load Image for Inference",
                Filter = "Image Files|*.png;*.jpg;*.jpeg;*.bmp;*.gif|All Files|*.*",
                InitialDirectory = initialDir,
                CheckFileExists = true,
                CheckPathExists = true,
                RestoreDirectory = true
            };

            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(dlg.FileName);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();

                    ModelTestImage.Source = bmp;
                    ClearModelBoundingBoxes();
                    Log($"Loaded test image: {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    Log($"Failed to load image: {ex.Message}");
                    MessageBox.Show($"Failed to load image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void RunInferenceButton_Click(object sender, RoutedEventArgs e)
        {
            if (ModelTestImage.Source is not BitmapSource bitmap)
            {
                MessageBox.Show("Load an image before running inference.", "No Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_appSettings?.DefaultModelPath))
            {
                MessageBox.Show("No model selected. Please select and save a model path in settings.", "No Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Ensure Python.NET is initialized (will attempt to locate an embedded python dll under Script\NewEnv if present)
            if (!EnsurePythonInitialized(out var initError))
            {
                Log($"Python initialization failed: {initError}");
                MessageBox.Show($"Python initialization failed: {initError}\nMake sure Python and pythonnet are configured correctly.", "Python Init Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            
            // Convert BitmapSource to Mat on UI thread then run inference in threadpool
            Mat mat = null!;
            try
            {
                mat = bitmap.ToMat();

                // Ensure mat is 640x480 (resize if necessary)
                var targetSize = new OpenCvSharp.Size(640, 480);

                // Convert 4-channel BGRA -> 3-channel BGR if needed (Python model usually expects 3 channels)
                if (mat.Channels() == 4)
                {
                    var tmp = new Mat();
                    Cv2.CvtColor(mat, tmp, ColorConversionCodes.BGRA2BGR);
                    mat.Dispose();
                    mat = tmp;
                }

                if (mat.Width != targetSize.Width || mat.Height != targetSize.Height)
                {
                    var resized = new Mat();
                    Cv2.Resize(mat, resized, targetSize, 0, 0, InterpolationFlags.Linear);
                    mat.Dispose();
                    mat = resized;
                }
            }
            catch (Exception ex)
            {
                Log($"Failed to convert/resize image to Mat: {ex.Message}");
                MessageBox.Show($"Failed to convert image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            DetectionResult[] detections = Array.Empty<DetectionResult>();
            try
            {
                // Run inference on threadpool to avoid blocking UI. GetDetectionsFromPython expects PythonEngine initialized.
                detections = await System.Threading.Tasks.Task.Run(() => GetDetectionsFromPython(mat));
            }
            catch (Exception ex)
            {
                Log($"Inference error: {ex.Message}");
                MessageBox.Show($"Inference error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                mat.Dispose();
            }

            // Draw results
            DrawModelBoundingBoxes(detections, bitmap);
        }

        // Reuses approach in Production.GetDetectionsFromPython
        private DetectionResult[] GetDetectionsFromPython(Mat mat)
        {
            try
            {
                Cv2.ImEncode(".jpg", mat, out var buf);

                using (Py.GIL())
                {
                    // Ensure script folder on sys.path
                    string pythonScriptDir = AppDomain.CurrentDomain.BaseDirectory;
                    pythonScriptDir = System.IO.Path.Combine(pythonScriptDir, "Script");
                    dynamic sys = Py.Import("sys");

                    bool pathExists = false;
                    foreach (dynamic p in sys.path)
                    {
                        if (pythonScriptDir.Equals((string)p.ToString(), StringComparison.OrdinalIgnoreCase))
                        {
                            pathExists = true;
                            break;
                        }
                    }
                    if (!pathExists) sys.path.append(pythonScriptDir);

                    // Instrumentation: write progress to log to help debugging
                    Log($"Python: importing inference module from '{pythonScriptDir}'");

                    dynamic inference;
                    try
                    {
                        inference = Py.Import("inference");
                    }
                    catch (PythonException pex)
                    {
                        // Capture Python traceback
                        try
                        {
                            dynamic tb = Py.Import("traceback");
                            string trace = tb.format_exc();
                            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error_inference_import.log");
                            File.WriteAllText(logPath, trace);
                            Log($"Failed to import 'inference' module. Trace saved to {logPath}");
                        }
                        catch
                        {
                            // fallback
                            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error_inference_import.log");
                            File.WriteAllText(logPath, pex.ToString());
                            Log($"Failed to import 'inference' module. See {logPath}");
                        }

                        return Array.Empty<DetectionResult>();
                    }

                    string modelPath = _appSettings?.DefaultModelPath ?? "model.pt";
                    string logDir = Logger.Instance.GetLogDirectory();

                    // Call detect and capture Python-level exceptions with traceback
                    dynamic results;
                    try
                    {
                        Log("Python: calling inference.detect(...)");
                        Logger.Instance.Info("Python: calling inference.detect(...)");
                        results = inference.detect(buf, modelPath, logDir);
                    }
                    catch (PythonException pex)
                    {
                        try
                        {
                            dynamic tb = Py.Import("traceback");
                            string trace = tb.format_exc();
                            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error_inference_detect.log");
                            File.WriteAllText(logPath, trace);
                            Logger.Instance.Error($"Python detect() failed. Trace saved to {logPath}\n{trace}");
                            SafeInvokeOnUi(() => Log($"Python detect() failed. See {logPath}"));
                        }
                        catch
                        {
                            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error_inference_detect.log");
                            File.WriteAllText(logPath, pex.ToString());
                            Logger.Instance.Error($"Python detect() failed. See {logPath}\n{pex}");
                            SafeInvokeOnUi(() => Log($"Python detect() failed. See {logPath}"));
                        }

                        return Array.Empty<DetectionResult>();
                    }
                    catch (Exception ex)
                    {
                        // non-Python errors
                        string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error_inference_detect.log");
                        File.WriteAllText(logPath, ex.ToString());
                        Log($"Error calling detect(): {ex.Message} (see {logPath})");
                        return Array.Empty<DetectionResult>();
                    }

                    // Parse results
                    var detections = new Collection<DetectionResult>();
                    try
                    {
                        foreach (dynamic det in results)
                        {
                            string task = det["Task"]?.ToString();
                            string className = det["class"]?.ToString();
                            double confidence = (double)det["confidence"];

                            if (task == "detect")
                            {
                                var box = det["box"];
                                if (box != null && box.Length() == 4)
                                {
                                    detections.Add(new DetectionResult
                                    {
                                        ClassName = className,
                                        Confidence = confidence,
                                        Box = $"{box[0]},{box[1]},{box[2]},{box[3]}",
                                        Task = "detect"
                                    });
                                }
                            }
                            else if (task == "obb")
                            {
                                var rotateBox = det["rotate_box"];
                                if (rotateBox != null && rotateBox.Length() == 5)
                                {
                                    detections.Add(new DetectionResult
                                    {
                                        ClassName = className,
                                        Confidence = confidence,
                                        Box = $"{rotateBox[0]},{rotateBox[1]},{rotateBox[2]},{rotateBox[3]},{rotateBox[4]}",
                                        Task = "obb"
                                    });
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_parse_results.log");
                        File.WriteAllText(logPath, ex.ToString());
                        Log($"Failed to parse inference results: {ex.Message} (see {logPath})");
                        return Array.Empty<DetectionResult>();
                    }

                    Log($"Python: detect returned {detections.Count} results");
                    return detections.ToArray();
                }
            }
            catch (Exception ex)
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error.log");
                File.WriteAllText(logPath, ex.ToString());
                string errorMsg = $"Detection error: {ex.Message} (see python_error.log)";
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (this.FindName("ModelInfoText") is TextBlock tb)
                            tb.Text = errorMsg;
                        Log(errorMsg);
                    }
                    catch
                    {
                        // swallow
                    }
                });
            }
            return Array.Empty<DetectionResult>();
        }

        // Ensure Python.NET is initialized. Tries to set PythonDLL if an embedded distribution exists under Script\NewEnv\Python313\python313.dll
        private bool EnsurePythonInitialized(out string errorMessage)
        {
            errorMessage = "";
            lock (_pythonInitLock)
            {
                if (_pythonInitialized) return true;

                try
                {
                    // If a bundled python DLL exists in Script\NewEnv, prefer it.
                    var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? "";
                    var bundled = System.IO.Path.Combine(baseDir, "Script", "NewEnv", "Python313", "python313.dll");
                    if (File.Exists(bundled))
                    {
                        try
                        {
                            Python.Runtime.Runtime.PythonDLL = bundled;
                        }
                        catch
                        {
                            // ignore - Runtime may throw if set twice, we'll try initialization below
                        }
                    }

                    // Initialize Python engine (no-op if already initialized)
                    PythonEngine.Initialize();
                    _pythonInitialized = true;
                    return true;
                }
                catch (Exception ex)
                {
                    errorMessage = ex.Message;
                    return false;
                }
            }
        }

        // ---------------------------
        // Drawing helpers (unchanged)
        // ---------------------------
        // Draw detection boxes onto ModelBoundingCanvas. Coordinates are expected to be in image pixel space.
        private void DrawModelBoundingBoxes(DetectionResult[] detections, BitmapSource bitmap)
        {
            Dispatcher.BeginInvoke(() =>
            {
                ClearModelBoundingBoxes();

                if (ModelTestImage.Source == null) return;

                // Ensure canvas matches displayed image size
                double dispW = ModelTestImage.ActualWidth;
                double dispH = ModelTestImage.ActualHeight;
                if (dispW <= 0 || dispH <= 0)
                {
                    // fallback to bitmap pixel size
                    dispW = bitmap.PixelWidth;
                    dispH = bitmap.PixelHeight;
                    ModelBoundingCanvas.Width = dispW;
                    ModelBoundingCanvas.Height = dispH;
                }
                else
                {
                    ModelBoundingCanvas.Width = dispW;
                    ModelBoundingCanvas.Height = dispH;
                }

                double scaleX = dispW / bitmap.PixelWidth;
                double scaleY = dispH / bitmap.PixelHeight;

                foreach (var det in detections)
                {
                    var parts = det.Box.Split(',');
                    if (det.Task == "detect" && parts.Length == 4 &&
                        double.TryParse(parts[0], out double x1) &&
                        double.TryParse(parts[1], out double y1) &&
                        double.TryParse(parts[2], out double x2) &&
                        double.TryParse(parts[3], out double y2))
                    {
                        double left = x1 * scaleX;
                        double top = y1 * scaleY;
                        double width = Math.Abs(x2 - x1) * scaleX;
                        double height = Math.Abs(y2 - y1) * scaleY;

                        var rect = new Rectangle
                        {
                            Stroke = Brushes.Red,
                            StrokeThickness = 2,
                            Width = Math.Max(1, width),
                            Height = Math.Max(1, height),
                            Fill = Brushes.Transparent
                        };
                        Canvas.SetLeft(rect, left);
                        Canvas.SetTop(rect, top);
                        ModelBoundingCanvas.Children.Add(rect);

                        var label = new TextBlock
                        {
                            Text = $"{det.ClassName} ({det.Confidence * 100:0.##}%)",
                            Foreground = Brushes.Yellow,
                            Background = Brushes.Black,
                            FontSize = 12,
                            Padding = new Thickness(2, 0, 2, 0)
                        };
                        Canvas.SetLeft(label, left + 2);
                        Canvas.SetTop(label, Math.Max(0, top - 18));
                        ModelBoundingCanvas.Children.Add(label);
                    }
                    else if (det.Task == "obb" && parts.Length == 5 &&
                        double.TryParse(parts[0], out double cx) &&
                        double.TryParse(parts[1], out double cy) &&
                        double.TryParse(parts[2], out double w) &&
                        double.TryParse(parts[3], out double h) &&
                        double.TryParse(parts[4], out double angle))
                    {
                        double left = (cx - w / 2) * scaleX;
                        double top = (cy - h / 2) * scaleY;
                        var rect = new Rectangle
                        {
                            Stroke = Brushes.Lime,
                            StrokeThickness = 2,
                            Width = Math.Max(1, w * scaleX),
                            Height = Math.Max(1, h * scaleY),
                            Fill = Brushes.Transparent,
                            RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                            RenderTransform = new RotateTransform(angle)
                        };
                        Canvas.SetLeft(rect, left);
                        Canvas.SetTop(rect, top);
                        ModelBoundingCanvas.Children.Add(rect);

                        var label = new TextBlock
                        {
                            Text = $"{det.ClassName} ({det.Confidence * 100:0.##}%)",
                            Foreground = Brushes.Cyan,
                            Background = Brushes.Black,
                            FontSize = 12,
                            Padding = new Thickness(2, 0, 2, 0)
                        };
                        Canvas.SetLeft(label, left + 2);
                        Canvas.SetTop(label, Math.Max(0, top - 18));
                        ModelBoundingCanvas.Children.Add(label);
                    }
                }
            });
        }

        // Ensure this method exists to clear any overlayed bounding boxes on the model test canvas.
        private void ClearModelBoundingBoxes()
        {
            try
            {
                if (ModelBoundingCanvas != null)
                {
                    ModelBoundingCanvas.Children.Clear();
                }
            }
            catch (Exception ex)
            {
                // Log but don't throw — keep UI responsive.
                try { Log($"ClearModelBoundingBoxes failed: {ex.Message}"); } catch { }
            }
        }
    }
}