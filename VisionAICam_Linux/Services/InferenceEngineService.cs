using System;
using System.Threading.Tasks;
using OpenCvSharp;
using ClearEngine.Model.Inference;
using ClearEngine.Logging;

namespace VisionAICam_Linux.Services
{
    /// <summary>
    /// Lightweight wrapper that exposes the ClearEngine InferenceEngine as a host service.
    /// The wrapper does not create multiple engine instances; it uses the InferenceEngine singleton
    /// produced by ClearEngine.Model.Inference.InferenceEngine.TryCreate(...) and forwards calls.
    /// </summary>
    public class InferenceEngineService : IInferenceEngineService
    {
        private readonly InferenceEngine _engine;
        private readonly ILogger _logger;

        public string? Status => _engine == null ? null : null; // engine doesn't expose status in current file; keep placeholder

        public InferenceEngineService(InferenceEngine engine, ILogger logger)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
            _logger = logger ?? ClearEngine.Logging.Logger.Instance;
        }

        public Task PrewarmAsync(string? modelPath = null, string? logDir = null)
        {
            // Prewarm does not block caller; run on threadpool.
            return Task.Run(() =>
            {
                try
                {
                    _engine.PrewarmFirstFrameAsync(modelPath ?? _engine.modelPath, logDir ?? _engine.logDir);
                }
                catch (Exception ex)
                {
                    try { _logger.LogError($"InferenceEngineService PrewarmAsync failed: {ex}"); } catch { }
                }
            });
        }

        public DetectionResult[] Detect(Mat mat, string modelPath, string? logDir = null)
        {
            return _engine.Detect(mat, modelPath, logDir);
        }

        public DetectionResult[] Detect(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            return _engine.Detect(jpegBuffer, modelPath, logDir);
        }

        public void Dispose()
        {
            try
            {
                // Do not dispose the underlying singleton here unless you want service lifetime to control python shutdown.
                // If we own the engine, call _engine.Dispose(); otherwise leave singleton in place.
                // _engine.Dispose();
            }
            catch (Exception ex)
            {
                try { _logger.LogError($"InferenceEngineService.Dispose error: {ex}"); } catch { }
            }
        }
    }
}