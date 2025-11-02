using System.Threading.Tasks;
using OpenCvSharp;
using ClearEngine.Model.Inference;

namespace VisionAICam.Services
{
    /// <summary>
    /// Host-facing inference service contract. Wraps ClearEngine.Model.Inference.InferenceEngine.
    /// Implementations must be safe to call from background threads; UI callers should marshal results to the UI thread.
    /// </summary>
    public interface IInferenceEngineService : System.IDisposable
    {
        string? Status { get; }
        Task PrewarmAsync(string? modelPath = null, string? logDir = null);
        DetectionResult[] Detect(Mat mat, string modelPath, string? logDir = null);
        DetectionResult[] Detect(byte[] jpegBuffer, string modelPath, string? logDir = null);
    }
}