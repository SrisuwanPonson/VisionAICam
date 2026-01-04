using System;
using System.Threading;
using System.Threading.Tasks;
using VisionAICam.Pages;
using VisionAICam.Core;
using ClearEngine.Logging;

namespace VisionAICam.Services
{
    public class AutoLabelerService : IAutoLabelerService
    {
        private readonly ILogger _log = Logger.Instance;

        public async Task<bool> RunAutoLabelingAsync(string? modelPath = null, double confidenceThreshold = 0.5, CancellationToken cancellationToken = default)
        {
            try
            {
                // Resolve model path if not provided: prefer settings registered with MasterController
                try
                {
                    var settings = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                    if (string.IsNullOrWhiteSpace(modelPath))
                        modelPath = settings?.DefaultModelPath;
                }
                catch (Exception ex)
                {
                    _log.LogWarning($"AutoLabelerService: failed to resolve settings: {ex.Message}");
                }

                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    _log.LogWarning("AutoLabelerService: no model path provided.");
                    return false;
                }

                if (cancellationToken.IsCancellationRequested) return false;

                _log.LogInfo($"AutoLabelerService: starting auto-labeling using model '{modelPath}'.");

                // Delegate to existing AutoLabeler helper (does heavy work off-UI thread internally)
                var result = await AutoLabeler.PerformAutoLabelingInferenceAsync(modelPath, confidenceThreshold).ConfigureAwait(false);

                _log.LogInfo($"AutoLabelerService: finished auto-labeling - added any: {result}");
                return result;
            }
            catch (Exception ex)
            {
                try { _log.LogError($"AutoLabelerService.RunAutoLabelingAsync failed: {ex}"); } catch { }
                return false;
            }
        }

        public bool PrepareTrainingDataset(int minImagesPerClass = 10, VisionAICam.YoloExportFormat format = VisionAICam.YoloExportFormat.YoloV8)
        {
            try
            {
                _log.LogInfo("AutoLabelerService: preparing training dataset...");
                var ok = AutoLabeler.PrepareTrainingDataset(minImagesPerClass, format);
                _log.LogInfo($"AutoLabelerService: PrepareTrainingDataset returned {ok}");
                return ok;
            }
            catch (Exception ex)
            {
                try { _log.LogError($"AutoLabelerService.PrepareTrainingDataset failed: {ex}"); } catch { }
                return false;
            }
        }
    }
}