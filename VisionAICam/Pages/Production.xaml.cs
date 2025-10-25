using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using VisionAICam;
using Python.Runtime;
using System.Diagnostics;
using ClearEngine.Devices.Camera; // use camera class library

namespace VisionAICam.Pages
{
    public class DetectionResult
    {
        public string ClassName { get; set; } = "";
        public double Confidence { get; set; }
        public string Box { get; set; } = ""; // "x1,y1,x2,y2"
        public string Task { get; set; } = ""; // "detect" or "obb"
    }

    public partial class Production : Page
    {
        private bool _isRunning = false;
        private bool _isPaused = false;
        private Thread? _cameraThread;
        private ICamera? _camera;
        private AppSettings? _appSettings;
        private System.Timers.Timer? _timer;
        private bool frameTrigger = false;

        public Production()
        {
            InitializeComponent();
            InitializeTimer();
        }

        private void InitializeTimer()
        {
            _timer = new System.Timers.Timer(20); // Set interval to 20 ms
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Enabled = false; // Start disabled, enable when needed
        }

        private void StartTimer()
        {
            if (_timer != null && !_timer.Enabled)
                _timer.Start();

            Application.Current.Dispatcher.Invoke(() => { /* optional periodic UI work */ });
        }

        private void StopTimer()
        {
            if (_timer != null && _timer.Enabled)
                _timer.Stop();
        }

        private void OnTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished)
                    return;

                dispatcher.Invoke(() =>
                {
                    frameTrigger = true;
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Timer error: {ex.Message}");
            }
        }

        public void StartProduction()
        {
            if (_isRunning) return;

            _isRunning = true;
            _isPaused = false;
            StatusTextBlock.Text = "Production started";
            LoadingOverlay.Visibility = Visibility.Visible;

            _appSettings = SettingsManager.Load();
            int cameraIndex = _appSettings?.CameraIndex ?? 0;

            _camera = CameraFactory.Create(CameraBackend.OpenCv);
            _camera.FrameReady += OnFrameReady;

            var options = _appSettings != null
                ? new CameraOptions { Brightness = _appSettings.Brightness, Contrast = _appSettings.Contrast, Exposure = _appSettings.Exposure }
                : null;

            _camera.Start(cameraIndex, options);
            if (!_camera.IsOpened)
            {
                StatusTextBlock.Text = "Could not open camera.";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _camera.FrameReady -= OnFrameReady;
                _camera.Dispose();
                _camera = null;
                _isRunning = false;
                return;
            }

            _cameraThread = new Thread(CameraLoop) { IsBackground = true };
            _cameraThread.Start();
            StartTimer();
        }

        public void StopProduction()
        {
            if (!_isRunning) return;
            StopTimer();
            _isRunning = false;
            _isPaused = false;
            StatusTextBlock.Text = "Production stopped";
            LoadingOverlay.Visibility = Visibility.Collapsed;

            _cameraThread?.Join();

            if (_camera != null)
            {
                _camera.FrameReady -= OnFrameReady;
                _camera.Stop();
                _camera.Dispose();
                _camera = null;
            }

            _cameraThread = null;
            ProductionImage.Source = null;
            ClearBoundingBoxes();
        }

        public void PauseProduction()
        {
            if (_isRunning && !_isPaused)
            {
                _isPaused = true;
                StatusTextBlock.Text = "Production paused";
            }
        }

        public void ResumeProduction()
        {
            if (_isRunning && _isPaused)
            {
                _isPaused = false;
                StatusTextBlock.Text = "Production resumed";
            }
        }

        public bool IsRunning => _isRunning;
        public bool IsPaused => _isPaused;

        private bool _cameraLoopRunning = false;

        private void CameraLoop()
        {
            if (_cameraLoopRunning)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = "Camera loop is already running.";
                });
                return;
            }

            _cameraLoopRunning = true;

            string pythonDllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script", "NewEnv", "Python313", "python313.dll");

            if (!File.Exists(pythonDllPath))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = $"Python DLL not found: {pythonDllPath}";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });
                _cameraLoopRunning = false;
                return;
            }

            Python.Runtime.Runtime.PythonDLL = pythonDllPath;
            Dispatcher.BeginInvoke(() => StatusTextBlock.Text = $"Using Python DLL: {Python.Runtime.Runtime.PythonDLL}");

            try
            {
                PythonEngine.Initialize();

                // Wait for first valid frame using the camera service
                Mat? firstMat = null;
                while (_isRunning && _camera != null && _camera.IsOpened)
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    var mat = _camera.CaptureCurrentFrame();
                    if (mat != null && !mat.Empty())
                    {
                        firstMat = mat;
                        break;
                    }
                    mat?.Dispose();
                    Thread.Sleep(30);
                }

                if (firstMat == null)
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        StatusTextBlock.Text = "No frames from camera.";
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    });
                    _cameraLoopRunning = false;
                    return;
                }

                // run first inference BEFORE disposing mat
                var firstDetections = GetDetectionsFromPython(firstMat);

                // convert to BitmapSource on background thread and freeze BEFORE disposing Mat
                var firstBitmap = firstMat.ToBitmapSource();
                firstBitmap.Freeze();

                Dispatcher.BeginInvoke(() =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Production started";
                    ProductionImage.Source = firstBitmap;
                    DrawBoundingBoxes(firstDetections);
                });

                firstMat.Dispose();

                // Main loop
                while (_isRunning && _camera != null && _camera.IsOpened)
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    if (this.frameTrigger)
                    {
                        // reset trigger
                        frameTrigger = false;

                        var mat = _camera.CaptureCurrentFrame();
                        if (mat != null && !mat.Empty())
                        {
                            try
                            {
                                // run detection first (uses Mat)
                                var detections = GetDetectionsFromPython(mat);

                                // convert for UI and freeze while still on background thread
                                var bitmapSource = mat.ToBitmapSource();
                                bitmapSource.Freeze();

                                Dispatcher.BeginInvoke(() =>
                                {
                                    ProductionImage.Source = bitmapSource;
                                    DrawBoundingBoxes(detections);
                                    FpsTextBlock.Text = "FPS: 30";
                                    InferenceTimeTextBlock.Text = "Inference: ~";
                                });
                            }
                            finally
                            {
                                // dispose Mat after conversion & freeze
                                mat.Dispose();
                            }
                        }
                        else
                        {
                            mat?.Dispose();
                        }
                    }

                    Thread.Sleep(30);
                }
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = $"Error: {ex.Message}";
                });
            }
            finally
            {
                PythonEngine.Shutdown();
                _cameraLoopRunning = false;
            }
        }

        private void OnFrameReady(BitmapSource bitmap)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;
                ProductionImage.Source = bitmap;
            });
        }

        private DetectionResult[] GetDetectionsFromPython(Mat mat)
        {
            try
            {
                Cv2.ImEncode(".jpg", mat, out var buf);

                using (Py.GIL())
                {
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

                    dynamic inference = Py.Import("inference");
                    string modelPath = _appSettings?.DefaultModelPath ?? "model.pt";
                    string logDir = Logger.Instance.GetLogDirectory();
                    dynamic results = inference.detect(buf, modelPath, logDir);

                    var detections = new Collection<DetectionResult>();
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
                    return detections.ToArray();
                }
            }
            catch (Exception ex)
            {
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error.log");
                File.WriteAllText(logPath, ex.ToString());
                string errorMsg = $"Detection error: {ex.Message} (see python_error.log)";
                Dispatcher.BeginInvoke(() => StatusTextBlock.Text = errorMsg);
            }
            return Array.Empty<DetectionResult>();
        }

        private void DrawBoundingBoxes(IEnumerable<DetectionResult> detections)
        {
            BoundingBoxCanvas.Children.Clear();

            foreach (var det in detections)
            {
                var parts = det.Box.Split(',');

                if (parts.Length == 4 && det.Task == "detect" &&
                    double.TryParse(parts[0], out double x1) &&
                    double.TryParse(parts[1], out double y1) &&
                    double.TryParse(parts[2], out double x2) &&
                    double.TryParse(parts[3], out double y2))
                {
                    var rect = new Rectangle
                    {
                        Stroke = Brushes.Red,
                        StrokeThickness = 2,
                        Width = Math.Abs(x2 - x1),
                        Height = Math.Abs(y2 - y1),
                        Fill = Brushes.Transparent
                    };
                    Canvas.SetLeft(rect, x1);
                    Canvas.SetTop(rect, y1);
                    BoundingBoxCanvas.Children.Add(rect);

                    var label = new TextBlock
                    {
                        Text = $"{det.ClassName} ({det.Confidence * 100:0.##}%)",
                        Foreground = Brushes.Yellow,
                        Background = Brushes.Black,
                        FontSize = 12,
                        Padding = new Thickness(2, 0, 2, 0)
                    };
                    Canvas.SetLeft(label, x1 + 2);
                    Canvas.SetTop(label, y1 - 18);
                    BoundingBoxCanvas.Children.Add(label);
                }
                else if (parts.Length == 5 && det.Task == "obb" &&
                    double.TryParse(parts[0], out double cx) &&
                    double.TryParse(parts[1], out double cy) &&
                    double.TryParse(parts[2], out double w) &&
                    double.TryParse(parts[3], out double h) &&
                    double.TryParse(parts[4], out double angle))
                {
                    var rect = new Rectangle
                    {
                        Stroke = Brushes.Lime,
                        StrokeThickness = 2,
                        Width = w,
                        Height = h,
                        Fill = Brushes.Transparent,
                        RenderTransformOrigin = new System.Windows.Point(0.5, 0.5),
                        RenderTransform = new RotateTransform(angle)
                    };
                    Canvas.SetLeft(rect, cx - w / 2);
                    Canvas.SetTop(rect, cy - h / 2);
                    BoundingBoxCanvas.Children.Add(rect);

                    var label = new TextBlock
                    {
                        Text = $"{det.ClassName} ({det.Confidence * 100:0.##}%)",
                        Foreground = Brushes.Cyan,
                        Background = Brushes.Black,
                        FontSize = 12,
                        Padding = new Thickness(2, 0, 2, 0)
                    };
                    Canvas.SetLeft(label, cx - w / 2 + 2);
                    Canvas.SetTop(label, cy - h / 2 - 18);
                    BoundingBoxCanvas.Children.Add(label);
                }
            }
        }

        private void ClearBoundingBoxes()
        {
            BoundingBoxCanvas.Children.Clear();
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            var mat = _camera?.CaptureCurrentFrame();
            if (mat != null)
            {
                try
                {
                    string basePath = _appSettings?.DefaultImagePath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                    string savePath = System.IO.Path.Combine(basePath, folderName);

                    if (!System.IO.Directory.Exists(savePath))
                        System.IO.Directory.CreateDirectory(savePath);

                    string ClassName = ""; // adapt if you have class/category controls here
                    string Category = "";
                    string fileName = $"{ClassName}_{Category}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = System.IO.Path.Combine(savePath, fileName);

                    mat.SaveImage(filePath);
                    MessageBox.Show($"Snapshot saved to {filePath}.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save snapshot: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    mat.Dispose();
                }
            }
            else
            {
                MessageBox.Show("Camera is not running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}