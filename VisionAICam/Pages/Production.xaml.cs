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
using ClearEngine.Logging; // <- use the new logger library
using ClearEngine.Model.Inference;
using VisionAICam.Core; // <- use MasterController

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
        // declare initError once
        string initError;
        // Use the shared logger from ClearEngine.Logging
        private readonly ILogger _logger = ClearEngine.Logging.Logger.Instance;
        // Add this field inside the Production class (near the other private fields)
        private ClearEngine.Model.Inference.InferenceEngine? _inferenceEngine;

        public Production()
        {
            InitializeComponent();
            InitializeTimer();
        }

        private void InitializeTimer()
        {
            // Use a safe default here. Actual sampling interval will be applied in StartProduction
            int intervalMs = 20;
            _timer = new System.Timers.Timer(intervalMs);
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

            // Prefer settings registered in MasterController; fall back to SettingsManager.Load()
            _appSettings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();

            // Apply sampling interval from settings to the timer if available
            if (_timer != null)
            {
                int intervalMs = _appSettings?.SamplingInterval ?? 20;
                // guard against invalid values
                if (intervalMs <= 0) intervalMs = 20;
                _timer.Interval = intervalMs;
            }

            int cameraIndex = _appSettings?.CameraIndex ?? 0;

            _camera = CameraFactory.Create(CameraBackend.OpenCv);
            // Removed per-frame UI subscription; camera loop will capture frames on timer trigger
            // _camera.FrameReady += OnFrameReady;

            var options = _appSettings != null
                ? new CameraOptions { Brightness = _appSettings.Brightness, Contrast = _appSettings.Contrast, Exposure = _appSettings.Exposure }
                : null;

            _camera.Start(cameraIndex, options);
            if (!_camera.IsOpened)
            {
                StatusTextBlock.Text = "Could not open camera.";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                // _camera.FrameReady -= OnFrameReady;
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
                // Removed per-frame UI unsubscribe since we never subscribe now
                // _camera.FrameReady -= OnFrameReady;
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

            // Read python DLL path from settings if available
            var settings = _appSettings ?? MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
            string pythonDllPath = settings?.PythonDllPath;
            if (string.IsNullOrWhiteSpace(pythonDllPath))
            {
                pythonDllPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script", "NewEnv", "Python313", "python313.dll");
            }

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

            // Let the inference library handle initialization + instance creation
            if (!ClearEngine.Model.Inference.InferenceEngine.TryCreate(pythonDllPath, _logger, out _inferenceEngine, out initError))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = $"Failed to initialize inference: {initError}";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });
                _cameraLoopRunning = false;
                return;
            }

            Dispatcher.BeginInvoke(() => StatusTextBlock.Text = $"Using Python DLL: {Python.Runtime.Runtime.PythonDLL}");
            #region Prewarm
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
            //// run first inference BEFORE disposing mat
            var modelPath = _appSettings?.DefaultModelPath ?? settings?.DefaultModelPath ?? "model.pt";
            var logDir = _logger.GetLogDirectory();
            _inferenceEngine.modelPath = modelPath;
            _inferenceEngine.logDir = logDir;

            _inferenceEngine?.PrewarmFirstFrameAsync();

            // convert to BitmapSource on background thread and freeze BEFORE disposing Mat
            var firstBitmap = firstMat.ToBitmapSource();
            firstBitmap.Freeze();

            Dispatcher.BeginInvoke(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = "Production started";
                ProductionImage.Source = firstBitmap;
                //DrawBoundingBoxes(firstDetections);
            });

            firstMat.Dispose();
            #endregion
            #region New Inference Engine - simplified (initialization moved into TryCreate)
            try
            {
                // Log some runtime info
                string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(() => StatusTextBlock.Text = $"Inference init error: {ex.Message}");
                _cameraLoopRunning = false;
                return;
            }

            try
            {
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
                                // run detection using inference engine
                                var remoteResults = _inferenceEngine.Detect(mat, modelPath, logDir);

                                var mapped = new Collection<DetectionResult>();
                                if (remoteResults != null)
                                {
                                    foreach (var r in remoteResults)
                                    {
                                        mapped.Add(new DetectionResult
                                        {
                                            ClassName = r.ClassName ?? string.Empty,
                                            Confidence = r.Confidence,
                                            Box = r.Box ?? string.Empty,
                                            Task = r.Task ?? string.Empty
                                        });
                                    }
                                }

                                // convert for UI and freeze while still on background thread
                                var bitmapSource = mat.ToBitmapSource();
                                bitmapSource.Freeze();

                                Dispatcher.BeginInvoke(() =>
                                {
                                    ProductionImage.Source = bitmapSource;
                                    DrawBoundingBoxes(mapped);
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
                try
                {
                    // Dispose the InferenceEngine instance rather than directly calling PythonEngine.Shutdown().
                    // InferenceEngine.Dispose() is responsible for shutting down the Python runtime and clearing
                    // the singleton in a safe, thread-locked manner.
                    _inferenceEngine?.Dispose();
                    _inferenceEngine = null;
                }
                catch (Exception ex)
                {
                    try { /* _logger.LogError($"InferenceEngine.Dispose threw: {ex}"); */ } catch { }
                }
                _cameraLoopRunning = false;
            }
            #endregion
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
                    string fileName = $"{ClassName}_{Category}_{DateTime.Now:yyyyMMdd}.png";
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