using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using VisionAICam;
using Python.Runtime;
using System.Diagnostics;

namespace VisionAICam.Pages
{
    public class DetectionResult
    {
        
        public string ClassName { get; set; } = "";
        public double Confidence { get; set; }
        public string Box { get; set; } = ""; // "x1,y1,x2,y2"
        
      
    }

    public partial class Production : Page
    {
        private bool _isRunning = false;
        private bool _isPaused = false;
        private Thread? _cameraThread;
        private VideoCapture? _capture;
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
            _timer = new System.Timers.Timer(20); // Set interval to 10 ms
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Enabled = false; // Start disabled, enable when needed
        }

        // Replace all occurrences of Dispatcher.Invoke with Application.Current.Dispatcher.Invoke
        private void StartTimer()
        {
            if (_timer != null && !_timer.Enabled)
            {
                _timer.Start();
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                // Add logic to execute every 10 ms here
            });
        }

        private void StopTimer()
        {
            if (_timer != null && _timer.Enabled)
            {
                _timer.Stop();
            }
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
                    // Add logic to execute every 10 ms here
                });
            }
            catch (Exception ex)
            {
                // Optional: log the error instead of rethrowing
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

            _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                StatusTextBlock.Text = "Could not open camera.";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _isRunning = false;
                return;
            }

            if (_appSettings != null)
            {
                _capture.Set(VideoCaptureProperties.Brightness, _appSettings.Brightness);
                _capture.Set(VideoCaptureProperties.Contrast, _appSettings.Contrast);
                _capture.Set(VideoCaptureProperties.Exposure, _appSettings.Exposure);
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
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
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
        private TaskType tasktype;

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

            string pythonDllPath = @"C:\Program Files\Python313\python313.dll";

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

                using var mat = new Mat();

                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = "Loading model and running first inference...";
                    ProductionImage.Source = null;
                    ClearBoundingBoxes();
                });

                // Wait for first valid frame
                while (_isRunning && _capture != null && _capture.IsOpened())
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    _capture.Read(mat);
                    if (!mat.Empty()) break;
                    Thread.Sleep(30);
                }

                var firstDetections = GetDetectionsFromPython(mat,out tasktype);

                Dispatcher.BeginInvoke(() =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Production started";
                    var bitmapSource = mat.ToBitmapSource();
                    bitmapSource.Freeze();
                    ProductionImage.Source = bitmapSource;
                    DrawBoundingBoxes(firstDetections,tasktype);
                });

                // Main loop
                while (_isRunning && _capture != null && _capture.IsOpened())
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    if (this.frameTrigger)
                    {
                        _capture.Read(mat);
                        if (!mat.Empty())
                        {
                            var bitmapSource = mat.ToBitmapSource();
                            bitmapSource.Freeze();

                            var detections = GetDetectionsFromPython(mat, out tasktype  
                                );

                            Dispatcher.BeginInvoke(() =>
                            {
                                ProductionImage.Source = bitmapSource;
                                DrawBoundingBoxes(detections,tasktype);
                                FpsTextBlock.Text = "FPS: 30";
                                InferenceTimeTextBlock.Text = "Inference: ~";
                            });
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

        private DetectionResult[] GetDetectionsFromPython(Mat mat, out TaskType taskType, double confidenceThreshold = 0.3)
        {
            taskType = TaskType.Detection;
            try
            {
                Cv2.ImEncode(".jpg", mat, out var buf);

                using (Py.GIL())
                {
                    string pythonScriptDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Script");
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
                    dynamic results = inference.detect(buf, modelPath);

                    var detections = new Collection<DetectionResult>();
                    foreach (dynamic det in results)
                    {
                        var box = det["box"];
                        double confidence = (double)det["confidence"];
                        if (box != null && box.Length() == 4 && confidence >= confidenceThreshold)
                        {
                            detections.Add(new DetectionResult
                            {
                                ClassName = det["class"].ToString(),
                                Confidence = confidence,
                                Box = $"{box[0]},{box[1]},{box[2]},{box[3]}"
                            });
                        }
                    }
                    if (detections.Count > 0)
                    {
                        var boxParts = detections[0].Box.Split(',');
                        int boxCount = boxParts.Length;

                        if (boxCount == 4)
                            taskType = TaskType.Detection;
                        else if (boxCount == 8)
                            taskType = TaskType.Obb;
                        else if (boxCount > 8)
                            taskType = TaskType.Segmentation;
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

        private void DrawBoundingBoxes(IEnumerable<DetectionResult> detections,TaskType taskType)
        {
            BoundingBoxCanvas.Children.Clear();

            if (taskType==TaskType.Detection)
            {
                foreach (var det in detections)
                {
                    var parts = det.Box.Split(',');
                    if (parts.Length == 4 &&
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
                } 
            }
            else if (taskType==TaskType.Obb)
            {
              
            }
        }

        private void ClearBoundingBoxes()
        {
            BoundingBoxCanvas.Children.Clear();
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement snapshot logic
        }

        private void ObbRadio_Checked(object sender, RoutedEventArgs e)
        {

        }

        private void AabbRadio_Checked(object sender, RoutedEventArgs e)
        {

        }

        private void SegmentationRadio_Checked(object sender, RoutedEventArgs e)
        {

        }
    }
}