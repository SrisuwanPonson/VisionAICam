using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ClearEngine.Logging;
using ClearEngine.Model.Inference;
using VisionAICam.Pages;
using VisionAICam.Services;

namespace VisionAICam.Core
{
    /// <summary>
    /// Core MasterController — application-wide singleton and service container.
    /// Other classes obtain the central controller via `MasterController.Instance`.
    /// Provides initialization, shutdown and a small DI-like Register/Get API.
    /// </summary>
    public sealed class MasterController : IAsyncDisposable, IDisposable
    {
        private static readonly Lazy<MasterController> _lazy = new(() => new MasterController());
        public static MasterController Instance => _lazy.Value;

        private readonly ConcurrentDictionary<Type, object> _services = new();
        private readonly ILogger _logger = global::ClearEngine.Logging.Logger.Instance;
        private readonly SemaphoreSlim _initLock = new(1, 1);
        private CancellationTokenSource? _cts;
        private bool _initialized;

        // Page instances (created on UI thread on-demand)
        private Production? _production;
        private CameraPage? _cameraPage;
        private DataSetPage? _dataSetPage;
        private ModelPage? _modelPage;
        private SettingPage? _settingPage;
        private DiagnosticsPage? _diagnosticsPage;
        private DataSetPage? _dataSetPage2;
        private UserPage? _userPage;

        private MasterController() { }

        // Expose pages (created on UI thread)
        public Production Production => EnsureOnUi(ref _production, () => new Production());
        public CameraPage CameraPage => EnsureOnUi(ref _cameraPage, () => new CameraPage());
        public DataSetPage DataSetPage => EnsureOnUi(ref _dataSetPage, () => new DataSetPage());
        public ModelPage ModelPage => EnsureOnUi(ref _modelPage, () => new ModelPage());
        public SettingPage SettingPage => EnsureOnUi(ref _settingPage, () => new SettingPage());
        public DiagnosticsPage DiagnosticsPage => EnsureOnUi(ref _diagnosticsPage, () => new DiagnosticsPage());
        public DataSetPage DataSetPage2 => EnsureOnUi(ref _dataSetPage2, () => new DataSetPage());
        public UserPage UserPage => EnsureOnUi(ref _userPage, () => new UserPage());

        public ILogger Logger => _logger;

        // Simple service registration / resolution
        public void RegisterService<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            _services[typeof(T)] = service;
        }

        public T? GetService<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var svc)) return svc as T;
            return null;
        }

        public T GetRequiredService<T>() where T : class
        {
            var svc = GetService<T>();
            if (svc == null)
                throw new InvalidOperationException($"Required service '{typeof(T).FullName}' is not registered. Ensure MasterController.Instance.InitializeAsync(...) has run and registered the service.");
            return svc;
        }

        /// <summary>
        /// Initialize application core: load settings, optionally initialize inference engine and register services.
        /// Safe to call multiple times; subsequent calls are no-ops.
        /// </summary>
        public async Task<bool> InitializeAsync(bool prewarmInferenceEngine = true, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_initialized)
                {
                    progress?.Report("Already initialized.");
                    return true;
                }

                _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                progress?.Report("Loading settings...");
                AppSettings? settings = null;
                try
                {
                    settings = SettingsManager.Load();
                    if (settings == null)
                    {
                        settings = new AppSettings();
                    }

                    // normalize some defaults
                    if (settings.SamplingInterval <= 0) settings.SamplingInterval = 20;
                    if (settings.CameraIndex < 0) settings.CameraIndex = 0;
                    if (string.IsNullOrWhiteSpace(settings.DefaultModelPath)) settings.DefaultModelPath = "model.pt";
                    if (string.IsNullOrWhiteSpace(settings.DefaultImagePath)) settings.DefaultImagePath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    if (string.IsNullOrWhiteSpace(settings.PythonDllPath))
                    {
                        settings.PythonDllPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script", "NewEnv", "Python313", "python313.dll");
                    }

                    RegisterService(settings);
                    progress?.Report("Settings loaded.");
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"MasterController: settings load failed: {ex}"); } catch { }
                    progress?.Report("Failed loading settings (continuing).");
                }

                //Try to initialize inference engine and register wrapper service
                if (prewarmInferenceEngine)
                {
                    try
                    {
                        //Production.Prewarm();
                        progress?.Report("Inference engine initialized.");
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError($"MasterController: engine init error: {ex}"); } catch { }
                    }
                }

                _initialized = true;
                progress?.Report("Initialization complete.");
                try { _logger.LogInfo("MasterController: initialization complete."); } catch { }
                return true;
            }
            catch (OperationCanceledException)
            {
                try { _logger.LogInfo("MasterController: initialization cancelled."); } catch { }
                progress?.Report("Initialization cancelled.");
                return false;
            }
            finally
            {
                _initLock.Release();
            }
        }

        public Task<bool> InitializeEverythingAsync(bool prewarmInferenceEngine = true, bool prewarmCamera = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
            => InitializeAsync(prewarmInferenceEngine: prewarmInferenceEngine, progress: progress, cancellationToken: cancellationToken);

        /// <summary>
        /// Shutdown: disposes registered IDisposable services and clears container.
        /// </summary>
        public async Task ShutdownAsync(IProgress<string>? progress = null)
        {
            await _initLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_initialized && _cts == null)
                {
                    progress?.Report("Nothing to shut down.");
                    return;
                }

                try { progress?.Report("Cancelling operations..."); _cts?.Cancel(); } catch { }

                foreach (var kv in _services)
                {
                    if (kv.Value is IDisposable d)
                    {
                        try { progress?.Report($"Disposing {kv.Key.Name}..."); d.Dispose(); } catch (Exception ex) { try { _logger.LogInfo($"Disposing {kv.Key.Name} failed: {ex.Message}"); } catch { } }
                    }
                }

                _services.Clear();

                try { progress?.Report("Finalizing GC..."); GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }

                _initialized = false;
                try { _logger.LogInfo("MasterController: shutdown complete."); } catch { }
                progress?.Report("Shutdown complete.");
            }
            finally
            {
                _initLock.Release();
            }
        }

        // Backward-compat helper
        public InferenceEngine? GetInferenceEngine()
        {
            return GetService<InferenceEngine>();
        }

        public async ValueTask DisposeAsync()
        {
            await ShutdownAsync().ConfigureAwait(false);
            _cts?.Dispose();
            _initLock.Dispose();
        }

        public void Dispose()
        {
            try { ShutdownAsync().GetAwaiter().GetResult(); } catch { }
            _cts?.Dispose();
            _initLock.Dispose();
            GC.SuppressFinalize(this);
        }

        // UI-thread safe factory helper (creates control on UI dispatcher if necessary)
        private T EnsureOnUi<T>(ref T? field, Func<T> factory) where T : class
        {
            if (field != null) return field;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                field = factory();
                return field;
            }

            if (dispatcher.CheckAccess())
            {
                field = factory();
                return field;
            }

            T? created = null;
            dispatcher.Invoke(() =>
            {
                try { created = factory(); } catch (Exception ex) { try { _logger.LogError($"EnsureOnUi factory threw: {ex}"); } catch { } }
            });

            if (field == null && created != null) field = created;
            return field ?? created!;
        }

        // Shared, UI-bound collection of detection results used by DataPage.
        // Uses the Production page DTO type (VisionAICam.Pages.DetectionResult).
        private readonly ObservableCollection<VisionAICam.Pages.DetectionResult> _sharedResults = new();
        public ObservableCollection<VisionAICam.Pages.DetectionResult> SharedResults => _sharedResults;

        private const int DefaultMaxSharedResults = 100;

        private readonly ConcurrentQueue<VisionAICam.Pages.DetectionResult> _resultsQueue = new();
        private int _flushPending = 0; // 0 = not scheduled, 1 = scheduled or running

        // Add results on UI thread in a single dispatched op and keep collection size bounded.
        public void AddDetectionResults(IEnumerable<VisionAICam.Pages.DetectionResult> results)
        {
            if (results == null) return;

            // enqueue items quickly from any thread
            foreach (var r in results)
                _resultsQueue.Enqueue(r);

            // schedule a single UI flush if none is pending
            if (Interlocked.Exchange(ref _flushPending, 1) == 0)
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    // No UI yet — drop or buffer (we already buffered in _resultsQueue)
                    Interlocked.Exchange(ref _flushPending, 0);
                    return;
                }

                dispatcher.BeginInvoke(new Action(FlushQueuedResults), DispatcherPriority.Normal);
            }
        }

        // runs on UI thread (dispatched)
        private void FlushQueuedResults()
        {
            try
            {
                // drain queue into a list
                var list = new List<VisionAICam.Pages.DetectionResult>();
                while (_resultsQueue.TryDequeue(out var item))
                    list.Add(item);

                if (list.Count > 0)
                    AddRangeAndTrim(_sharedResults, list.ToArray(), DefaultMaxSharedResults);
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"FlushQueuedResults failed: {ex}"); } catch { }
            }
            finally
            {
                // mark not pending
                Interlocked.Exchange(ref _flushPending, 0);

                // If new items arrived while we were flushing, schedule another flush
                if (!_resultsQueue.IsEmpty && Interlocked.Exchange(ref _flushPending, 1) == 0)
                {
                    var dispatcher = Application.Current?.Dispatcher;
                    if (dispatcher != null)
                        dispatcher.BeginInvoke(new Action(FlushQueuedResults), DispatcherPriority.Normal);
                    else
                        Interlocked.Exchange(ref _flushPending, 0);
                }
            }
        }

        // Helper: add items and trim oldest to keep collection bounded (must be called on UI thread)
        private static void AddRangeAndTrim(ObservableCollection<VisionAICam.Pages.DetectionResult> target, VisionAICam.Pages.DetectionResult[] items, int maxItems)
        {
            if (target == null || items == null || items.Length == 0) return;

            foreach (var it in items)
            {
                target.Add(it);
            }

            // Trim oldest entries if we've grown too large
            if (maxItems > 0)
            {
                while (target.Count > maxItems)
                {
                    try { target.RemoveAt(0); } catch { break; }
                }
            }
        }

        /// <summary>
        /// Attempts to prewarm the inference engine using an image taken from the configured
        /// DefaultImagePath (file or first image under the directory). This method is safe to
        /// call multiple times and runs the actual detection on a background thread so it does
        /// not block the caller.
        /// Returns true when an image was found and detection completed (or ran without throwing).
        /// </summary>
        public async Task<bool> PrewarmInferenceFromDefaultImageAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var settings = GetService<AppSettings>() ?? SettingsManager.Load();
                string? startPath = settings?.DefaultImagePath;
                if (string.IsNullOrWhiteSpace(startPath))
                    startPath = AppDomain.CurrentDomain.BaseDirectory;

                string? imageFile = null;

                // If path is a file, use it directly
                if (File.Exists(startPath))
                {
                    imageFile = startPath;
                }
                else
                {
                    // Treat as directory and search for common image types (first hit)
                    if (!Directory.Exists(startPath))
                        startPath = AppDomain.CurrentDomain.BaseDirectory;

                    string[] exts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".tiff", ".webp" };
                    foreach (var ext in exts)
                    {
                        try
                        {
                            imageFile = Directory.EnumerateFiles(startPath, "*" + ext, SearchOption.AllDirectories).FirstOrDefault();
                            if (!string.IsNullOrEmpty(imageFile) && File.Exists(imageFile))
                                break;
                            imageFile = null;
                        }
                        catch (UnauthorizedAccessException) { /* skip inaccessible folders */ }
                        catch (PathTooLongException) { /* skip problematic paths */ }
                    }
                }

                if (string.IsNullOrWhiteSpace(imageFile) || !File.Exists(imageFile))
                {
                    try { _logger.LogInfo("PrewarmInferenceFromDefaultImageAsync: no image found to prewarm."); } catch { }
                    return false;
                }

                // Read image bytes once (small memory cost) and run detection in background to prewarm
                byte[] jpegBuffer = await Task.Run(() => File.ReadAllBytes(imageFile), cancellationToken).ConfigureAwait(false);

                var engine = GetService<InferenceEngine>();
                if (engine == null)
                {
                    try { _logger.LogWarning("PrewarmInferenceFromDefaultImageAsync: InferenceEngine service not registered."); } catch { }
                    return false;
                }

                string modelPath = engine.modelPath ?? settings?.DefaultModelPath ?? "model.pt";
                string logDir = _logger.GetLogDirectory();

                // Run detection on background thread — this will initialize Python + model calls inside the engine
                await Task.Run(() =>
                {
                    try
                    {
                        // Use Detect(byte[]) which already attempts in-memory call and falls back to temp file as needed.
                        var res = engine.Detect(jpegBuffer, modelPath, logDir);
                        try { _logger.LogInfo($"PrewarmInferenceFromDefaultImageAsync: detection ran on {Path.GetFileName(imageFile)}, results: {res?.Length ?? 0}"); } catch { }
                    }
                    catch (Exception ex)
                    {
                        try { _logger.LogError($"PrewarmInferenceFromDefaultImageAsync: detection failed: {ex}"); } catch { }
                        throw;
                    }
                }, cancellationToken).ConfigureAwait(false);

                return true;
            }
            catch (OperationCanceledException)
            {
                try { _logger.LogInfo("PrewarmInferenceFromDefaultImageAsync cancelled."); } catch { }
                return false;
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"PrewarmInferenceFromDefaultImageAsync error: {ex}"); } catch { }
                return false;
            }
        }
    }
}
