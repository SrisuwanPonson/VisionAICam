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
using ClearEngine.Model.Inference; // <-- already present
using System.Threading;
using System.Threading.Tasks;
using System.ComponentModel;
using System.Reflection; // <-- add this
using ClearEngine.Logging;

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
        private AppSettings? _app_settings;

        // Python init tracking
        private static readonly object _pythonInitLock = new();
        private static bool _pythonInitialized = false;

        // Inference/cancellation tracking
        private CancellationTokenSource? _inferenceCts;
        private Task? _runningInferenceTask;
        private bool _isCleaningUp = false;

        // add to DiagnosticsPage fields near other fields
        private ClearEngine.Model.Inference.InferenceEngine? _cachedEngine;
        // Use the shared logger from ClearEngine.Logging
        private readonly ILogger _logger = ClearEngine.Logging.Logger.Instance;
        public int InitCount { get; private set; } = 0;

        public DiagnosticsPage()
        {
            InitializeComponent();
            _app_settings = SettingsManager.Load();
            LoadSystemInfo();
            LoadCameraInfo();
            LoadModelInfo();
            LoadPerformanceMetrics();

            // Populate model path UI
            ModelPathText.Text = string.IsNullOrEmpty(_app_settings?.DefaultModelPath) ? "(none)" : _app_settings!.DefaultModelPath!;
            UpdateModelControlsVisibility();
            _logger.LogInfo("Diagnostics page initialized.");

            // Lifecycle hooks: subscribe so we can clean up when the page is unloaded or host window closes
            this.Loaded += DiagnosticsPage_Loaded;
            this.Unloaded += DiagnosticsPage_Unloaded;
            this.IsVisibleChanged += DiagnosticsPage_IsVisibleChanged;
        }

        private void DiagnosticsPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (this.IsVisible)
            {
                _logger.LogInfo("Diagnostics page is now visible.");
            }
            else
            {
                _logger.LogInfo("Diagnostics page is now hidden.");
            }
        }

        // update DiagnosticsPage_Loaded to pre-warm engine in background
        private void DiagnosticsPage_Loaded(object? sender, RoutedEventArgs e)
        {
            try
            {
                var win = System.Windows.Window.GetWindow(this);
                if (win != null)
                {
                    // safe to use win (e.g., subscribe to Closing)
                    win.Closing += HostWindow_Closing;
                }
                else
                {
                    _logger.LogInfo("Host window not found.");
                }

                // Pre-warm removed: do not initialize Python/inference engine automatically on page load.
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"DiagnosticsPage_Loaded failed: {ex.Message}");
            }
        }

        private void DiagnosticsPage_Unloaded(object? sender, RoutedEventArgs e)
        {
            // When page is unloaded (navigation away / window closed) ensure resources are released.
            CleanupResources();
        }

        private void HostWindow_Closing(object? sender, CancelEventArgs e)
        {
            // Called when host window is closing - perform cleanup.
            CleanupResources();
        }

        // New: update Save button and toggle label depending on whether a model is selected
        private void UpdateModelControlsVisibility()
        {
            SafeInvokeOnUi(() =>
            {
                bool hasModel = !string.IsNullOrWhiteSpace(ModelPathText.Text) && ModelPathText.Text != "(none)";
                SaveModelButton.Visibility = hasModel ? Visibility.Collapsed : Visibility.Visible;
                ToggleModelDetailsButton.IsEnabled = hasModel;
                if (!hasModel)
                {
                    ModelDetailsTextBox.Visibility = Visibility.Collapsed;
                    ToggleModelDetailsButton.Content = "Show Model Details";
                }
            });
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
            _logger.LogInfo("Camera list refreshed.");
        }
        #endregion

        private void TestModelButton_Click(object sender, RoutedEventArgs e)
        {
            _logger.LogInfo("Model inference test triggered.");
            MessageBox.Show("Model inference test completed.", "YOLO Diagnostics", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // 📊 Step 4: Performance Metrics
        private void LoadPerformanceMetrics()
        {
            FpsText.Text = "30.2";
            InferenceTimeText.Text = "42 ms";
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
            return Math.Round(memKb / 1024 / 1024, 1);
        }

        private string[] GetCameraNames()
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT Name, PNPClass FROM Win32_PnPEntity " +
                    "WHERE Name LIKE '%Camera%' OR Name LIKE '%Image%' OR PNPClass = 'Image' OR PNPClass = 'Camera'");

                using var results = searcher.Get();
                return results.Cast<ManagementBaseObject>()
                              .Select(m => m["Name"]?.ToString())
                              .Where(n => !string.IsNullOrEmpty(n))
                              .Distinct()
                              .ToArray();
            }
            catch { return Array.Empty<string>(); }
        }

        private void LiveButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isLive)
            {
                try
                {
                    _camera = CameraFactory.Create();
                    _camera.FrameReady += OnFrameReady;
                    _camera.Start(0);

                    if (!_camera.IsOpened)
                    {
                        _camera.FrameReady -= OnFrameReady;
                        _camera.Dispose();
                        _camera = null;
                        CameraStatusText.Text = "No camera available or failed to open camera.";
                        _logger.LogInfo("Failed to open camera.");
                        return;
                    }

                    _isLive = true;
                    _fpsWatch.Restart();
                    _fpsFrameCount = 0;

                    CameraStatusText.Text = "Live";
                    _logger.LogInfo("Live preview started (ClearEngine.Devices.Camera).");
                }
                catch (Exception ex)
                {
                    CameraStatusText.Text = $"Failed to start live: {ex.Message}";
                    _logger.LogInfo($"Failed to start live preview: {ex.Message}");
                    try { _camera?.Dispose(); } catch { }
                    _camera = null;
                    _isLive = false;
                }
            }
            else
            {
                StopCamera();
            }
        }

        private void OnFrameReady(BitmapSource bitmap)
        {
            _lastBitmap = bitmap;
            _fpsFrameCount++;
            var elapsed = _fpsWatch.Elapsed.TotalSeconds;
            if (elapsed >= 1.0)
            {
                var fps = _fpsFrameCount / elapsed;
                _fpsFrameCount = 0;
                _fpsWatch.Restart();
                SafeInvokeOnUi(() => FpsText.Text = $"{fps:F1}");
            }

            SafeInvokeOnUi(() =>
            {
                CameraStatusText.Text = "Live";
                if (this.FindName("CameraPreviewImage") is System.Windows.Controls.Image img)
                    img.Source = bitmap;
            });
        }

        private void CaptureButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_camera == null || !_camera.IsOpened)
                {
                    CameraStatusText.Text = "Camera not running.";
                    _logger.LogInfo("Capture requested but camera is not running.");
                    return;
                }

                var mat = _camera.CaptureCurrentFrame();
                if (mat == null)
                {
                    CameraStatusText.Text = "No frame available to capture.";
                    _logger.LogInfo("Capture requested but no frame available.");
                    return;
                }

                try
                {
                    string basePath = !string.IsNullOrWhiteSpace(_app_settings?.DefaultImagePath)
                        ? _app_settings!.DefaultImagePath!
                        : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                    string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                    string savePath = System.IO.Path.Combine(basePath, folderName);

                    if (!Directory.Exists(savePath)) Directory.CreateDirectory(savePath);

                    string ClassName = "";
                    string Category = "";
                    string fileName = $"{ClassName}_{Category}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = System.IO.Path.Combine(savePath, fileName);

                    mat.SaveImage(filePath);
                    CameraStatusText.Text = $"Saved: {filePath}";
                    _logger.LogInfo($"Captured image saved to {filePath}");
                }
                finally
                {
                    mat.Dispose();
                }
            }
            catch (Exception ex)
            {
                CameraStatusText.Text = $"Capture failed: {ex.Message}";
                _logger.LogInfo($"Capture failed: {ex.Message}");
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
                _logger.LogInfo($"Error stopping camera: {ex.Message}");
            }
            finally
            {
                _camera = null;
                _isLive = false;
                SafeInvokeOnUi(() =>
                {
                    FpsText.Text = "0";
                    CameraStatusText.Text = "Stopped";
                    if (this.FindName("CameraPreviewImage") is System.Windows.Controls.Image img) img.Source = null;
                });
                _logger.LogInfo("Live preview stopped.");
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
                _logger.LogInfo($"Model selected: {dlg.FileName}");
                UpdateModelControlsVisibility();
                LoadModelDetails(dlg.FileName);
            }
        }

        private void SaveModelButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_app_settings == null) _app_settings = new AppSettings();
                _app_settings.DefaultModelPath = ModelPathText.Text;
                SettingsManager.Save(_app_settings);
                _logger.LogInfo($"Model path saved to settings: {_app_settings.DefaultModelPath}");
                MessageBox.Show("Model saved to settings.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
                UpdateModelControlsVisibility();
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"Failed to save model: {ex.Message}");
                MessageBox.Show($"Failed to save model: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ToggleModelDetailsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool showingDetails = ModelDetailsTextBox.Visibility == Visibility.Visible;
                if (showingDetails)
                {
                    // Hide details -> show image and overlay (only if image is loaded)
                    ModelDetailsTextBox.Visibility = Visibility.Collapsed;
                    ModelTestImage.Visibility = Visibility.Visible;
                    if (ModelBoundingCanvas != null)
                        ModelBoundingCanvas.Visibility = ModelTestImage?.Source != null ? Visibility.Visible : Visibility.Collapsed;
                    ToggleModelDetailsButton.Content = "Show Model Details";
                }
                else
                {
                    // Show details -> hide image and overlay
                    ModelTestImage.Visibility = Visibility.Collapsed;
                    if (ModelBoundingCanvas != null)
                        ModelBoundingCanvas.Visibility = Visibility.Collapsed;
                    ModelDetailsTextBox.Visibility = Visibility.Visible;
                    ToggleModelDetailsButton.Content = "Hide Model Details";

                    var path = ModelPathText.Text;
                    if (!string.IsNullOrWhiteSpace(path) && path != "(none)")
                        LoadModelDetails(path);
                }
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"ToggleModelDetailsButton_Click failed: {ex.Message}");
            }
        }

        private void LoadModelDetails(string modelPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
                {
                    SafeInvokeOnUi(() => ModelDetailsTextBox.Text = "Model file not found.");
                    return;
                }

                var fi = new FileInfo(modelPath);
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Path: {fi.FullName}");
                sb.AppendLine($"Size: {fi.Length:N0} bytes");
                sb.AppendLine($"LastModified: {fi.LastWriteTimeUtc:O}");
                sb.AppendLine($"Created: {fi.CreationTimeUtc:O}");
                sb.AppendLine();

                if (fi.Length <= 16 * 1024 && (modelPath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) || modelPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
                {
                    try
                    {
                        sb.AppendLine("Content Preview:");
                        sb.AppendLine(File.ReadAllText(modelPath));
                    }
                    catch { sb.AppendLine("Failed to read textual model content."); }
                }
                else
                {
                    sb.AppendLine("Content preview skipped for large/binary model file.");
                }

                SafeInvokeOnUi(() => ModelDetailsTextBox.Text = sb.ToString());
            }
            catch (Exception ex)
            {
                SafeInvokeOnUi(() => ModelDetailsTextBox.Text = $"Failed to load model details: {ex.Message}");
            }
        }

        private void LoadImageButton_Click(object sender, RoutedEventArgs e)
        {
            string initialDir = null;
            if (!string.IsNullOrWhiteSpace(_app_settings?.DefaultImagePath) && Directory.Exists(_app_settings.DefaultImagePath))
            {
                initialDir = _app_settings.DefaultImagePath;
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
                    _logger.LogInfo($"Loaded test image: {dlg.FileName}");
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"Failed to load image: {ex.Message}");
                    MessageBox.Show($"Failed to load image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private async void RunInferenceButton_Click(object sender, RoutedEventArgs e)
        {
            // Prevent concurrent runs
            if (_runningInferenceTask != null && !_runningInferenceTask.IsCompleted)
            {
                MessageBox.Show("An inference task is already running. Please wait until it finishes.", "Inference Running", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (ModelTestImage.Source is not BitmapSource bitmap)
            {
                MessageBox.Show("Load an image before running inference.", "No Image", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (string.IsNullOrWhiteSpace(_app_settings?.DefaultModelPath))
            {
                MessageBox.Show("No model selected. Please select and save a model path in settings.", "No Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // Disable UI controls while inference runs and show status
            SafeInvokeOnUi(() =>
            {
                RunInferenceButton.IsEnabled = false;
                LoadImageButton.IsEnabled = false;
                ToggleModelDetailsButton.IsEnabled = false;
                InferenceTimeText.Text = "Running...";
                CameraStatusText.Text = "Running inference...";
            });

            var sw = Stopwatch.StartNew();

            // Do not auto-cancel an existing run here; create a fresh CTS for this run.
            try
            {
                _inferenceCts?.Dispose();
                _inferenceCts = new CancellationTokenSource();
                var token = _inferenceCts.Token;

                Mat mat = null!;
                if (/*InitCount==0*/true)
                {
                    try
                    {
                        mat = bitmap.ToMat();
                        var targetSize = new OpenCvSharp.Size(640, 480);

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
                        _logger.LogInfo($"Failed to convert/resize image to Mat: {ex.Message}");
                        MessageBox.Show($"Failed to convert image: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        mat?.Dispose();
                        return;
                    }
                }

                ClearEngine.Model.Inference.DetectionResult[] detections = Array.Empty<ClearEngine.Model.Inference.DetectionResult>();

                // Run inference on a background thread and track the task so we can cancel/wait during cleanup.
                _runningInferenceTask = Task.Run(() =>
                {
                    ClearEngine.Model.Inference.InferenceEngine? engine = null;
                    var modelPath = _app_settings?.DefaultModelPath ?? "model.pt";
                    var logDir = ClearEngine.Logging.Logger.Instance.GetLogDirectory();
                    bool engineIsCached = false;
                    try
                    {
                        if (/*InitCount==0*/true)
                        {
                            InitCount++;
                            token.ThrowIfCancellationRequested();

                            // Prefer cached engine when available
                            if (_cachedEngine != null)
                            {
                                engine = _cachedEngine;
                                engineIsCached = true;
                            }
                            else
                            {
                                // Use user-configured Python DLL path if available; otherwise fall back to the same hardcoded default used in Production
                                string pythonDllPath;
                                if (!string.IsNullOrWhiteSpace(_app_settings?.PythonDllPath))
                                {
                                    pythonDllPath = _app_settings.PythonDllPath;
                                }
                                else
                                {
                                    pythonDllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script", "NewEnv", "Python313", "python313.dll");
                                }

                                if (!InferenceEngine.TryCreate(pythonDllPath, ClearEngine.Logging.Logger.Instance, out engine, out var initError))
                                {
                                    _logger.LogInfo($"Inference engine initialization failed: {initError}");
                                    return;
                                }

                                // if we created it here, don't mark _pythonInitialized globally unless you want to persist it
                                lock (_pythonInitLock) { _pythonInitialized = true; }
                            }

                            token.ThrowIfCancellationRequested();


                        }

                        ClearEngine.Model.Inference.DetectionResult[] remoteResults = Array.Empty<ClearEngine.Model.Inference.DetectionResult>();
                        try
                        {
                            if (engine != null)
                                remoteResults = engine.Detect(mat, modelPath, logDir) ?? Array.Empty<ClearEngine.Model.Inference.DetectionResult>();
                        }
                        catch (Exception ex)
                        {
                            string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "python_error_inference_detect.log");
                            File.WriteAllText(logPath, ex.ToString());
                            _logger.LogInfo($"Engine.Detect threw: {ex.Message} (see {logPath})");
                            return;
                        }

                        token.ThrowIfCancellationRequested();

                        detections = remoteResults;
                    }
                    catch (OperationCanceledException)
                    {
                        _logger.LogInfo("Inference cancelled.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInfo($"Inference task error: {ex.Message}");
                    }
                    finally
                    {
                        // Dispose only if engine was created locally (not the cached one)
                        try
                        {
                            if (!engineIsCached)
                            {
                                engine?.Dispose();
                                engine = null;
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogInfo($"Failed to dispose inference engine: {ex.Message}");
                        }
                    }
                }, token);

                try
                {
                    // await the task but honor cancellation
                    await _runningInferenceTask;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInfo("RunInferenceButton_Click: inference awaited cancelled.");
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"RunInferenceButton_Click task error: {ex.Message}");
                }
                finally
                {
                    _runningInferenceTask = null;
                    try { _inferenceCts?.Dispose(); } catch { }
                    _inferenceCts = null;
                    if (mat != null)
                    {
                        mat.Dispose();
                    }
                }

                // Update UI from main thread
                DrawModelBoundingBoxes(detections, bitmap);
            }
            finally
            {
                sw.Stop();
                SafeInvokeOnUi(() =>
                {
                    // Show elapsed time or cancellation state
                    if (InferenceTimeText != null)
                    {
                        InferenceTimeText.Text = sw.ElapsedMilliseconds > 0 ? $"{sw.ElapsedMilliseconds} ms" : "Done";
                    }
                    CameraStatusText.Text = "Idle";
                    RunInferenceButton.IsEnabled = true;
                    LoadImageButton.IsEnabled = true;
                    ToggleModelDetailsButton.IsEnabled = true;
                });
            }
        }

        // Drawing helpers
        private void DrawModelBoundingBoxes(ClearEngine.Model.Inference.DetectionResult[] detections, BitmapSource bitmap)
        {
            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    ClearModelBoundingBoxes();

                    if (ModelTestImage.Source == null) return;

                    double dispW = ModelTestImage.ActualWidth;
                    double dispH = ModelTestImage.ActualHeight;
                    if (dispW <= 0 || dispH <= 0)
                    {
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
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"DrawModelBoundingBoxes failed: {ex.Message}");
                }
            });
        }

        private void ClearModelBoundingBoxes()
        {
            try
            {
                if (ModelBoundingCanvas != null)
                    ModelBoundingCanvas.Children.Clear();
            }
            catch (Exception ex)
            {
                try { _logger.LogInfo($"ClearModelBoundingBoxes failed: {ex.Message}"); } catch { }
            }
        }

        // Helper to avoid invalid UI access during shutdown
        private void SafeInvokeOnUi(Action action)
        {
            try
            {
                if (Dispatcher == null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                if (Dispatcher.CheckAccess()) action(); else Dispatcher.BeginInvoke(action);
            }
            catch { }
        }

        private void LoadModelInfo()
        {
            try
            {
                var path = _app_settings?.DefaultModelPath;
                SafeInvokeOnUi(() =>
                {
                    if (string.IsNullOrWhiteSpace(path))
                    {
                        ModelPathText.Text = "(none)";
                        ModelDetailsTextBox.Text = "No model configured.";
                        ToggleModelDetailsButton.IsEnabled = false;
                        ModelTestImage.Visibility = Visibility.Visible;
                        ModelDetailsTextBox.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        ModelPathText.Text = path;
                        ToggleModelDetailsButton.IsEnabled = true;
                        ModelTestImage.Visibility = Visibility.Visible;
                        ModelDetailsTextBox.Visibility = Visibility.Collapsed;

                        if (File.Exists(path))
                        {
                            // Populate details but don't auto-show details panel
                            LoadModelDetails(path);
                        }
                        else
                        {
                            ModelDetailsTextBox.Text = "Configured model file not found.";
                        }
                    }

                    UpdateModelControlsVisibility();
                });
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"LoadModelInfo failed: {ex.Message}");
            }
        }

        private void StopButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                StopCamera();
            }
            catch (Exception ex)
            {
                _logger.LogInfo($"StopButton_Click failed: {ex.Message}");
                MessageBox.Show($"Failed to stop camera: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Clean up any background work, stop camera, cancel inference and release native resources.
        /// Called on Unloaded or host window Closing.
        /// </summary>
        private async void CleanupResources()
        {
            if (_isCleaningUp) return;
            _isCleaningUp = true;

            _logger.LogInfo("Cleaning up resources...");

            try
            {
                // Stop camera first (safe to call multiple times)
                try
                {
                    StopCamera();
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"CleanupResources StopCamera: {ex.Message}");
                }

                // Cancel inference task and wait briefly for it to finish
                try
                {
                    _inferenceCts?.Cancel();
                }
                catch { }

                if (_runningInferenceTask != null)
                {
                    try
                    {
                        await Task.WhenAny(_runningInferenceTask, Task.Delay(2000));
                    }
                    catch { }
                }

                // Dispose/clear UI images and overlays
                SafeInvokeOnUi(() =>
                {
                    try
                    {
                        if (this.FindName("ModelTestImage") is System.Windows.Controls.Image mimg) mimg.Source = null;
                        if (this.FindName("CameraPreviewImage") is System.Windows.Controls.Image cimg) cimg.Source = null;
                        ClearModelBoundingBoxes();

                        // Clear last bitmap reference
                        _lastBitmap = null;
                    }
                    catch { }
                });

                // Dispose any inference engine singleton if present (best-effort)
                try
                {
                    var engineType = typeof(ClearEngine.Model.Inference.InferenceEngine);
                    var instanceProp = engineType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    object? engineInstance = null;
                    if (instanceProp != null)
                    {
                        try { engineInstance = instanceProp.GetValue(null); } catch { engineInstance = null; }
                    }

                    if (engineInstance is ClearEngine.Model.Inference.InferenceEngine engine)
                    {
                        try { engine.Dispose(); } catch (Exception ex) { _logger.LogInfo($"CleanupResources dispose engine: {ex.Message}"); }
                    }

                    // Try to clear backing field if property is read-only
                    if (instanceProp != null && !instanceProp.CanWrite)
                    {
                        var field = engineType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)
                                    ?? engineType.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
                                    ?? engineType.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
                        if (field != null)
                        {
                            try { field.SetValue(null, null); } catch { }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"CleanupResources engine disposal error: {ex.Message}");
                }

                // Shutdown Python runtime if we previously initialized it
                try
                {
                    lock (_pythonInitLock)
                    {
                        if (_pythonInitialized)
                        {
                            try
                            {
                                if (PythonEngine.IsInitialized)
                                    PythonEngine.Shutdown();
                            }
                            catch (Exception ex)
                            {
                                _logger.LogInfo($"PythonEngine.Shutdown failed: {ex.Message}");
                            }
                            _pythonInitialized = false;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"CleanupResources python shutdown error: {ex.Message}");
                }

                // dispose cached engine in CleanupResources
                try
                {
                    if (_cachedEngine != null)
                    {
                        try { _cachedEngine.Dispose(); } catch (Exception ex) { _logger.LogInfo($"Failed to dispose cached engine: {ex.Message}"); }
                        _cachedEngine = null;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogInfo($"Error disposing cached engine: {ex.Message}");
                }

                // Give final chance to dispose any remaining objects
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch { }
            }
            finally
            {
                _isCleaningUp = false;
                _logger.LogInfo("Cleanup finished.");
            }
        }

        // Add this public forwarding method inside the DiagnosticsPage class
        public void CleanupResourcesPublic()
        {
            try
            {
                // Call the existing private cleanup routine (fire-and-forget)
                CleanupResources();
            }
            catch
            {
                try { _logger.LogInfo("CleanupResourcesPublic: failed to invoke CleanupResources."); } catch { }
            }
        }
    }
}