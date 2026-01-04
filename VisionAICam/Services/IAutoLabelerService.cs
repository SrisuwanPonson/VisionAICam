using System.Threading;
using System.Threading.Tasks;

namespace VisionAICam.Services
{
    /// <summary>
    /// Service contract for running auto-labeler operations from other parts of the app.
    /// Designed to be registered with MasterController.Instance.RegisterService&lt;IAutoLabelerService&gt;(...)
    /// </summary>
    public interface IAutoLabelerService
    {
        /// <summary>
        /// Run model-backed auto-labeling on the currently selected image.
        /// Returns true when inference ran and at least one annotation was added.
        /// </summary>
        Task<bool> RunAutoLabelingAsync(string? modelPath = null, double confidenceThreshold = 0.5, CancellationToken cancellationToken = default);

        /// <summary>
        /// Prepare a YOLO-style training dataset from the current project.
        /// Returns true when export succeeded.
        /// </summary>
        bool PrepareTrainingDataset(int minImagesPerClass = 10, VisionAICam.YoloExportFormat format = VisionAICam.YoloExportFormat.YoloV8);
    }
}