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
using VisionAICam; // For AppSettings and SettingsManager
using Python.Runtime; // Requires pythonnet NuGet package

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
        private readonly ObservableCollection<DetectionResult> _detections = new();

        public Production()
        {
            InitializeComponent();
            DetectionListView.ItemsSource = _detections;
        }

        public void StartProduction()
        {
            if (_isRunning) return;

            _isRunning = true;
            _isPaused = false;
            StatusTextBlock.Text = "Production started";

            // Show the loading overlay
            LoadingOverlay.Visibility = Visibility.Visible;

            // Load settings
            _appSettings = SettingsManager.Load();
            int cameraIndex = _appSettings?.CameraIndex ?? 0;

            // Open camera
            _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                StatusTextBlock.Text = "Could not open camera.";
                LoadingOverlay.Visibility = Visibility.Collapsed;
                _isRunning = false;
                return;
            }

            // Optionally set camera properties from settings
            if (_appSettings != null)
            {
                _capture.Set(VideoCaptureProperties.Brightness, _appSettings.Brightness);
                _capture.Set(VideoCaptureProperties.Contrast, _appSettings.Contrast);
                _capture.Set(VideoCaptureProperties.Exposure, _appSettings.Exposure);
            }

            // Start camera/model thread
            _cameraThread = new Thread(CameraLoop)
            {
                IsBackground = true
            };
            _cameraThread.Start();
        }

        public void StopProduction()
        {
            if (!_isRunning) return;

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
            _detections.Clear();
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

        private void CameraLoop()
        {
            string pythonDllPath = @"C:\Program Files\Python313\python313.dll";

            if (!File.Exists(pythonDllPath))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = $"Python DLL not found: {pythonDllPath}";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });
                return;
            }

            Python.Runtime.Runtime.PythonDLL = pythonDllPath;
            Dispatcher.BeginInvoke(() => StatusTextBlock.Text = $"Using Python DLL: {Python.Runtime.Runtime.PythonDLL}");
            PythonEngine.Initialize();

            try
            {
                using var mat = new Mat();
                // Show progress message and clear image
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = "Loading model and running first inference...";
                    ProductionImage.Source = null;
                    _detections.Clear();
                    ClearBoundingBoxes();
                });

                // Wait for the first valid frame
                while (_isRunning && _capture != null && _capture.IsOpened())
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    _capture.Read(mat);
                    if (!mat.Empty())
                        break;
                    Thread.Sleep(30);
                }

                // Run first inference (blocking)
                var firstDetections = GetDetectionsFromPython(mat);

                Dispatcher.BeginInvoke(() =>
                {
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Production started";
                    var bitmapSource = mat.ToBitmapSource();
                    bitmapSource.Freeze();
                    ProductionImage.Source = bitmapSource;
                    _detections.Clear();
                    foreach (var d in firstDetections)
                        _detections.Add(d);
                    DrawBoundingBoxes(firstDetections);
                });

                // Main loop: show image and update detections as usual
                while (_isRunning && _capture != null && _capture.IsOpened())
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    _capture.Read(mat);
                    if (!mat.Empty())
                    {
                        var bitmapSource = mat.ToBitmapSource();
                        bitmapSource.Freeze();

                        // All Python.NET code runs on this thread
                        var detections = GetDetectionsFromPython(mat);

                        // UI update on dispatcher
                        Dispatcher.BeginInvoke(() =>
                        {
                            ProductionImage.Source = bitmapSource;
                            _detections.Clear();
                            foreach (var d in detections)
                                _detections.Add(d);

                            DrawBoundingBoxes(detections);

                            FpsTextBlock.Text = "FPS: 30";
                            InferenceTimeTextBlock.Text = "Inference: ~";
                        });
                    }
                    Thread.Sleep(30); // ~30 FPS
                }
            }
            finally
            {
                PythonEngine.Shutdown(); // <-- Shutdown Python.NET on this thread
            }
        }

        private DetectionResult[] GetDetectionsFromPython(Mat mat)
        {
            try
            {
                // Encode Mat to JPEG bytes
                Cv2.ImEncode(".jpg", mat, out var buf);

                using (Py.GIL())
                {
                    string pythonScriptDir = AppDomain.CurrentDomain.BaseDirectory;
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
                    if (!pathExists)
                        sys.path.append(pythonScriptDir);

                    dynamic inference = Py.Import("inference");

                    // Get model path from settings, fallback to "model.pt"
                    string modelPath = _appSettings?.DefaultModelPath ?? "model.pt";

                    // Pass both image bytes and model path to detect
                    dynamic results = inference.detect(buf, modelPath);

                    var detections = new Collection<DetectionResult>();
                    foreach (dynamic det in results)
                    {
                        var box = det["box"];
                        if (box != null && box.Length() == 4)
                        {
                            detections.Add(new DetectionResult
                            {
                                ClassName = det["class"].ToString(),
                                Confidence = (double)det["confidence"],
                                Box = $"{box[0]},{box[1]},{box[2]},{box[3]}"
                            });
                        }
                    }
                    return detections.ToArray();
                }
            }
            catch (Exception ex)
            {
                // Write full error details to a log file for easy copy/paste
                string logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "python_error.log");
                File.WriteAllText(logPath, ex.ToString());

                // Show a short message in the UI
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
                        Height = Math.Abs(y2 - y1)
                    };
                    Canvas.SetLeft(rect, x1);
                    Canvas.SetTop(rect, y1);
                    BoundingBoxCanvas.Children.Add(rect);

                    var label = new TextBlock
                    {
                        Text = $"{det.ClassName} ({det.Confidence:P0})",
                        Foreground = Brushes.Yellow,
                        Background = Brushes.Black,
                        FontSize = 12
                    };
                    Canvas.SetLeft(label, x1);
                    Canvas.SetTop(label, y1 - 18);
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
            // TODO: Implement snapshot logic
        }
    }
}
