using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
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

                // Try to initialize inference engine and register wrapper service
                if (prewarmInferenceEngine)
                {
                    try
                    {
                        var settingsSvc = GetService<AppSettings>();
                        string pythonDll = settingsSvc?.PythonDllPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script", "NewEnv", "Python313", "python313.dll");

                        if (InferenceEngine.TryCreate(pythonDll, _logger, out var engine, out var engineError) && engine != null)
                        {
                            // configure engine
                            engine.modelPath = settingsSvc?.DefaultModelPath ?? engine.modelPath ?? "model.pt";
                            engine.logDir = _logger.GetLogDirectory();

                            // register concrete engine for backward compatibility
                            RegisterService(engine);

                            // register wrapper service
                            var svc = new InferenceEngineService(engine, _logger);
                            RegisterService<IInferenceEngineService>(svc);

                            try { _logger.LogInfo("InferenceEngine initialized and registered."); } catch { }

                            // kick off async prewarm if configured
                            bool prewarm = true;
                            try
                            {
                                var prop = settingsSvc?.GetType().GetProperty("InferencePrewarm");
                                if (prop != null) prewarm = Convert.ToBoolean(prop.GetValue(settingsSvc) ?? true);
                            }
                            catch { }

                            if (prewarm)
                            {
                                _ = svc.PrewarmAsync(engine.modelPath, engine.logDir);
                            }
                        }
                        else
                        {
                            try { _logger.LogError($"InferenceEngine initialization failed: {engineError}"); } catch { }
                        }
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
    }
}
