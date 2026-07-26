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

        public async Task StartProduction()
        {
            if (_isRunning) return;

            _isRunning = true;
            _isPaused = false;
            StatusTextBlock.Text = "Production started";
            LoadingOverlay.Visibility = Visibility.Visible;

            _appSettings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();

            if (_appSettings == null)
            {
                MessageBox.Show("Failed to load application settings.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                _isRunning = false;
                LoadingOverlay.Visibility = Visibility.Collapsed;
                return;
            }

            int opencvIndex = _appSettings.CameraIndex;
            var backend = _appSettings.CameraBackend;

            if (_timer != null)
            {
                int intervalMs = _appSettings.SamplingInterval;
                if (intervalMs <= 0) intervalMs = 20;
                _timer.Interval = intervalMs;
            }

            _perFrameSummary.Clear();
            _productionCancelTokenSource = new CancellationTokenSource();

            // =================================================================
            // HIKVISION BACKEND
            // =================================================================
            if (backend == CameraBackend.Hikvision)
            {
                Debug.WriteLine("[PRODUCTION] Starting Hikvision backend");
                _logger?.LogInfo("Starting Hikvision backend");

                try
                {
                    string serviceUrl = "http://localhost:5005";
                    int cameraIndex = _appSettings.CameraIndex;

                    // 1. List available cameras
                    List<HikDevice>? devices = null;
                    try
                    {
                        var listJson = await http.GetStringAsync($"{serviceUrl}/list");
                        Debug.WriteLine($"[HIK-LIST] {listJson}");
                        devices = System.Text.Json.JsonSerializer.Deserialize<List<HikDevice>>(listJson);

                        if (devices == null || devices.Count == 0)
                        {
                            throw new Exception("No Hikvision cameras found. Please connect a camera and restart the service.");
                        }

                        Debug.WriteLine($"[HIK-LIST] Found {devices.Count} camera(s)");
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new Exception($"Cannot connect to Hikvision service at {serviceUrl}.\n\nPlease ensure:\n1. Python service is running (python hik_server.py)\n2. Service is listening on port 5005", ex);
                    }

                    // 2. Check if camera needs to be started
                    bool needsStart = false;
                    try
                    {
                        // Create a temporary test path for checking camera status
                        string testPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hik_test.jpg");
                        var testResponse = await http.GetAsync($"{serviceUrl}/snapshot_to/{Uri.EscapeDataString(testPath)}",
                            new CancellationTokenSource(1000).Token);
                        needsStart = !testResponse.IsSuccessStatusCode;
                    }
                    catch
                    {
                        needsStart = true;
                    }

                    // 3. Start camera if needed
                    if (needsStart)
                    {
                        Debug.WriteLine($"[HIK] Starting camera index {cameraIndex}");

                        // Stop any existing camera session
                        try
                        {
                            await http.GetAsync($"{serviceUrl}/stop");
                            Debug.WriteLine("[HIK] Stopped previous camera session");
                            await Task.Delay(200);
                        }
                        catch (Exception stopEx)
                        {
                            Debug.WriteLine($"[HIK] Stop warning: {stopEx.Message}");
                        }

                        // Start the camera
                        var startResponse = await http.GetAsync($"{serviceUrl}/start/{cameraIndex}");
                        var startJson = await startResponse.Content.ReadAsStringAsync();
                        Debug.WriteLine($"[HIK] Start response: {startJson}");

                        if (!startJson.Contains("\"status\":\"started\"") && !startJson.Contains("started"))
                        {
                            throw new Exception($"Failed to start Hikvision camera {cameraIndex}.\n\nResponse: {startJson}\n\nAvailable cameras: {devices?.Count ?? 0}");
                        }

                        // Apply camera settings
                        try
                        {
                            await http.GetStringAsync($"{serviceUrl}/set/exposure/{_appSettings.HikExposureTime}");
                            await http.GetStringAsync($"{serviceUrl}/set/gain/{_appSettings.HikGain}");
                            await http.GetStringAsync($"{serviceUrl}/set/gamma/{_appSettings.HikGamma}");
                            await http.GetStringAsync($"{serviceUrl}/set/blacklevel/{_appSettings.HikBlackLevel}");
                            Debug.WriteLine("[HIK] Camera parameters applied");
                        }
                        catch (Exception paramEx)
                        {
                            Debug.WriteLine($"[HIK] Warning: Failed to set parameters: {paramEx.Message}");
                            _logger?.LogWarning($"Could not apply camera parameters: {paramEx.Message}");
                        }

                        // Wait for camera to stabilize
                        await Task.Delay(500);
                    }
                    else
                    {
                        Debug.WriteLine("[HIK] Camera already started");
                    }

                    _logger?.LogInfo("Hikvision camera ready for production");

                    // 3.5 Initialize Inference Engine (CRITICAL - was missing!)
                    Debug.WriteLine("[HIK] Initializing inference engine...");

                    var settings = _appSettings ?? MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                    string pythonDllPath = settings?.PythonDllPath ?? @"C:\ClearEngine\VisionAICam\PythonEnv\Python313\python313.dll";

                    Debug.WriteLine($"[HIK-PREWARM] Python DLL path: {pythonDllPath}");

                    if (!System.IO.File.Exists(pythonDllPath))
                    {
                        string errorMsg = $"Python DLL not found:\n{pythonDllPath}\n\n";
                        errorMsg += "Expected location:\nC:\\ClearEngine\\VisionAICam\\PythonEnv\\Python313\\python313.dll\n\n";
                        errorMsg += "Please verify Python 3.13 is installed correctly.";

                        await Dispatcher.InvokeAsync(() =>
                        {
                            LoadingOverlay.Visibility = Visibility.Collapsed;
                            StatusTextBlock.Text = "Python DLL not found";
                            MessageBox.Show(errorMsg, "Inference Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });

                        return;
                    }

                    Debug.WriteLine($"[HIK-PREWARM] Creating inference engine...");

                    if (!ClearEngine.Model.Inference.InferenceEngine.TryCreate(
                            pythonDllPath, _logger, out _inferenceEngine, out var tempInitError))
                    {
                        string errorMsg = $"Failed to initialize inference engine:\n\n{tempInitError}";
                        _logger?.LogError(errorMsg);

                        await Dispatcher.InvokeAsync(() =>
                        {
                            LoadingOverlay.Visibility = Visibility.Collapsed;
                            StatusTextBlock.Text = "Inference initialization failed";
                            MessageBox.Show(errorMsg, "Inference Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        });

                        return;
                    }

                    Debug.WriteLine($"[HIK-PREWARM] Inference engine created successfully");

                    var modelPath = _appSettings?.DefaultModelPath ?? settings?.DefaultModelPath ?? string.Empty;

                    if (!string.IsNullOrWhiteSpace(modelPath))
                    {
                        if (!System.IO.File.Exists(modelPath))
                        {
                            string errorMsg = $"Model file not found:\n{modelPath}";
                            _logger?.LogError(errorMsg);

                            await Dispatcher.InvokeAsync(() =>
                            {
                                LoadingOverlay.Visibility = Visibility.Collapsed;
                                StatusTextBlock.Text = "Model file not found";
                                MessageBox.Show(errorMsg, "Model Error", MessageBoxButton.OK, MessageBoxImage.Error);
                            });

                            return;
                        }

                        Debug.WriteLine($"[HIK-PREWARM] Model path: {modelPath}");

                        var logDir = _logger.GetLogDirectory();
                        if (_inferenceEngine != null)
                        {
                            _inferenceEngine.modelPath = modelPath;
                            _inferenceEngine.logDir = logDir;
                        }

                        try
                        {
                            Debug.WriteLine("[HIK-PREWARM] Prewarming model...");
                            
                            // Add detailed exception logging
                            try
                            {
                                _inferenceEngine?.PrewarmFirstFrameAsync();
                                Debug.WriteLine("[HIK-PREWARM] Model prewarmed successfully");
                            }
                            catch (Python.Runtime.PythonException pyEx)
                            {
                                Debug.WriteLine($"[HIK-PREWARM] ⚠️ Python exception during prewarm: {pyEx.Message}");
                                Debug.WriteLine($"[HIK-PREWARM] Python type: {pyEx.Type}");
                                
                                // Try to get traceback if available
                                try
                                {
                                    var tb = pyEx.Traceback;
                                    if (tb != null)
                                        Debug.WriteLine($"[HIK-PREWARM] Python traceback: {tb}");
                                }
                                catch { }
                                
                                _logger?.LogWarning($"Python prewarm warning: {pyEx.Message}");
                                
                                // Don't fail - model might still work for actual frames
                                Debug.WriteLine("[HIK-PREWARM] Continuing despite prewarm warning...");
                            }
                            catch (Exception ex)
                            {
                                Debug.WriteLine($"[HIK-PREWARM] ⚠️ Prewarm warning: {ex.Message}");
                                _logger?.LogWarning($"Model prewarm warning: {ex.Message}");
                                
                                // Don't fail - model might still work for actual frames
                                Debug.WriteLine("[HIK-PREWARM] Continuing despite prewarm warning...");
                            }
                        }
                        catch (Exception outerEx)
                        {
                            Debug.WriteLine($"[HIK-PREWARM] Error during prewarm setup: {outerEx.Message}");
                            _logger?.LogError($"Prewarm setup error: {outerEx}");
                        }
                    }
                    else
                    {
                        Debug.WriteLine("[HIK-PREWARM] No model configured, will run in live view mode");
                    }

                    Debug.WriteLine("[HIK] Inference engine ready");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                        StatusTextBlock.Text = "Production running";
                    });

                    Debug.WriteLine("[HIK] ✅ UI updated, preparing to start loop");

                    // 4. Prepare snapshot directory
                    string snapshotDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "VisionAICam_Frames");
                    if (!System.IO.Directory.Exists(snapshotDir))
                    {
                        System.IO.Directory.CreateDirectory(snapshotDir);
                    }
                    Debug.WriteLine($"[HIK] Snapshot directory: {snapshotDir}");

                    Debug.WriteLine($"[HIK] ⚠️ About to check cancellation token...");
                    Debug.WriteLine($"[HIK] Token source null? {_productionCancelTokenSource == null}");
                    Debug.WriteLine($"[HIK] Token cancelled? {_productionCancelToken.IsCancellationRequested}");
                    Debug.WriteLine($"[HIK] _isRunning = {_isRunning}");

                    // 5. Main production loop
                    int frameCount = 0;
                    var fpsTimer = System.Diagnostics.Stopwatch.StartNew();
                    int consecutiveErrors = 0;
                    const int maxConsecutiveErrors = 10;

                    Debug.WriteLine($"[HIK-LOOP] 🚀 Attempting to enter while loop...");

                    while (!_productionCancelToken.IsCancellationRequested)
                    {
                        Debug.WriteLine($"[HIK-LOOP] ✅ INSIDE LOOP - Iteration {frameCount + 1}");
                        
                        try
                        {
                            Debug.WriteLine($"[HIK-LOOP] Creating stopwatch...");
                            var loopStart = System.Diagnostics.Stopwatch.StartNew();

                            Debug.WriteLine($"[HIK-LOOP] Generating frame filename...");
                            // Generate unique filename for this frame
                            string frameFilename = $"frame_{DateTime.Now:yyyyMMdd_HHmmss_fff}.jpg";
                            string framePath = System.IO.Path.Combine(snapshotDir, frameFilename);
                            Debug.WriteLine($"[HIK-LOOP] Frame path: {framePath}");

                            Debug.WriteLine($"[HIK-LOOP] Making HTTP request to snapshot_to...");
                            // Get frame using snapshot_to endpoint
                            var frameResponse = await http.GetAsync(
                                $"{serviceUrl}/snapshot_to/{Uri.EscapeDataString(framePath)}",
                                _productionCancelToken);

                            Debug.WriteLine($"[HIK-LOOP] Response received: {frameResponse.StatusCode}");

                            if (!frameResponse.IsSuccessStatusCode)
                            {
                                Debug.WriteLine($"[HIK] Snapshot failed: {frameResponse.StatusCode}");
                                consecutiveErrors++;

                                if (consecutiveErrors >= maxConsecutiveErrors)
                                {
                                    throw new Exception($"Camera stopped responding after {maxConsecutiveErrors} consecutive failures. The camera may have disconnected.");
                                }

                                await Task.Delay(100, _productionCancelToken);
                                continue;
                            }

                            Debug.WriteLine($"[HIK-LOOP] Checking if file exists...");
                            // Verify file was created
                            if (!System.IO.File.Exists(framePath))
                            {
                                Debug.WriteLine($"[HIK] Frame file not found: {framePath}");
                                consecutiveErrors++;
                                await Task.Delay(100, _productionCancelToken);
                                continue;
                            }

                            Debug.WriteLine($"[HIK-LOOP] Reading file bytes...");
                            // Read the saved image file
                            byte[] frameBytes = await System.IO.File.ReadAllBytesAsync(framePath, _productionCancelToken);

                            Debug.WriteLine($"[HIK-LOOP] ✅ File read: {frameBytes?.Length ?? 0} bytes");

                            if (frameBytes == null || frameBytes.Length == 0)
                            {
                                Debug.WriteLine("[HIK] Empty frame received");
                                consecutiveErrors++;
                                await Task.Delay(100, _productionCancelToken);
                                continue;
                            }

                            // Reset error counter on successful frame
                            consecutiveErrors = 0;
                            frameCount++;

                            Debug.WriteLine($"[HIK-LOOP] Starting Dispatcher.InvokeAsync for bitmap decode...");

                            // Decode to BitmapSource for UI display
                            BitmapSource? bitmapSource = null;
                            await Dispatcher.InvokeAsync(() =>
                            {
                                Debug.WriteLine($"[HIK-LOOP] Inside Dispatcher - decoding bitmap");
                                try
                                {
                                    using var ms = new System.IO.MemoryStream(frameBytes);
                                    var decoder = BitmapDecoder.Create(ms,
                                        BitmapCreateOptions.PreservePixelFormat,
                                        BitmapCacheOption.OnLoad);
                                    bitmapSource = decoder.Frames[0];
                                    bitmapSource.Freeze();
                                    ProductionImage.Source = bitmapSource;
                                    Debug.WriteLine($"[HIK-LOOP] ✅ Bitmap displayed");
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[HIK] Frame decode error: {ex.Message}");
                                }
                            });

                            Debug.WriteLine($"[HIK-LOOP] Returned from Dispatcher.InvokeAsync");

                            // Clean up temporary frame file
                            try
                            {
                                System.IO.File.Delete(framePath);
                            }
                            catch
                            {
                                // Ignore cleanup errors
                            }

                            Debug.WriteLine($"[HIK-LOOP] Checking bitmapSource...");

                            if (bitmapSource == null)
                            {
                                await Task.Delay(100, _productionCancelToken);
                                continue;
                            }

                            Debug.WriteLine($"[HIK-LOOP] Bitmap valid, checking model path...");

                            // 6. Run inference if model is configured and engine is initialized
                            if (!string.IsNullOrEmpty(modelPath) && _inferenceEngine != null)
                            {
                                ClearEngine.Model.Inference.InferenceResponse? inferenceResponse = null;

                                try
                                {
                                    Debug.WriteLine($"[HIK-LOOP] Calling DetectWithError directly (not in Task.Run)");
                                    inferenceResponse = _inferenceEngine.DetectWithError(frameBytes, modelPath, _logDir);
                                    Debug.WriteLine($"[PREDICT] Success: {inferenceResponse?.Success}, Detections: {inferenceResponse?.Detections?.Length ?? 0}");
    
                                    // Log the error details if inference failed
                                    if (inferenceResponse != null && !inferenceResponse.Success && inferenceResponse.Error != null)
                                    {
                                        Debug.WriteLine($"[PREDICT] ❌ Inference failed:");
                                        Debug.WriteLine($"[PREDICT]   Type: {inferenceResponse.Error.ErrorType}");
                                        Debug.WriteLine($"[PREDICT]   Message: {inferenceResponse.Error.Message}");
                                        Debug.WriteLine($"[PREDICT]   Traceback: {inferenceResponse.Error.Traceback}");
                                    }
                                }
                                catch (Python.Runtime.PythonException pyEx)
                                {
                                    Debug.WriteLine($"[PREDICT] ❌ Python Exception: {pyEx.Message}");
                                    Debug.WriteLine($"[PREDICT]   Type: {pyEx.Type}");
                                    
                                    // Try to get traceback safely
                                    try
                                    {
                                        var tb = pyEx.Traceback;
                                        if (tb != null)
                                            Debug.WriteLine($"[PREDICT]   Traceback: {tb}");
                                    }
                                    catch { }
                                    
                                    if (!string.IsNullOrEmpty(pyEx.StackTrace))
                                        Debug.WriteLine($"[PREDICT]   .NET StackTrace: {pyEx.StackTrace}");

                                    _logger?.LogError($"Python inference error: {pyEx.Message} | Type: {pyEx.Type}");

                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        DrawBoundingBoxes(Array.Empty<DetectionResult>());
                                        UpdateFrameSummary(Array.Empty<DetectionResult>());
                                        StatusTextBlock.Text = $"Python error: {pyEx.Type}";
                                    });

                                    await Task.Delay(1000, _productionCancelToken);
                                    continue;
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[PREDICT] ❌ Exception: {ex.GetType().Name}: {ex.Message}");
                                    if (!string.IsNullOrEmpty(ex.StackTrace))
                                        Debug.WriteLine($"[PREDICT]   StackTrace: {ex.StackTrace}");

                                    _logger?.LogError($"Inference error: {ex}");

                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        DrawBoundingBoxes(Array.Empty<DetectionResult>());
                                        UpdateFrameSummary(Array.Empty<DetectionResult>());
                                        StatusTextBlock.Text = $"Inference error: {ex.Message}";
                                    });

                                    await Task.Delay(1000, _productionCancelToken);
                                    continue;
                                }

                                if (inferenceResponse == null || !inferenceResponse.Success)
                                {
                                    if (inferenceResponse?.Error != null)
                                    {
                                        Debug.WriteLine($"[PREDICT] Python error: {inferenceResponse.Error.ErrorType} - {inferenceResponse.Error.Message}");
                                        _logger?.LogError($"Python inference error: {inferenceResponse.Error.ErrorType} - {inferenceResponse.Error.Message}");
                                    }

                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        DrawBoundingBoxes(Array.Empty<DetectionResult>());
                                        UpdateFrameSummary(Array.Empty<DetectionResult>());
                                    });

                                    await Task.Delay(1000, _productionCancelToken);
                                    continue;
                                }

                                var raw = inferenceResponse.Detections;

                                if (raw == null || raw.Length == 0)
                                {
                                    Debug.WriteLine("[PREDICT] No detections");

                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        DrawBoundingBoxes(Array.Empty<DetectionResult>());
                                        UpdateFrameSummary(Array.Empty<DetectionResult>());

                                        if (fpsTimer.Elapsed.TotalSeconds >= 1.0)
                                        {
                                            double fps = frameCount / fpsTimer.Elapsed.TotalSeconds;
                                            StatusTextBlock.Text = $"No detections | FPS: {fps:F1}";
                                            frameCount = 0;
                                            fpsTimer.Restart();
                                        }
                                    });
                                }
                                else
                                {
                                    var mappedDetections = raw.Select(r => new DetectionResult
                                    {
                                        ClassName = r.ClassName ?? string.Empty,
                                        Confidence = r.Confidence,
                                        Box = r.Box ?? string.Empty,
                                        Task = r.Task ?? string.Empty,
                                        Timestamp = DateTime.Now
                                    }).ToArray();

                                    Debug.WriteLine($"[DRAW] Drawing {mappedDetections.Length} detections");

                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        try
                                        {
                                            DrawBoundingBoxes(mappedDetections);
                                            UpdateFrameSummary(mappedDetections);

                                            if (fpsTimer.Elapsed.TotalSeconds >= 1.0)
                                            {
                                                double fps = frameCount / fpsTimer.Elapsed.TotalSeconds;
                                                StatusTextBlock.Text = $"Detections: {mappedDetections.Length} | FPS: {fps:F1}";
                                                frameCount = 0;
                                                fpsTimer.Restart();
                                            }
                                            else
                                            {
                                                StatusTextBlock.Text = $"Detections: {mappedDetections.Length}";
                                            }
                                        }
                                        catch (Exception drawEx)
                                        {
                                            Debug.WriteLine($"[DRAW] Error: {drawEx.Message}");
                                        }
                                    });

                                    // Save detection results
                                    try
                                    {
                                        MasterController.Instance.AddDetectionResults(mappedDetections);

                                        var store = MasterController.Instance.GetService<VisionAICam.Services.PredictionStore>();
                                        if (store != null)
                                            _ = store.ReplaceDetectionResultsDeltaAsync(mappedDetections);
                                        else
                                            _ = VisionAICam.Services.PredictionStore.Instance.ReplaceDetectionResultsDeltaAsync(mappedDetections);
                                    }
                                    catch (Exception storeEx)
                                    {
                                        _logger?.LogError($"Failed to persist predictions: {storeEx}");
                                    }

                                    // Send to robot if configured
                                    if (mappedDetections.Length > 0)
                                    {
                                        var firstClassName = mappedDetections[0].ClassName;
                                        _ = Task.Run(() => SendFirstDetectionToRobotUsingServiceAsync(firstClassName));
                                    }

                                    // Auto-snapshot if enabled
                                    if (_appSettings.EnableAutoSnapshot && bitmapSource != null)
                                    {
                                        try
                                        {
                                            string topClass = mappedDetections.FirstOrDefault()?.ClassName ?? "Unknown";
                                            _ = SaveDetectionSnapshot(bitmapSource, frameCount, topClass, mappedDetections.Length);
                                        }
                                        catch (Exception snapEx)
                                        {
                                            Debug.WriteLine($"[SNAPSHOT] Failed: {snapEx.Message}");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                // No inference - just display frames
                                await Dispatcher.InvokeAsync(() =>
                                {
                                    if (fpsTimer.Elapsed.TotalSeconds >= 1.0)
                                    {
                                        double fps = frameCount / fpsTimer.Elapsed.TotalSeconds;
                                        StatusTextBlock.Text = $"Live view (no model) | FPS: {fps:F1}";
                                        frameCount = 0;
                                        fpsTimer.Restart();
                                    }
                                });
                            }

                            // Frame rate control
                            loopStart.Stop();
                            int targetDelay = Math.Max(1, 33 - (int)loopStart.ElapsedMilliseconds);
                            Debug.WriteLine($"[HIK-LOOP] Delaying for {targetDelay}ms before next iteration");
                            await Task.Delay(targetDelay, _productionCancelToken);
            
                            Debug.WriteLine($"[HIK-LOOP] Completed iteration {frameCount}, continuing to next...");
                        }
                        catch (OperationCanceledException)
                        {
                            Debug.WriteLine("[HIK-LOOP] ⚠️ Operation cancelled");
                            break;
                        }
                        catch (HttpRequestException httpEx)
                        {
                            Debug.WriteLine($"[HIK-LOOP] ❌ HTTP Error: {httpEx.Message}");
                            _logger?.LogError($"Hikvision HTTP error: {httpEx}");

                            consecutiveErrors++;
                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                throw new Exception($"Lost connection to camera service after {maxConsecutiveErrors} attempts. Service may have stopped.", httpEx);
                            }

                            await Task.Delay(1000, _productionCancelToken);
                        }
                        catch (Exception loopEx)
                        {
                            Debug.WriteLine($"[HIK-LOOP] ❌ Unexpected error: {loopEx.GetType().Name}: {loopEx.Message}");
                            Debug.WriteLine($"[HIK-LOOP] Stack trace: {loopEx.StackTrace}");
                            _logger?.LogError($"Hikvision loop error: {loopEx}");

                            consecutiveErrors++;
                            if (consecutiveErrors >= maxConsecutiveErrors)
                            {
                                Debug.WriteLine($"[HIK-LOOP] ❌ Max errors reached, throwing...");
                                throw;
                            }

                            await Task.Delay(1000, _productionCancelToken);
                        }
                    } // ← End of while loop

                    Debug.WriteLine($"[HIK-LOOP] ⚠️ Exited while loop normally");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[PRODUCTION] Hikvision error: {ex.Message}");
                    Debug.WriteLine($"[PRODUCTION] Stack: {ex.StackTrace}");
                    _logger?.LogError($"Hikvision backend error: {ex}");

                    await Dispatcher.InvokeAsync(() =>
                    {
                        StatusTextBlock.Text = $"Error: {ex.Message}";
                        MessageBox.Show(
                            $"Hikvision Production Error:\n\n{ex.Message}\n\n" +
                            $"Troubleshooting:\n" +
                            $"1. Ensure Flask service is running: python hik_server.py\n" +
                            $"2. Verify camera is connected and listed in Camera Page\n" +
                            $"3. Check service is accessible at http://localhost:5005\n" +
                            $"4. Verify Python DLL and model paths are configured\n" +
                            $"5. Review logs for detailed error information",
                            "Hikvision Error",
                            MessageBoxButton.OK,
                            MessageBoxImage.Error);
                    });
                }
                finally
                {
                    // Clean up inference engine
                    try
                    {
                        _inferenceEngine?.Dispose();
                        _inferenceEngine = null;
                        Debug.WriteLine("[HIK] Inference engine disposed");
                    }
                    catch (Exception disposeEx)
                    {
                        Debug.WriteLine($"[HIK] Dispose warning: {disposeEx.Message}");
                    }

                    await Dispatcher.InvokeAsync(() =>
                    {
                        _isRunning = false;
                        if (!StatusTextBlock.Text.StartsWith("Error"))
                        {
                            StatusTextBlock.Text = "Stopped";
                        }
                        LoadingOverlay.Visibility = Visibility.Collapsed;
                    });
                }

                return;
            }

            // =================================================================
            // OPENCV BACKEND
            // =================================================================
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
            Debug.WriteLine($"[STOP] ⚠️ StopProduction() called! Stack: {new System.Diagnostics.StackTrace()}");
    
            if (!_isRunning) return;

            _productionCancelTokenSource?.Cancel();

            StopTimer();
            _isRunning = false;
            _isPaused = false;

            StatusTextBlock.Text = "Production stopped";
            LoadingOverlay.Visibility = Visibility.Collapsed;

            _cameraThread?.Join();
            _cameraThread = null;

            _mjpeg?.Stop();
            _mjpeg = null;

            if (_camera != null)
            {
                _camera.Stop();
                _camera.Dispose();
                _camera = null;
            }

            ProductionImage.Source = null;
            ClearBoundingBoxes();
            _perFrameSummary.Clear();

            _productionCancelTokenSource?.Dispose();
            _productionCancelTokenSource = null;
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