using ClearEngine.Devices.Camera; // use camera class library
using ClearEngine.Logging; // <- use the new logger library
using ClearEngine.Model.Inference;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using Python.Runtime;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using VisionAICam;
using VisionAICam.Core; // <- use MasterController
using VisionAICam.Helpers;
using VisionAICam.Properties;
using VisionAICam.Services;
using static System.Net.WebRequestMethods;
using static VisionAICam.Pages.CameraPage;

namespace VisionAICam.Pages
{
    public class DetectionResult
    {
        public DateTime Timestamp { get; set; } = DateTime.Now;
        public string ClassName { get; set; } = "";
        public string ClassId { get; set; } = ""; // optional
        public double Confidence { get; set; }
        public string Box { get; set; } = ""; // "x1,y1,x2,y2"
        public string Task { get; set; } = ""; // "detect" or "obb"
    }

    // Per-frame summary DTO
    public class FrameSummary
    {
        public string ClassName { get; set; } = "";
        public int Count { get; set; }

        // Brush used to color the class name in the DataGrid (matches bounding box color)
        public Brush ColorBrush { get; set; } = Brushes.White;
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
        public InferenceEngine? InferenceEngineInstance => _inferenceEngine;
        private MjpegStreamReader _mjpeg;
        private static readonly HttpClient http = new HttpClient();
        // Per-frame summary collection bound to UI DataGrid (cleared and replaced each trigger frame)
        private readonly ObservableCollection<FrameSummary> _perFrameSummary = new();
        private readonly Queue<byte[]> _fifoFrames = new Queue<byte[]>(5);
        private BitmapSource _latestFrame;
        // Brush cache: colors per class name
        // keep a few sensible defaults, others will be generated with high contrast
        private readonly Dictionary<string, SolidColorBrush> _classBrushes = new(StringComparer.OrdinalIgnoreCase)
        {
            ["person"] = Brushes.Red as SolidColorBrush,
            ["car"] = Brushes.Lime as SolidColorBrush,
            ["truck"] = Brushes.Orange as SolidColorBrush,
            ["bicycle"] = Brushes.Cyan as SolidColorBrush,
            ["motorbike"] = Brushes.Magenta as SolidColorBrush,
            ["cat"] = Brushes.Yellow as SolidColorBrush,
            ["dog"] = Brushes.Blue as SolidColorBrush
        };

        // Map class name -> id. Only use user-configured AppSettings.ClassIdMap.
        // If no mapping exists, return 0 (caller will skip sending).
        private static ushort MapClassToId(string? className)
        {
            if (string.IsNullOrWhiteSpace(className)) return 0;

            try
            {
                var settings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                if (settings?.ClassIdMap != null && settings.ClassIdMap.Count > 0)
                {
                    if (settings.ClassIdMap.TryGetValue(className.Trim(), out var uid))
                        return uid;
                }
            }
            catch
            {
                // swallow - if config can't be read we will not send
            }

            // No mapping => do not send
            return 0;
        }

        public Production()
        {
            InitializeComponent();
            InitializeTimer();
            InitializeFrameSummary();
        }

        private void InitializeFrameSummary()
        {
            // If a DataGrid named PerFrameSummaryGrid exists in XAML, bind it to our collection.
            try
            {
                var dg = this.FindName("PerFrameSummaryGrid") as DataGrid;
                if (dg != null)
                {
                    dg.ItemsSource = _perFrameSummary;
                }
            }
            catch { /* non-fatal if UI element not present */ }
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
        public static BitmapSource LoadBitmap(byte[] imageData)
        {
            using var ms = new MemoryStream(imageData);
            BitmapImage bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }

        public async Task StartProduction()
        {
            if (_isRunning) return;

            _isRunning = true;
            _isPaused = false;
            StatusTextBlock.Text = "Production started";
            LoadingOverlay.Visibility = Visibility.Visible;

            _appSettings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();

            int opencvIndex = _appSettings.CameraIndex;
            int hikIndex = _appSettings.HikCameraIndex;
            var backend = _appSettings.CameraBackend;

            // Apply sampling interval
            if (_timer != null)
            {
                int intervalMs = _appSettings?.SamplingInterval ?? 20;
                if (intervalMs <= 0) intervalMs = 20;
                _timer.Interval = intervalMs;
            }

            _perFrameSummary.Clear();

            // ---------------------------------------------------------
            // 🔥 1) HIKVISION BACKEND (STREAMING LOOP)
            // ---------------------------------------------------------
            if (backend == CameraBackend.Hikvision)
            {
                try
                {
                    Debug.WriteLine("[START] Backend = HIKVISION");

                    if (!Prewarm())
                    {
                        Debug.WriteLine("[INIT] Prewarm failed");
                        _isRunning = false;
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        return;
                    }

                    var engine = _inferenceEngine;
                    var modelPath = engine.modelPath;
                    var logDir = engine.logDir;

                    // ---------------------------------------------------------
                    // ROTATING FILE LIST
                    // ---------------------------------------------------------
                    string basePath = @"C:\ClearEngine\VisionAICam\PythonScripts\";
                    string[] files = new string[]
                    {
            basePath + "test1.jpg",
            basePath + "test2.jpg",
            basePath + "test3.jpg",
            basePath + "test4.jpg",
            basePath + "test5.jpg"
                    };

                    int index = 0;
                    _latestFrame = null;

                    // ---------------------------------------------------------
                    // FILE READER LOOP
                    // ---------------------------------------------------------
                    _ = Task.Run(async () =>
                    {
                        while (_isRunning)
                        {
                            string filePath = files[index];

                            try
                            {
                                if (System.IO.File.Exists(filePath))
                                {
                                    byte[] bytes = null;

                                    // SAFE READ (avoid Python write-lock)
                                    for (int i = 0; i < 5; i++)
                                    {
                                        try
                                        {
                                            bytes = System.IO.File.ReadAllBytes(filePath);
                                            break;
                                        }
                                        catch
                                        {
                                            await Task.Delay(5);
                                        }
                                    }

                                    if (bytes != null && bytes.Length > 100)
                                    {
                                        using (var ms = new MemoryStream(bytes))
                                        {
                                            var bmp = new BitmapImage();
                                            bmp.BeginInit();
                                            bmp.CacheOption = BitmapCacheOption.OnLoad;
                                            bmp.StreamSource = ms;
                                            bmp.EndInit();
                                            bmp.Freeze();

                                            _latestFrame = bmp;

                                            Dispatcher.Invoke(() =>
                                            {
                                                ProductionImage.Source = bmp;
                                            });
                                        }
                                    }
                                }
                                else
                                {
                                    Debug.WriteLine("[FILE] Missing: " + filePath);
                                }
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("[FILE-ERROR] " + ex.Message);
                            }

                            // Move to next file
                            index = (index + 1) % files.Length;

                            await Task.Delay(30); // ~33 FPS
                        }
                    });

                    // ---------------------------------------------------------
                    // WAIT FOR FIRST FRAME
                    // ---------------------------------------------------------
                    while (_isRunning && _latestFrame == null)
                    {
                        Debug.WriteLine("[INIT] Waiting for first testX.jpg...");
                        await Task.Delay(50);
                    }

                    // ---------------------------------------------------------
                    // YOLO LOOP (SAFE)
                    // ---------------------------------------------------------
                    _ = Task.Run(async () =>
                    {
                        while (_isRunning)
                        {
                            try
                            {
                                var src = _latestFrame;
                                if (src == null)
                                {
                                    await Task.Delay(10);
                                    continue;
                                }

                                using Mat mat = BitmapSourceToMatExtensions.ToMat(src);
                                if (mat == null || mat.Empty())
                                {
                                    Debug.WriteLine("[PREDICT] Empty Mat");
                                    await Task.Delay(10);
                                    continue;
                                }

                                Debug.WriteLine($"[PREDICT] Mat Size = {mat.Width}x{mat.Height}, Channels = {mat.Channels()}");

                                var raw = await Task.Run(() => engine.Detect(mat, modelPath, logDir));
                                if (raw == null)
                                    continue;

                                var results = raw.Select(r => new DetectionResult
                                {
                                    ClassName = r.ClassName,
                                    Confidence = r.Confidence,
                                    Box = r.Box,
                                    Task = r.Task
                                }).ToList();

                                await Dispatcher.InvokeAsync(() =>
                                {
                                    DrawBoundingBoxes(results);
                                    UpdateFrameSummary(results);
                                });

                                if (results.Count > 0)
                                    _ = SendFirstDetectionToRobotUsingServiceAsync(results[0].ClassName);
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine("[YOLO-ERROR] " + ex.Message);
                            }

                            await Task.Delay(10);
                        }
                    });

                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    StatusTextBlock.Text = "Hikvision production started (ROTATING FILE MODE)";
                    return;
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Hikvision start failed: {ex.Message}");
                    _isRunning = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    return;
                }
            }






















            // ---------------------------------------------------------
            // 🔥 2) OPENCV BACKEND (unchanged)
            // ---------------------------------------------------------
            try
            {
                Debug.WriteLine($"[START] Backend = OpenCV");
                Debug.WriteLine($"[START] OpenCV Index = {opencvIndex}");

                var options = new CameraOptions
                {
                    Brightness = _appSettings.Brightness,
                    Contrast = _appSettings.Contrast,
                    Exposure = _appSettings.Exposure
                };

                _camera = CameraFactory.Create(ClearEngine.Devices.Camera.CameraBackend.OpenCv);
                _camera.Start(opencvIndex, options);

                if (!_camera.IsOpened)
                {
                    StatusTextBlock.Text = "Could not open OpenCV camera.";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    _camera.Dispose();
                    _camera = null;
                    _isRunning = false;
                    return;
                }

                _cameraThread = new Thread(CameraLoop) { IsBackground = true };
                _cameraThread.Start();
                StartTimer();

                LoadingOverlay.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = "OpenCV production started";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"OpenCV start failed: {ex.Message}", "OpenCV", MessageBoxButton.OK, MessageBoxImage.Error);
                _isRunning = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
            }
        }






        public void StopProduction()
        {
            if (!_isRunning) return;

            // Stop flags
            StopTimer();
            _isRunning = false;
            _isPaused = false;

            StatusTextBlock.Text = "Production stopped";
            LoadingOverlay.Visibility = Visibility.Collapsed;

            // Stop OpenCV thread
            _cameraThread?.Join();
            _cameraThread = null;

            // Stop Hikvision camera
            try
            {
                _ = http.GetStringAsync("http://localhost:5005/stop");
                Debug.WriteLine("[HIK-STOP] Camera stopped.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HIK-STOP-ERROR] " + ex.Message);
            }

            // Stop Hikvision MJPEG stream
            try
            {
                _ = http.GetStringAsync("http://localhost:5005/stream/stop");
                Debug.WriteLine("[HIK-STREAM-STOP] Stream stopped.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[HIK-STREAM-STOP-ERROR] " + ex.Message);
            }

            // Stop MJPEG reader
            _mjpeg?.Stop();
            _mjpeg = null;

            // Stop OpenCV camera
            if (_camera != null)
            {
                _camera.Stop();
                _camera.Dispose();
                _camera = null;
            }

            // Clear UI
            ProductionImage.Source = null;
            ClearBoundingBoxes();
            _perFrameSummary.Clear();
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
        public bool Prewarm()
        {
            // Load settings
            var settings = _appSettings
                           ?? MasterController.Instance.GetService<AppSettings>()
                           ?? SettingsManager.Load();

            // Resolve Python DLL path
            string pythonDllPath = settings?.PythonDllPath;
            if (string.IsNullOrWhiteSpace(pythonDllPath))
            {
                pythonDllPath = System.IO.Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Script", "NewEnv", "Python313", "python313.dll"
                );
            }

            // Validate Python DLL exists
            if (!System.IO.File.Exists(pythonDllPath))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = $"Python DLL not found: {pythonDllPath}";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });

                _cameraLoopRunning = false;
                return false;
            }

            // Initialize inference engine
            if (!ClearEngine.Model.Inference.InferenceEngine.TryCreate(
                    pythonDllPath,
                    _logger,
                    out _inferenceEngine,
                    out initError))
            {
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = $"Failed to initialize inference: {initError}";
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                });

                _cameraLoopRunning = false;
                return false;
            }

            // Show Python DLL being used
            Dispatcher.BeginInvoke(() =>
            {
                StatusTextBlock.Text = $"Using Python DLL: {Python.Runtime.Runtime.PythonDLL}";
            });

            // Configure inference engine paths
            var modelPath = _appSettings?.DefaultModelPath
                            ?? settings?.DefaultModelPath
                            ?? "model.pt";

            var logDir = _logger.GetLogDirectory();

            _inferenceEngine.modelPath = modelPath;
            _inferenceEngine.logDir = logDir;

            // Prewarm engine
            try
            {
                _inferenceEngine?.PrewarmFirstFrameAsync();
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"PrewarmFirstFrameAsync threw: {ex}"); } catch { }
            }

            return true;
        }

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

            // Initialize inference engine
            Prewarm();

            Dispatcher.BeginInvoke(() =>
            {
                LoadingOverlay.Visibility = Visibility.Collapsed;
                StatusTextBlock.Text = "Production started";
            });

            var settings = _appSettings ?? MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
            var modelPath = _appSettings?.DefaultModelPath ?? settings?.DefaultModelPath ?? "model.pt";
            var logDir = _logger.GetLogDirectory();

            try
            {
                while (_isRunning && _camera != null && _camera.IsOpened)
                {
                    if (_isPaused)
                    {
                        Thread.Sleep(100);
                        continue;
                    }

                    if (frameTrigger)
                    {
                        frameTrigger = false;

                        // BitmapSource from HikCamera
                        var bitmap = _camera.CaptureCurrentFrame();
                        if (bitmap != null)
                        {
                            Mat cvMat = null;

                            try
                            {
                                // Convert BitmapSource → Mat (use our extension explicitly)
                                cvMat = VisionAICam.Helpers.BitmapSourceToMatExtensions.ToMat(bitmap);

                                // Run inference
                                var remoteResults = InferenceEngineInstance?.Detect(cvMat, modelPath, logDir);

                                // Map results
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

                                // Freeze bitmap for UI thread
                                if (bitmap.CanFreeze)
                                    bitmap.Freeze();

                                // Update UI
                                Dispatcher.BeginInvoke(() =>
                                {
                                    ProductionImage.Source = bitmap;
                                    DrawBoundingBoxes(mapped);
                                    FpsTextBlock.Text = "FPS: 30";
                                    InferenceTimeTextBlock.Text = "Inference: ~";

                                    UpdateFrameSummary(mapped);

                                    if (mapped.Count > 0)
                                    {
                                        var firstClassName = mapped[0].ClassName;
                                        _ = Task.Run(() =>
                                            SendFirstDetectionToRobotUsingServiceAsync(firstClassName)
                                        );
                                    }
                                });

                                // Push results to MasterController
                                try
                                {
                                    MasterController.Instance.AddDetectionResults(mapped);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"Failed to add detection results: {ex}");
                                }

                                // Persist predictions
                                try
                                {
                                    var store = MasterController.Instance.GetService<VisionAICam.Services.PredictionStore>();
                                    if (store != null)
                                        _ = store.ReplaceDetectionResultsDeltaAsync(mapped);
                                    else
                                        _ = VisionAICam.Services.PredictionStore.Instance.ReplaceDetectionResultsDeltaAsync(mapped);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"Failed to persist predictions: {ex}");
                                }
                            }
                            finally
                            {
                                // Dispose only Mat
                                cvMat?.Dispose();
                            }
                        }
                        else
                        {
                            Dispatcher.BeginInvoke(() => _perFrameSummary.Clear());
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
                    _inferenceEngine?.Dispose();
                    _inferenceEngine = null;
                }
                catch { }

                _cameraLoopRunning = false;
            }
        }


        // Improved color generation: produce bright / saturated colors for high contrast.
        private SolidColorBrush GetBrushForClass(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
                return Brushes.Red as SolidColorBrush;

            if (_classBrushes.TryGetValue(className, out var brush))
                return brush;

            // Deterministic hue from hash, ensure positive
            int hash = Math.Abs(className.GetHashCode());
            double hue = hash % 360; // 0..359

            // Slight variation in saturation/value derived from hash to avoid too-similar tones
            double satVariant = ((hash >> 8) & 0xFF) / 255.0; // 0..1
            double valVariant = ((hash >> 16) & 0xFF) / 255.0; // 0..1

            // Choose saturation and value in high range for vivid colors
            double saturation = 0.65 + satVariant * 0.25; // 0.65 .. 0.90
            double value = 0.75 + valVariant * 0.20;      // 0.75 .. 0.95

            var color = HsvToRgb(hue, saturation, value);
            var newBrush = new SolidColorBrush(color);
            newBrush.Freeze();

            lock (_classBrushes)
            {
                if (!_classBrushes.ContainsKey(className))
                    _classBrushes[className] = newBrush;
                else
                    newBrush = _classBrushes[className];
            }

            return newBrush;
        }

        // Helper: convert HSV to Color (H 0-360, S 0-1, V 0-1)
        private static Color HsvToRgb(double h, double s, double v)
        {
            h = h % 360;
            double c = v * s;
            double hh = h / 60.0;
            double x = c * (1 - Math.Abs((hh % 2) - 1));
            double r1 = 0, g1 = 0, b1 = 0;

            if (hh >= 0 && hh < 1) { r1 = c; g1 = x; b1 = 0; }
            else if (hh >= 1 && hh < 2) { r1 = x; g1 = c; b1 = 0; }
            else if (hh >= 2 && hh < 3) { r1 = 0; g1 = c; b1 = x; }
            else if (hh >= 3 && hh < 4) { r1 = 0; g1 = x; b1 = c; }
            else if (hh >= 4 && hh < 5) { r1 = x; g1 = 0; b1 = c; }
            else if (hh >= 5 && hh < 6) { r1 = c; g1 = 0; b1 = x; }

            double m = v - c;
            byte r = (byte)Math.Round((r1 + m) * 255);
            byte g = (byte)Math.Round((g1 + m) * 255);
            byte b = (byte)Math.Round((b1 + m) * 255);

            return Color.FromRgb(r, g, b);
        }

        private void UpdateFrameSummary(IEnumerable<DetectionResult> mapped)
        {
            // Ensure we run on UI thread since we mutate ObservableCollection
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => UpdateFrameSummary(mapped));
                return;
            }

            _perFrameSummary.Clear();

            if (mapped == null) return;

            var counts = mapped
                .Where(d => !string.IsNullOrEmpty(d.ClassName))
                .GroupBy(d => d.ClassName)
                .Select(g => new FrameSummary
                {
                    ClassName = g.Key,
                    Count = g.Count(),
                    ColorBrush = GetBrushForClass(g.Key)
                })
                .OrderByDescending(s => s.Count)
                .ToList();

            foreach (var s in counts)
                _perFrameSummary.Add(s);
        }

        private void DrawBoundingBoxes(IEnumerable<DetectionResult> detections)
        {
            BoundingBoxCanvas.Children.Clear();

            foreach (var det in detections)
            {
                var parts = det.Box.Split(',');

                // choose color per class
                var strokeBrush = GetBrushForClass(det.ClassName);
                Brush labelBrush = strokeBrush;

                if (parts.Length == 4 && det.Task == "detect" &&
                    double.TryParse(parts[0], out double x1) &&
                    double.TryParse(parts[1], out double y1) &&
                    double.TryParse(parts[2], out double x2) &&
                    double.TryParse(parts[3], out double y2))
                {
                    var rect = new Rectangle
                    {
                        Stroke = strokeBrush,
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
                        Foreground = labelBrush,
                        Background = Brushes.Transparent,
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
                        Stroke = strokeBrush,
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
                        Foreground = labelBrush,
                        Background = Brushes.Transparent,
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
            // bitmap = BitmapSource จาก HikCamera
            var bitmap = _camera?.CaptureCurrentFrame();
            if (bitmap != null)
            {
                try
                {
                    string basePath = _appSettings?.DefaultImagePath
                                      ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                    string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                    string savePath = System.IO.Path.Combine(basePath, folderName);

                    if (!Directory.Exists(savePath))
                        Directory.CreateDirectory(savePath);

                    string ClassName = "";
                    string Category = "";
                    string fileName = $"{ClassName}_{Category}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = System.IO.Path.Combine(savePath, fileName);

                    // ใช้ extension SaveImage() ที่เราเขียนไว้ใน BitmapSourceExtensions.cs
                    bitmap.SaveImage(filePath);

                    MessageBox.Show($"Snapshot saved to {filePath}.",
                        "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save snapshot: {ex.Message}",
                        "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Camera is not running.",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }


        // Add this helper method inside Production (non-blocking, swallows errors)
        private async Task SendFirstDetectionToRobotUsingServiceAsync(string className)
        {
            try
            {
                var robot = MasterController.Instance.GetService<RobotService>() ?? MasterController.Instance.RobotService;
                if (robot == null || !robot.IsConnected) return;

                byte slaveId = 1; // adjust if needed

                // Read register address and mapping from settings (fallbacks included)
                ushort registerAddress = 10;
                try
                {
                    var settings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                    if (settings != null) registerAddress = settings.RobotRegisterAddress;
                }
                catch { /* swallow */ }

                ushort value = MapClassToId(className);
                if (value == 0) return; // unknown class, skip

                // write without blocking camera loop (await here because RobotService is async; caller uses Task.Run/_)
                await robot.WriteSingleRegisterAsync(slaveId, registerAddress, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"SendFirstDetectionToRobotUsingServiceAsync failed: {ex}"); } catch { }
                // swallow - do not crash camera loop
            }
        }
    }
}