using ClearEngine.Devices.Camera;
using ClearEngine.Logging;
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
using VisionAICam.Core;
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
        public string ClassId { get; set; } = "";
        public double Confidence { get; set; }
        public string Box { get; set; } = "";
        public string Task { get; set; } = "";
    }

    public class FrameSummary
    {
        public string ClassName { get; set; } = "";
        public int Count { get; set; }
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
        private string? initError;

        private readonly ILogger _logger = ClearEngine.Logging.Logger.Instance;
        private ClearEngine.Model.Inference.InferenceEngine? _inferenceEngine;
        public InferenceEngine? InferenceEngineInstance => _inferenceEngine;

        private MjpegStreamReader? _mjpeg;
        private static readonly HttpClient http = new HttpClient();

        private readonly ObservableCollection<FrameSummary> _perFrameSummary = new();
        private readonly Queue<byte[]> _fifoFrames = new Queue<byte[]>(5);

        private CancellationTokenSource? _productionCancelTokenSource;
        private CancellationToken _productionCancelToken => _productionCancelTokenSource?.Token ?? CancellationToken.None;
        private string? _logDir;
        private VisionAICam.Helpers.MjpegStreamReader? _mjpegReader;
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
            catch { }

            return 0;
        }

        public Production()
        {
            InitializeComponent();
            InitializeTimer();
            InitializeFrameSummary();
            _logDir = _logger.GetLogDirectory();

        }

        private void InitializeFrameSummary()
        {
            try
            {
                var dg = this.FindName("PerFrameSummaryGrid") as DataGrid;
                if (dg != null)
                {
                    dg.ItemsSource = _perFrameSummary;
                }
            }
            catch { }
        }

        private void InitializeTimer()
        {
            int intervalMs = 20;
            _timer = new System.Timers.Timer(intervalMs);
            _timer.Elapsed += OnTimerElapsed;
            _timer.AutoReset = true;
            _timer.Enabled = false;
        }

        private void StartTimer()
        {
            if (_timer != null && !_timer.Enabled)
                _timer.Start();

            Application.Current.Dispatcher.Invoke(() => { });
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

        private readonly SemaphoreSlim _startLock = new SemaphoreSlim(1, 1);

        public async Task StartProduction()
        {
            // Prevent concurrent starts
            if (!await _startLock.WaitAsync(0))
            {
                _logger?.LogWarning("Production start blocked - already starting/running");

                // Show friendly message to user
                await Dispatcher.InvokeAsync(() =>
                {
                    MessageBox.Show(
                        "Production is already starting. Please wait for the camera to initialize.",
                        "Please Wait",
                        MessageBoxButton.OK,
                        MessageBoxImage.Information);
                });

                return;
            }

            try
            {
                if (_isRunning)
                {
                    _logger?.LogWarning("Production start blocked - already running");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        MessageBox.Show(
                            "Production is already running.",
                            "Already Running",
                            MessageBoxButton.OK,
                            MessageBoxImage.Information);
                    });

                    return;
                }

                _isRunning = true;
                _isPaused = false;

                _logger?.LogInfo("=== Production initialization started ===");

                await Dispatcher.InvokeAsync(() =>
                {
                    StatusTextBlock.Text = "Starting production...";
                    LoadingOverlay.Visibility = Visibility.Visible;
                });

                _appSettings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();

                if (_appSettings == null)
                {
                    _logger?.LogError("Failed to load application settings");
                    MessageBox.Show("Failed to load application settings.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _isRunning = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    return;
                }

                int opencvIndex = _appSettings.CameraIndex;
                var backend = _appSettings.CameraBackend;

                _logger?.LogInfo($"Camera Backend: {backend}");
                _logger?.LogInfo($"Camera Index: {opencvIndex}");

                if (_timer != null)
                {
                    int intervalMs = _appSettings.SamplingInterval;
                    if (intervalMs <= 0) intervalMs = 20;
                    _timer.Interval = intervalMs;
                    _logger?.LogInfo($"Timer interval: {intervalMs}ms");
                }

                _perFrameSummary.Clear();
                _productionCancelTokenSource = new CancellationTokenSource();

                // =================================================================
                // HIKVISION BACKEND
                // =================================================================
                if (backend == CameraBackend.Hikvision)
                {
                    _logger?.LogInfo("=== Starting Hikvision backend ===");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusTextBlock.Text = "Initializing Hikvision camera...";
                    });

                    try
                    {
                        string serviceUrl = "http://localhost:5005";
                        int cameraIndex = _appSettings.CameraIndex;

                        _logger?.LogInfo($"Service URL: {serviceUrl}");
                        _logger?.LogInfo($"Camera Index: {cameraIndex}");

                        // 1. List available cameras
                        _logger?.LogInfo("[STEP-1] Listing available cameras...");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusTextBlock.Text = "Connecting to camera service...";
                        });

                        List<HikDevice>? devices = null;
                        try
                        {
                            _logger?.LogInfo($"HTTP GET {serviceUrl}/list");
                            var listJson = await http.GetStringAsync($"{serviceUrl}/list");
                            _logger?.LogInfo($"List response: {listJson}");

                            devices = System.Text.Json.JsonSerializer.Deserialize<List<HikDevice>>(listJson);

                            if (devices == null || devices.Count == 0)
                            {
                                _logger?.LogError("No cameras found in service response");
                                throw new Exception("No Hikvision cameras found. Please connect a camera and restart the service.");
                            }

                            _logger?.LogInfo($"Found {devices.Count} camera(s)");

                            for (int i = 0; i < devices.Count; i++)
                            {
                                _logger?.LogInfo($"[DEVICE-{i}] Type: {devices[i].type}, Name: {devices[i].name}");
                            }
                        }
                        catch (HttpRequestException ex)
                        {
                            _logger?.LogError($"HTTP request failed: {ex.Message}");
                            throw new Exception($"Cannot connect to Hikvision service at {serviceUrl}.\n\nPlease ensure:\n1. Python service is running (python hik_server.py)\n2. Service is listening on port 5005", ex);
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"Failed to parse camera list: {ex.Message}");
                            throw;
                        }

                        // 2. Check if camera needs to be started
                        _logger?.LogInfo("[STEP-2] Checking if camera is already streaming...");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusTextBlock.Text = "Checking camera status...";
                        });

                        bool needsStart = false;
                        try
                        {
                            _logger?.LogInfo($"Testing stream at {serviceUrl}/stream");
                            var testResponse = await http.GetAsync($"{serviceUrl}/stream",
                                HttpCompletionOption.ResponseHeadersRead,
                                new CancellationTokenSource(1000).Token);

                            needsStart = !testResponse.IsSuccessStatusCode;
                            _logger?.LogInfo($"Stream test status: {testResponse.StatusCode}, Needs start: {needsStart}");
                        }
                        catch (Exception testEx)
                        {
                            needsStart = true;
                            _logger?.LogInfo($"Stream not available: {testEx.Message}");
                        }

                        // 3. Start camera if needed
                        if (needsStart)
                        {
                            _logger?.LogInfo("[STEP-3] Camera needs to be started");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                StatusTextBlock.Text = $"Starting camera {cameraIndex}...";
                            });

                            _logger?.LogInfo($"Attempting to start camera index {cameraIndex}");

                            // Stop any existing session
                            try
                            {
                                _logger?.LogInfo($"HTTP GET {serviceUrl}/stop");
                                await http.GetAsync($"{serviceUrl}/stop");
                                _logger?.LogInfo("Previous camera session stopped");
                                await Task.Delay(200);
                            }
                            catch (Exception stopEx)
                            {
                                _logger?.LogWarning($"Stop warning: {stopEx.Message}");
                            }

                            // Start the camera
                            _logger?.LogInfo($"HTTP GET {serviceUrl}/start/{cameraIndex}");
                            var startResponse = await http.GetAsync($"{serviceUrl}/start/{cameraIndex}");
                            var startJson = await startResponse.Content.ReadAsStringAsync();
                            _logger?.LogInfo($"Camera start response: {startJson}");

                            if (!startJson.Contains("\"status\":\"started\"") && !startJson.Contains("started"))
                            {
                                _logger?.LogError($"Camera failed to start: {startJson}");
                                throw new Exception($"Failed to start Hikvision camera {cameraIndex}.\n\nResponse: {startJson}\n\nAvailable cameras: {devices?.Count ?? 0}");
                            }

                            _logger?.LogInfo("Camera started successfully");

                            // Apply camera settings
                            _logger?.LogInfo("[STEP-4] Applying camera settings...");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                StatusTextBlock.Text = "Applying camera settings...";
                            });

                            try
                            {
                                _logger?.LogInfo($"Setting Exposure: {_appSettings.HikExposureTime}");
                                _logger?.LogInfo($"HTTP GET {serviceUrl}/set/exposure/{_appSettings.HikExposureTime}");
                                var expResponse = await http.GetStringAsync($"{serviceUrl}/set/exposure/{_appSettings.HikExposureTime}");
                                _logger?.LogInfo($"Exposure response: {expResponse}");

                                _logger?.LogInfo($"Setting Gain: {_appSettings.HikGain}");
                                _logger?.LogInfo($"HTTP GET {serviceUrl}/set/gain/{_appSettings.HikGain}");
                                var gainResponse = await http.GetStringAsync($"{serviceUrl}/set/gain/{_appSettings.HikGain}");
                                _logger?.LogInfo($"Gain response: {gainResponse}");

                                _logger?.LogInfo($"Setting Gamma: {_appSettings.HikGamma}");
                                _logger?.LogInfo($"HTTP GET {serviceUrl}/set/gamma/{_appSettings.HikGamma}");
                                var gammaResponse = await http.GetStringAsync($"{serviceUrl}/set/gamma/{_appSettings.HikGamma}");
                                _logger?.LogInfo($"Gamma response: {gammaResponse}");

                                _logger?.LogInfo($"Setting BlackLevel: {_appSettings.HikBlackLevel}");
                                _logger?.LogInfo($"HTTP GET {serviceUrl}/set/blacklevel/{_appSettings.HikBlackLevel}");
                                var blackLevelResponse = await http.GetStringAsync($"{serviceUrl}/set/blacklevel/{_appSettings.HikBlackLevel}");
                                _logger?.LogInfo($"BlackLevel response: {blackLevelResponse}");

                                _logger?.LogInfo("All camera parameters applied successfully");
                            }
                            catch (Exception paramEx)
                            {
                                _logger?.LogError($"Failed to set parameters: {paramEx.Message}");
                                _logger?.LogError($"Stack trace: {paramEx.StackTrace}");
                            }

                            _logger?.LogInfo("Waiting 500ms for settings to stabilize...");
                            await Task.Delay(500);
                        }
                        else
                        {
                            _logger?.LogInfo("Camera already started and streaming");
                        }

                        _logger?.LogInfo("[STEP-5] Camera initialization complete");

                        // 4. Initialize Inference Engine
                        _logger?.LogInfo("[STEP-6] Initializing inference engine...");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusTextBlock.Text = "Loading AI model...";
                        });

                        var settings = _appSettings ?? MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                        string pythonDllPath = settings?.PythonDllPath ?? @"C:\ClearEngine\VisionAICam\PythonEnv\Python313\python313.dll";

                        _logger?.LogInfo($"Python DLL path: {pythonDllPath}");

                        if (!System.IO.File.Exists(pythonDllPath))
                        {
                            string errorMsg = $"Python DLL not found: {pythonDllPath}";
                            _logger?.LogError(errorMsg);

                            await Dispatcher.InvokeAsync(() =>
                            {
                                LoadingOverlay.Visibility = Visibility.Collapsed;
                                StatusTextBlock.Text = "Python DLL not found";
                                MessageBox.Show(errorMsg, "Inference Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });

                            return;
                        }

                        _logger?.LogInfo("Creating inference engine...");
                        if (!ClearEngine.Model.Inference.InferenceEngine.TryCreate(
                                pythonDllPath, _logger, out _inferenceEngine, out var tempInitError))
                        {
                            string errorMsg = $"Failed to initialize inference engine: {tempInitError}";
                            _logger?.LogError(errorMsg);

                            await Dispatcher.InvokeAsync(() =>
                            {
                                LoadingOverlay.Visibility = Visibility.Collapsed;
                                StatusTextBlock.Text = "Inference initialization failed";
                                MessageBox.Show(errorMsg, "Inference Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });

                            return;
                        }

                        _logger?.LogInfo("Inference engine created successfully");

                        var modelPath = _appSettings?.DefaultModelPath ?? settings?.DefaultModelPath ?? string.Empty;
                        _logger?.LogInfo($"Model path: {modelPath}");

                        if (!string.IsNullOrWhiteSpace(modelPath))
                        {
                            if (!System.IO.File.Exists(modelPath))
                            {
                                string errorMsg = $"Model file not found: {modelPath}";
                                _logger?.LogError(errorMsg);

                                await Dispatcher.InvokeAsync(() =>
                                {
                                    LoadingOverlay.Visibility = Visibility.Collapsed;
                                    StatusTextBlock.Text = "Model file not found";
                                    MessageBox.Show(errorMsg, "Model Error", MessageBoxButton.OK, MessageBoxImage.Error);
                                });

                                return;
                            }

                            var logDir = _logger?.GetLogDirectory() ?? System.IO.Path.Combine(
                                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                "VisionAICam", "Logs");

                            _logger?.LogInfo($"Log directory: {logDir}");

                            if (_inferenceEngine != null)
                            {
                                _inferenceEngine.modelPath = modelPath;
                                _inferenceEngine.logDir = logDir;
                                _logger?.LogInfo("Inference engine configured with model and log paths");
                            }

                            _logger?.LogInfo("[STEP-7] Warming up AI model...");
                            await Dispatcher.InvokeAsync(() =>
                            {
                                StatusTextBlock.Text = "Warming up AI model...";
                            });

                            try
                            {
                                _logger?.LogInfo("Starting model prewarm...");
                                _inferenceEngine?.PrewarmFirstFrameAsync();
                                _logger?.LogInfo("Model prewarmed successfully");
                            }
                            catch (Exception ex)
                            {
                                _logger?.LogWarning($"Model prewarm warning: {ex.Message}");
                            }
                        }
                        else
                        {
                            _logger?.LogWarning("No model path configured, skipping model load");
                        }

                        // 5. Start MJPEG stream reader
                        _logger?.LogInfo("[STEP-8] Starting video stream...");
                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusTextBlock.Text = "Starting video stream...";
                        });

                        _logger?.LogInfo($"Creating MJPEG reader for {serviceUrl}/stream");
                        _mjpegReader = new VisionAICam.Helpers.MjpegStreamReader();

                        int frameCount = 0;
                        var fpsTimer = System.Diagnostics.Stopwatch.StartNew();
                        BitmapImage? latestFrame = null;
                        object frameLock = new object();
                        bool firstFrameReceived = false;

                        // Start stream reader
                        _logger?.LogInfo("Starting async stream reader...");
                        _ = _mjpegReader.StartAsync($"{serviceUrl}/stream", frame =>
                        {
                            lock (frameLock)
                            {
                                latestFrame = frame;
                                frameCount++;

                                if (!firstFrameReceived)
                                {
                                    firstFrameReceived = true;
                                    _logger?.LogInfo($"✓ FIRST FRAME RECEIVED! (Frame #{frameCount})");

                                    Dispatcher.BeginInvoke(() =>
                                    {
                                        LoadingOverlay.Visibility = Visibility.Collapsed;
                                        StatusTextBlock.Text = "Production running";
                                        _logger?.LogInfo("Loading overlay hidden, production started");
                                    });
                                }
                                else if (frameCount % 100 == 0) // Log every 100 frames
                                {
                                    _logger?.LogInfo($"Received frame #{frameCount}");
                                }
                            }

                            // Display frame on UI thread
                            Dispatcher.BeginInvoke(() =>
                            {
                                try
                                {
                                    ProductionImage.Source = frame;
                                }
                                catch (Exception ex)
                                {
                                    _logger?.LogError($"Display error: {ex.Message}");
                                }
                            });
                        });

                        _logger?.LogInfo("Stream reader started, waiting for first frame...");

                        // Timeout handler for first frame
                        _ = Task.Run(async () =>
                        {
                            _logger?.LogInfo("Starting 10-second timeout watch for first frame...");

                            for (int i = 1; i <= 10; i++)
                            {
                                await Task.Delay(1000);

                                if (firstFrameReceived)
                                {
                                    _logger?.LogInfo($"✓ Frame received after {i} seconds");
                                    return;
                                }

                                _logger?.LogInfo($"Waiting for frame... {i}/10 seconds (Frames received: {frameCount})");
                            }

                            if (!firstFrameReceived)
                            {
                                _logger?.LogError("=== NO FRAMES RECEIVED AFTER 10 SECONDS ===");
                                _logger?.LogError($"Total frames received: {frameCount}");
                                _logger?.LogError($"Stream URL: {serviceUrl}/stream");

                                await Dispatcher.InvokeAsync(() =>
                                {
                                    LoadingOverlay.Visibility = Visibility.Collapsed;
                                    StatusTextBlock.Text = "Waiting for video stream...";

                                    MessageBox.Show(
                                        $"Camera started but no video frames received after 10 seconds.\n\n" +
                                        $"Stream URL: {serviceUrl}/stream\n" +
                                        $"Frames received: {frameCount}\n\n" +
                                        "Please check:\n" +
                                        "1. Hikvision Python service is running (python hik_server.py)\n" +
                                        "2. Camera is properly connected\n" +
                                        "3. Check Python service console for errors\n" +
                                        "4. Try restarting the Python service",
                                        "No Video Stream",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Warning);
                                });
                            }
                        });

                        _logger?.LogInfo("MJPEG stream fully initialized");
                        _logger?.LogInfo("=== Entering production inference loop ===");

                        // 6. Inference loop (processes frames from stream)
                        while (!_productionCancelToken.IsCancellationRequested)
                        {
                            // Your existing inference loop code here
                            await Task.Delay(10); // Prevent tight loop
                        }

                        _logger?.LogInfo("=== Exited production loop ===");

                        // Stop MJPEG stream
                        _logger?.LogInfo("Stopping MJPEG stream...");
                        _mjpegReader?.Stop();
                        _mjpegReader?.Dispose();
                        _mjpegReader = null;
                        _logger?.LogInfo("Stream stopped and disposed");
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError("=== Hikvision backend failed ===");
                        _logger?.LogError($"Exception: {ex.Message}");
                        _logger?.LogError($"Stack trace: {ex.StackTrace}");

                        await Dispatcher.InvokeAsync(() =>
                        {
                            StatusTextBlock.Text = $"Error: {ex.Message}";
                            MessageBox.Show(
                                $"Hikvision Production Error:\n\n{ex.Message}",
                                "Hikvision Error",
                                MessageBoxButton.OK,
                                MessageBoxImage.Error);
                        });
                    }
                    finally
                    {
                        _logger?.LogInfo("Entering cleanup section");

                        // Clean up
                        try
                        {
                            _logger?.LogInfo("Disposing MJPEG reader...");
                            _mjpegReader?.Stop();
                            _mjpegReader?.Dispose();
                            _mjpegReader = null;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"MJPEG cleanup failed: {ex.Message}");
                        }

                        try
                        {
                            _logger?.LogInfo("Disposing inference engine...");
                            _inferenceEngine?.Dispose();
                            _inferenceEngine = null;
                        }
                        catch (Exception ex)
                        {
                            _logger?.LogError($"Inference cleanup failed: {ex.Message}");
                        }

                        await Dispatcher.InvokeAsync(() =>
                        {
                            _isRunning = false;
                            if (!StatusTextBlock.Text.StartsWith("Error"))
                            {
                                StatusTextBlock.Text = "Stopped";
                            }
                            LoadingOverlay.Visibility = Visibility.Collapsed;
                            _logger?.LogInfo("UI cleaned up, production stopped");
                        });

                        _logger?.LogInfo("=== Cleanup complete ===");
                    }

                    return;
                }

                // =================================================================
                // OPENCV BACKEND
                // =================================================================
                _logger?.LogInfo("=== Starting OpenCV backend ===");

                try
                {
                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusTextBlock.Text = "Initializing OpenCV camera...";
                    });

                    _logger?.LogInfo($"Backend = OpenCV");
                    _logger?.LogInfo($"Camera Index = {opencvIndex}");

                    var options = new CameraOptions
                    {
                        Brightness = _appSettings.Brightness,
                        Contrast = _appSettings.Contrast,
                        Exposure = _appSettings.Exposure
                    };

                    _logger?.LogInfo($"Camera options - Brightness: {options.Brightness}, Contrast: {options.Contrast}, Exposure: {options.Exposure}");

                    _camera = CameraFactory.Create(ClearEngine.Devices.Camera.CameraBackend.OpenCv);
                    _logger?.LogInfo("Camera instance created");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusTextBlock.Text = "Opening camera...";
                    });

                    _logger?.LogInfo($"Starting camera at index {opencvIndex}...");
                    _camera.Start(opencvIndex, options);

                    if (!_camera.IsOpened)
                    {
                        _logger?.LogError("Camera failed to open");
                        StatusTextBlock.Text = "Could not open OpenCV camera.";
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        MessageBox.Show(
                            $"Failed to open camera at index {opencvIndex}.\n\nPlease check:\n1. Camera is connected\n2. Camera index is correct\n3. Camera is not in use by another application",
                            "Camera Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                        _camera.Dispose();
                        _camera = null;
                        _isRunning = false;
                        return;
                    }

                    _logger?.LogInfo("Camera opened successfully");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusTextBlock.Text = "Starting video feed...";
                    });

                    _logger?.LogInfo("Starting camera thread...");
                    _cameraThread = new Thread(CameraLoop) { IsBackground = true };
                    _cameraThread.Start();

                    _logger?.LogInfo("Starting timer...");
                    StartTimer();

                    // Wait a moment for first frame
                    _logger?.LogInfo("Waiting 500ms for first frame...");
                    await Task.Delay(500);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        StatusTextBlock.Text = "Production running";
                    });

                    _logger?.LogInfo("=== OpenCV production started successfully ===");
                }
                catch (Exception ex)
                {
                    _logger?.LogError("=== OpenCV start failed ===");
                    _logger?.LogError($"Exception: {ex.Message}");
                    _logger?.LogError($"Stack trace: {ex.StackTrace}");

                    MessageBox.Show($"OpenCV start failed: {ex.Message}", "OpenCV Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    _isRunning = false;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                }
            }
            finally
            {
                _logger?.LogInfo("Releasing start lock");
                _startLock.Release();
            }
        }

        public void StopProduction()
        {
            Debug.WriteLine("==================================================");
            Debug.WriteLine($"[STOP] ⚠️ StopProduction() called!");
            Debug.WriteLine($"[STOP] Current Status - IsRunning: {_isRunning}, IsPaused: {_isPaused}");
            Debug.WriteLine("==================================================");

            if (!_isRunning)
            {
                Debug.WriteLine("[STOP] ℹ️ Production is not running. Nothing to stop.");
                return;
            }

            try
            {
                Debug.WriteLine("[STOP] 🛑 Initiating production stop sequence...");

                // 1. ส่งสัญญาณ cancel
                if (_productionCancelTokenSource != null && !_productionCancelTokenSource.IsCancellationRequested)
                {
                    Debug.WriteLine("[STOP] 📢 Sending cancellation signal...");
                    _productionCancelTokenSource.Cancel();
                }

                // 2. Update state flags ทันที
                _isRunning = false;
                _isPaused = false;
                Debug.WriteLine("[STOP] ✅ State flags updated");

                // ⭐ 3. CLEAR IMAGE ก่อนอื่นหมด (สำคัญมาก!)
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        Debug.WriteLine("[STOP] 🖼️ Clearing ProductionImage...");

                        // วิธีที่ 1: Set Source เป็น null
                        if (ProductionImage != null)
                        {
                            ProductionImage.Source = null;
                            Debug.WriteLine("[STOP] ✅ ProductionImage.Source = null");
                        }

                        // วิธีที่ 2: Force update layout
                        ProductionImage?.UpdateLayout();
                        Debug.WriteLine("[STOP] ✅ ProductionImage layout updated");

                        // วิธีที่ 3: Clear bounding boxes
                        ClearBoundingBoxes();
                        Debug.WriteLine("[STOP] ✅ Bounding boxes cleared");

                        // วิธีที่ 4: Update UI ทันที
                        StatusTextBlock.Text = "Production stopping...";
                        LoadingOverlay.Visibility = Visibility.Collapsed;

                        // Force render
                        ProductionImage?.InvalidateVisual();
                        Debug.WriteLine("[STOP] ✅ UI cleared and invalidated");
                    }, System.Windows.Threading.DispatcherPriority.Send); // ⭐ ใช้ Send เพื่อบังคับทันที
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STOP] ❌ Error clearing image: {ex.Message}");
                }

                // 4. Stop timer
                Debug.WriteLine("[STOP] ⏱️ Stopping timer...");
                try
                {
                    StopTimer();
                    Debug.WriteLine("[STOP] ✅ Timer stopped");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STOP] ⚠️ Error stopping timer: {ex.Message}");
                }

                // 5. Stop MJPEG reader
                if (_mjpegReader != null)
                {
                    Debug.WriteLine("[STOP] 📹 Stopping MJPEG stream reader...");
                    try
                    {
                        _mjpegReader.Stop();
                        Debug.WriteLine("[STOP] ✅ MJPEG reader stopped");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ❌ Error stopping MJPEG reader: {ex.Message}");
                    }
                    finally
                    {
                        _mjpegReader = null;
                    }
                }

                // 6. Stop Hikvision camera
                if (_appSettings?.CameraBackend == CameraBackend.Hikvision)
                {
                    Debug.WriteLine("[STOP] 📷 Stopping Hikvision camera...");
                    try
                    {
                        using var httpClient = new HttpClient();
                        httpClient.Timeout = TimeSpan.FromSeconds(3);
                        var response = httpClient.GetAsync("http://localhost:5005/stop").Result;
                        Debug.WriteLine($"[STOP] ✅ Hikvision stop response: {response.StatusCode}");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ⚠️ Error stopping Hikvision: {ex.Message}");
                    }
                }

                // 7. Stop camera thread
                if (_cameraThread != null)
                {
                    Debug.WriteLine("[STOP] 🧵 Waiting for camera thread...");
                    try
                    {
                        if (!_cameraThread.Join(TimeSpan.FromSeconds(5)))
                        {
                            Debug.WriteLine("[STOP] ⚠️ Thread timeout, aborting...");
#pragma warning disable SYSLIB0006
                            _cameraThread.Abort();
#pragma warning restore SYSLIB0006
                        }
                        Debug.WriteLine("[STOP] ✅ Camera thread stopped");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ❌ Error with camera thread: {ex.Message}");
                    }
                    finally
                    {
                        _cameraThread = null;
                    }
                }

                // 8. Stop legacy MJPEG
                if (_mjpeg != null)
                {
                    Debug.WriteLine("[STOP] 📹 Stopping legacy MJPEG...");
                    try
                    {
                        _mjpeg.Stop();
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ❌ Error stopping legacy MJPEG: {ex.Message}");
                    }
                    finally
                    {
                        _mjpeg = null;
                    }
                }

                // 9. Dispose OpenCV camera
                if (_camera != null)
                {
                    Debug.WriteLine("[STOP] 📷 Disposing OpenCV camera...");
                    try
                    {
                        _camera.Stop();
                        _camera.Dispose();
                        Debug.WriteLine("[STOP] ✅ OpenCV camera disposed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ❌ Error disposing camera: {ex.Message}");
                    }
                    finally
                    {
                        _camera = null;
                    }
                }

                // ⭐ 10. DOUBLE CHECK - Clear image อีกครั้งหลังจาก stop ทุกอย่าง
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        Debug.WriteLine("[STOP] 🖼️ Double-checking image clear...");

                        if (ProductionImage != null)
                        {
                            // Clear source
                            ProductionImage.Source = null;

                            // Force width/height to trigger redraw
                            var tempWidth = ProductionImage.ActualWidth;
                            var tempHeight = ProductionImage.ActualHeight;
                            ProductionImage.Width = tempWidth;
                            ProductionImage.Height = tempHeight;

                            // Reset to auto
                            ProductionImage.Width = double.NaN;
                            ProductionImage.Height = double.NaN;

                            // Force update
                            ProductionImage.UpdateLayout();
                            ProductionImage.InvalidateVisual();
                        }

                        // Clear overlay canvas (ถ้ามี)
                        var overlayCanvas = this.FindName("BoundingBoxCanvas") as Canvas;
                        if (overlayCanvas != null)
                        {
                            overlayCanvas.Children.Clear();
                            overlayCanvas.UpdateLayout();
                        }

                        StatusTextBlock.Text = "Production stopped";
                        LoadingOverlay.Visibility = Visibility.Collapsed;

                        Debug.WriteLine("[STOP] ✅ Double-check image clear completed");
                    }, System.Windows.Threading.DispatcherPriority.Send);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[STOP] ❌ Error in double-check clear: {ex.Message}");
                }

                // 11. Clear frame summary
                if (_perFrameSummary != null)
                {
                    try
                    {
                        int count = _perFrameSummary.Count;
                        _perFrameSummary.Clear();
                        Debug.WriteLine($"[STOP] 📊 Cleared {count} frame summaries");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ❌ Error clearing summary: {ex.Message}");
                    }
                }

                // 12. Dispose cancellation token
                if (_productionCancelTokenSource != null)
                {
                    try
                    {
                        _productionCancelTokenSource.Dispose();
                        Debug.WriteLine("[STOP] ✅ Token disposed");
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[STOP] ❌ Error disposing token: {ex.Message}");
                    }
                    finally
                    {
                        _productionCancelTokenSource = null;
                    }
                }

                // 13. Force GC
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                    Debug.WriteLine("[STOP] 🧹 GC completed");
                }
                catch { }

                Debug.WriteLine("==================================================");
                Debug.WriteLine("[STOP] ✅✅✅ Production stopped successfully!");
                Debug.WriteLine("[STOP] ImageBox should be clear now!");
                Debug.WriteLine("==================================================");
            }
            catch (Exception ex)
            {
                Debug.WriteLine("==================================================");
                Debug.WriteLine($"[STOP] ❌❌❌ CRITICAL ERROR!");
                Debug.WriteLine($"[STOP] Error: {ex.Message}");
                Debug.WriteLine($"[STOP] Stack: {ex.StackTrace}");
                Debug.WriteLine("==================================================");

                _isRunning = false;
                _isPaused = false;

                // Force clear image แม้จะ error
                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (ProductionImage != null)
                        {
                            ProductionImage.Source = null;
                            ProductionImage.UpdateLayout();
                        }
                        StatusTextBlock.Text = $"Error: {ex.Message}";
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    });
                }
                catch { }
            }
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
            var settings = _appSettings
                           ?? MasterController.Instance.GetService<AppSettings>()
                           ?? SettingsManager.Load();

            // Python DLL path - will use default from AppSettings if not configured
            string pythonDllPath = settings?.PythonDllPath ?? @"C:\ClearEngine\VisionAICam\PythonEnv\Python313\python313.dll";

            Debug.WriteLine($"[PREWARM] Python DLL path: {pythonDllPath}");

            if (!System.IO.File.Exists(pythonDllPath))
            {
                string errorMsg = $"Python DLL not found:\n{pythonDllPath}\n\n";
                errorMsg += "Expected location:\nC:\\ClearEngine\\VisionAICam\\PythonEnv\\Python313\\python313.dll\n\n";
                errorMsg += "Please verify Python 3.13 is installed correctly.";

                RaiseModelAlarm(errorMsg);
                _cameraLoopRunning = false;
                return false;
            }

            Debug.WriteLine($"[PREWARM] Creating inference engine...");

            if (!ClearEngine.Model.Inference.InferenceEngine.TryCreate(
                    pythonDllPath, _logger, out _inferenceEngine, out var tempInitError))
            {
                initError = tempInitError;
                RaiseModelAlarm($"Failed to initialize inference:\n\n{initError}");
                _cameraLoopRunning = false;
                return false;
            }

            Debug.WriteLine($"[PREWARM] Inference engine created");

            var modelPath = _appSettings?.DefaultModelPath
                            ?? settings?.DefaultModelPath
                            ?? string.Empty;

            if (string.IsNullOrWhiteSpace(modelPath))
            {
                Debug.WriteLine($"[PREWARM] No model configured, inference will be skipped");
                return true;
            }

            if (!System.IO.File.Exists(modelPath))
            {
                RaiseModelAlarm($"Model file not found:\n{modelPath}");
                _cameraLoopRunning = false;
                return false;
            }

            Debug.WriteLine($"[PREWARM] Model: {modelPath}");

            var logDir = _logger.GetLogDirectory();
            if (_inferenceEngine != null)
            {
                _inferenceEngine.modelPath = modelPath;
                _inferenceEngine.logDir = logDir;
            }

            try
            {
                Debug.WriteLine("[PREWARM] Prewarming model...");
                _inferenceEngine?.PrewarmFirstFrameAsync();
                Debug.WriteLine("[PREWARM] Model prewarmed");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[PREWARM] Prewarm warning: {ex.Message}");
            }

            Debug.WriteLine("[PREWARM] Ready");
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

                        var bitmap = _camera.CaptureCurrentFrame();
                        if (bitmap != null)
                        {
                            Mat? cvMat = null;

                            try
                            {
                                cvMat = VisionAICam.Helpers.BitmapSourceToMatExtensions.ToMat(bitmap);

                                var remoteResults = InferenceEngineInstance?.Detect(cvMat, modelPath, logDir);

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

                                if (bitmap.CanFreeze)
                                    bitmap.Freeze();

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

                                try
                                {
                                    MasterController.Instance.AddDetectionResults(mapped);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError($"Failed to add detection results: {ex}");
                                }

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

        private SolidColorBrush GetBrushForClass(string className)
        {
            if (string.IsNullOrWhiteSpace(className))
                return Brushes.Red as SolidColorBrush ?? new SolidColorBrush(Colors.Red);

            if (_classBrushes.TryGetValue(className, out var brush))
                return brush ?? new SolidColorBrush(Colors.Red);

            int hash = Math.Abs(className.GetHashCode());
            double hue = hash % 360;

            double satVariant = ((hash >> 8) & 0xFF) / 255.0;
            double valVariant = ((hash >> 16) & 0xFF) / 255.0;

            double saturation = 0.65 + satVariant * 0.25;
            double value = 0.75 + valVariant * 0.20;

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

            // Update MainWindow's DataGrid and statistics
            var mainWindow = Application.Current.MainWindow as MainWindow;
            if (mainWindow != null)
            {
                var grid = mainWindow.FindName("PerFrameSummaryGrid") as System.Windows.Controls.DataGrid;
                if (grid != null)
                {
                    grid.ItemsSource = null;
                    grid.ItemsSource = _perFrameSummary;
                }

                // Update statistics in MainWindow
                int totalDetections = counts.Sum(c => c.Count);
                if (totalDetections > 0)
                {
                    mainWindow.IncrementTotalCount(totalDetections);
                    mainWindow.IncrementSessionCount(totalDetections);
                    mainWindow.IncrementFrameCount(1);
                }
                
                mainWindow.UpdateStatistics();
            }
        }

        private void DrawBoundingBoxes(IEnumerable<DetectionResult> detections)
        {
            try
            {
                BoundingBoxCanvas.Children.Clear();

                if (detections == null)
                {
                    Debug.WriteLine("[DRAW] detections is null");
                    return;
                }

                var detList = detections.ToList();
                Debug.WriteLine($"[DRAW] Drawing {detList.Count} detections");

                if (detList.Count == 0)
                {
                    Debug.WriteLine("[DRAW] No detections to draw");
                    return;
                }

                double imgWidth = ProductionImage.ActualWidth;
                double imgHeight = ProductionImage.ActualHeight;

                Debug.WriteLine($"[DRAW] Image size: {imgWidth}x{imgHeight}");
                Debug.WriteLine($"[DRAW] Canvas size: {BoundingBoxCanvas.ActualWidth}x{BoundingBoxCanvas.ActualHeight}");

                foreach (var det in detList)
                {
                    Debug.WriteLine($"[DRAW] Class: {det.ClassName}, Conf: {det.Confidence:F2}, Box: {det.Box}, Task: {det.Task}");

                    var parts = det.Box.Split(',');
                    var strokeBrush = GetBrushForClass(det.ClassName);
                    Brush labelBrush = strokeBrush;

                    if (parts.Length == 4 && det.Task == "detect")
                    {
                        if (double.TryParse(parts[0], out double x1) &&
                            double.TryParse(parts[1], out double y1) &&
                            double.TryParse(parts[2], out double x2) &&
                            double.TryParse(parts[3], out double y2))
                        {
                            double width = Math.Abs(x2 - x1);
                            double height = Math.Abs(y2 - y1);

                            Debug.WriteLine($"[DRAW] Box position: ({x1}, {y1}) size: {width}x{height}");

                            if (x1 < 0 || y1 < 0 || width <= 0 || height <= 0)
                            {
                                Debug.WriteLine($"[DRAW] Invalid box dimensions, skipping");
                                continue;
                            }

                            var rect = new Rectangle
                            {
                                Stroke = strokeBrush,
                                StrokeThickness = 3,
                                Width = width,
                                Height = height,
                                Fill = Brushes.Transparent
                            };
                            Canvas.SetLeft(rect, x1);
                            Canvas.SetTop(rect, y1);
                            BoundingBoxCanvas.Children.Add(rect);

                            Debug.WriteLine($"[DRAW] Rectangle added at ({x1}, {y1})");

                            var labelText = $"{det.ClassName} {det.Confidence * 100:0.#}%";
                            var label = new TextBlock
                            {
                                Text = labelText,
                                Foreground = Brushes.White,
                                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                                FontSize = 14,
                                FontWeight = FontWeights.Bold,
                                Padding = new Thickness(4, 2, 4, 2)
                            };
                            Canvas.SetLeft(label, x1 + 2);
                            Canvas.SetTop(label, Math.Max(0, y1 - 22));
                            BoundingBoxCanvas.Children.Add(label);

                            Debug.WriteLine($"[DRAW] Label added: {labelText}");
                        }
                        else
                        {
                            Debug.WriteLine($"[DRAW] Failed to parse box coordinates: {det.Box}");
                        }
                    }
                    else if (parts.Length == 5 && det.Task == "obb")
                    {
                        if (double.TryParse(parts[0], out double cx) &&
                            double.TryParse(parts[1], out double cy) &&
                            double.TryParse(parts[2], out double w) &&
                            double.TryParse(parts[3], out double h) &&
                            double.TryParse(parts[4], out double angle))
                        {
                            Debug.WriteLine($"[DRAW] OBB center: ({cx}, {cy}) size: {w}x{h} angle: {angle}°");

                            var rect = new Rectangle
                            {
                                Stroke = strokeBrush,
                                StrokeThickness = 3,
                                Width = w,
                                Height = h,
                                Fill = Brushes.Transparent,
                                RenderTransform = new RotateTransform(angle, w / 2, h / 2)
                            };
                            Canvas.SetLeft(rect, cx - w / 2);
                            Canvas.SetTop(rect, cy - h / 2);
                            BoundingBoxCanvas.Children.Add(rect);

                            var labelText = $"{det.ClassName} {det.Confidence * 100:0.#}%";
                            var label = new TextBlock
                            {
                                Text = labelText,
                                Foreground = Brushes.White,
                                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                                FontSize = 14,
                                FontWeight = FontWeights.Bold,
                                Padding = new Thickness(4, 2, 4, 2)
                            };
                            Canvas.SetLeft(label, cx - w / 2 + 2);
                            Canvas.SetTop(label, Math.Max(0, cy - h / 2 - 22));
                            BoundingBoxCanvas.Children.Add(label);

                            Debug.WriteLine($"[DRAW] OBB rectangle and label added");
                        }
                        else
                        {
                            Debug.WriteLine($"[DRAW] Failed to parse OBB coordinates: {det.Box}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine($"[DRAW] Unknown detection format: parts={parts.Length}, task={det.Task}");
                    }
                }

                Debug.WriteLine($"[DRAW] Total canvas children: {BoundingBoxCanvas.Children.Count}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DRAW-ERROR] {ex.Message}");
                Debug.WriteLine($"[DRAW-ERROR] Stack: {ex.StackTrace}");
                _logger?.LogError($"DrawBoundingBoxes error: {ex}");
            }
        }

        private void ClearBoundingBoxes()
        {
            try
            {
                BoundingBoxCanvas.Children.Clear();
                Debug.WriteLine("[DRAW] Bounding boxes cleared");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DRAW-CLEAR-ERROR] {ex.Message}");
            }
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                BitmapSource? bitmap = ProductionImage.Source as BitmapSource;

                if (bitmap == null)
                {
                    MessageBox.Show("No image to capture. Please start production first.",
                        "Snapshot", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!bitmap.IsFrozen && bitmap.CanFreeze)
                {
                    bitmap.Freeze();
                }

                string basePath = _appSettings?.DefaultImagePath
                                  ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                string savePath = System.IO.Path.Combine(basePath, folderName);

                if (!Directory.Exists(savePath))
                    Directory.CreateDirectory(savePath);

                string backendName = _appSettings?.CameraBackend.ToString() ?? "Unknown";
                string fileName = $"Snapshot_{backendName}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = System.IO.Path.Combine(savePath, fileName);

                bitmap.SaveImage(filePath);

                Debug.WriteLine($"[SNAPSHOT] Saved to {filePath}");
                _logger?.LogInfo($"Snapshot saved: {filePath}");

                MessageBox.Show($"Snapshot saved successfully!\n\nPath: {filePath}",
                    "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[SNAPSHOT-ERROR] {ex.Message}");
                _logger?.LogError($"Snapshot failed: {ex}");

                MessageBox.Show($"Failed to save snapshot:\n\n{ex.Message}",
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>
        /// Saves a snapshot with detection information
        /// </summary>
        private async Task SaveDetectionSnapshot(BitmapSource bitmap, int frameNumber, string detectedClass, int detectionCount)
        {
            await Task.Run(() =>
            {
                try
                {
                    string basePath = _appSettings?.DefaultImagePath
                                      ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);

                    string folderName = System.IO.Path.Combine(
                        $"Production_{DateTime.Now:yyyyMMdd}",
                        detectedClass
                    );
                    string savePath = System.IO.Path.Combine(basePath, folderName);

                    if (!Directory.Exists(savePath))
                        Directory.CreateDirectory(savePath);

                    string fileName = $"{detectedClass}_{detectionCount}obj_{DateTime.Now:HHmmss_fff}.jpg";
                    string filePath = System.IO.Path.Combine(savePath, fileName);

                    if (!bitmap.IsFrozen && bitmap.CanFreeze)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (bitmap.CanFreeze)
                                bitmap.Freeze();
                        });
                    }

                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        JpegBitmapEncoder encoder = new JpegBitmapEncoder
                        {
                            QualityLevel = 90
                        };
                        encoder.Frames.Add(BitmapFrame.Create(bitmap));
                        encoder.Save(fileStream);
                    }

                    Debug.WriteLine($"[SNAPSHOT] Saved detection: {filePath}");
                    _logger?.LogInfo($"Snapshot saved: {detectedClass} ({detectionCount} objects)");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[SNAPSHOT-ERROR] {ex.Message}");
                    _logger?.LogError($"Detection snapshot failed: {ex}");
                }
            });
        }

        private async Task SendFirstDetectionToRobotUsingServiceAsync(string className)
        {
            try
            {
                var robot = MasterController.Instance.GetService<RobotService>() ?? MasterController.Instance.RobotService;
                if (robot == null || !robot.IsConnected) return;

                byte slaveId = 1;

                ushort registerAddress = 10;
                try
                {
                    var settings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                    if (settings != null) registerAddress = settings.RobotRegisterAddress;
                }
                catch { }

                ushort value = MapClassToId(className);
                if (value == 0) return;

                await robot.WriteSingleRegisterAsync(slaveId, registerAddress, value).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"SendFirstDetectionToRobotUsingServiceAsync failed: {ex}"); } catch { }
            }
        }

        private void RaiseModelAlarm(string message, Exception? ex = null)
        {
            try
            {
                var full = ex == null ? message : $"{message}{Environment.NewLine}{ex.Message}";
                _logger.LogError(ex == null ? message : $"{message} | {ex}");
                Dispatcher.BeginInvoke(() =>
                {
                    StatusTextBlock.Text = message;
                    LoadingOverlay.Visibility = Visibility.Collapsed;
                    MessageBox.Show(full, "Model Error", MessageBoxButton.OK, MessageBoxImage.Error);
                });
            }
            catch
            {
            }
        }
    }
}