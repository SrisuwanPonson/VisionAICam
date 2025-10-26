using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using ClearEngine.Logging;
using ClearEngine.Model.Inference;

namespace VisionAICam.Core
{
    /// <summary>
    /// Simplified MasterController — basic singleton with lightweight service container
    /// and minimal async Initialize / Shutdown lifecycle.
    /// Kept compatibility with existing InitializeEverythingAsync / InitializeAsync signatures
    /// so callers (like the splash startup) continue to compile.
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

        private MasterController() { }

        public ILogger Logger => _logger;

        public void RegisterService<T>(T service) where T : class
        {
            if (service == null) throw new ArgumentNullException(nameof(service));
            _services[typeof(T)] = service!;
        }

        public T? GetService<T>() where T : class
        {
            if (_services.TryGetValue(typeof(T), out var svc)) return svc as T;
            return null;
        }

        /// <summary>
        /// Backwards-compatible simple initializer. Reports minimal progress if provided.
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

                try
                {
                    progress?.Report("Loading settings...");
                    try
                    {
                        var settings = SettingsManager.Load();
                        if (settings != null) RegisterService(settings);
                        progress?.Report("Settings loaded.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogInfo($"MasterController: settings load failed: {ex.Message}");
                        progress?.Report("Failed loading settings (continuing).");
                    }

                    // Keep prewarm flags but don't perform heavy work in basic version.
                    if (prewarmInferenceEngine)
                    {
                        progress?.Report("Skipping heavy inference pre-warm in basic mode.");
                    }

                    _initialized = true;
                    progress?.Report("Initialization complete.");
                    _logger.LogInfo("MasterController: initialization complete (basic).");
                    return true;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInfo("MasterController: initialization cancelled.");
                    progress?.Report("Initialization cancelled.");
                    return false;
                }
            }
            finally
            {
                _initLock.Release();
            }
        }

        /// <summary>
        /// Minimal InitializeEverythingAsync — kept for compatibility with existing callers.
        /// Does not perform device or engine pre-warm in this basic implementation.
        /// </summary>
        public async Task<bool> InitializeEverythingAsync(bool prewarmInferenceEngine = true, bool prewarmCamera = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
        {
            // Delegate to the simpler initializer to avoid duplication.
            return await InitializeAsync(prewarmInferenceEngine: prewarmInferenceEngine, progress: progress, cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Graceful shutdown — disposes registered disposable services.
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
                        try { progress?.Report($"Disposing {kv.Key.Name}..."); d.Dispose(); } catch (Exception ex) { _logger.LogInfo($"MasterController: disposing {kv.Key.Name} failed: {ex.Message}"); }
                    }
                }

                _services.Clear();

                try { progress?.Report("Finalizing GC..."); GC.Collect(); GC.WaitForPendingFinalizers(); } catch { }

                _initialized = false;
                _logger.LogInfo("MasterController: shutdown complete (basic).");
                progress?.Report("Shutdown complete.");
            }
            finally
            {
                _initLock.Release();
            }
        }

        // Keep a compatibility stub for code that expects to query the inference engine.
        // Returns null in basic mode.
        public InferenceEngine? GetInferenceEngine() => null;

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
    }
}
