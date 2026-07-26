using ClearEngine.Logging;
using OpenCvSharp;
using Python.Runtime;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using static System.Formats.Asn1.AsnWriter;

namespace ClearEngine.Model.Inference
{
    // Lightweight DTO returned by the inference library
    public class DetectionResult
    {
        public string ClassName { get; set; } = "";
        public double Confidence { get; set; }
        public string Box { get; set; } = ""; // "x1,y1,x2,y2" or "cx,cy,w,h,angle"
        public string Task { get; set; } = ""; // "detect" or "obb"
    }

    // Python error information
    public class PythonInferenceError
    {
        public string ErrorType { get; set; } = "";
        public string Message { get; set; } = "";
        public string Traceback { get; set; } = "";
        
        public override string ToString()
        {
            return $"[{ErrorType}] {Message}";
        }
    }

    // Inference result wrapper
    public class InferenceResponse
    {
        public bool Success { get; set; }
        public DetectionResult[] Detections { get; set; } = Array.Empty<DetectionResult>();
        public PythonInferenceError? Error { get; set; }
    }

    // InferenceEngine: thread-safe Singleton with factory helpers and IDisposable.
    public sealed class InferenceEngine : IDisposable
    {
        private static readonly object _initLock = new();
        private static bool _initialized = false;
        public string modelPath { get; set; } = string.Empty;
        public string logDir { get; set; } = string.Empty;
        
        // Singleton instance (null until created via Create/TryCreate)
        public static InferenceEngine? Instance { get; private set; }

        // Instance logger used by instance methods
        public ILogger Logger { get; set; } = ClearEngine.Logging.Logger.Instance;

        // Track disposal state for the singleton instance
        private bool _disposed;

        // Cached Python module and function
        private dynamic? _cachedInferenceModule;
        private dynamic? _cachedDetectFunction;

        // Private ctor - enforce controlled creation (singleton/factory)
        private InferenceEngine() { }

        // Backward-compatible Initialize
        public static bool Initialize(string? pythonDllPath, out string error)
            => Initialize(pythonDllPath, ClearEngine.Logging.Logger.Instance, out error);

        // Preferred Initialize overload that accepts an ILogger
        public static bool Initialize(string? pythonDllPath, ILogger? logger, out string error)
        {
            error = string.Empty;
            lock (_initLock)
            {
                if (_initialized) return true;

                try
                {
                    if (!string.IsNullOrEmpty(pythonDllPath) && File.Exists(pythonDllPath))
                    {
                        try
                        {
                            Runtime.PythonDLL = pythonDllPath;
                            logger?.LogInfo($"Set Python.Runtime.PythonDLL = {pythonDllPath}");
                        }
                        catch (Exception ex)
                        {
                            logger?.LogError($"Setting PythonDLL threw: {ex}");
                        }
                    }

                    PythonEngine.Initialize();
                    
                    // ✅ THIS IS CRITICAL - DID YOU ADD THIS LINE?
                    PythonEngine.BeginAllowThreads();
                    
                    _initialized = true;
                    logger?.LogInfo("PythonEngine.Initialize() succeeded with threading enabled.");
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    logger?.LogError($"PythonEngine.Initialize() failed: {ex}");
                    return false;
                }
            }
        }

        // Factory that initializes Python and returns engine singleton
        public static bool TryCreate(string? pythonDllPath, ILogger? logger, out InferenceEngine? engine, out string? error)
        {
            engine = null;
            error = null;

            lock (_initLock)
            {
                if (Instance != null)
                {
                    if (logger != null)
                    {
                        Instance.Logger = logger;
                        logger.LogInfo("InferenceEngine.Instance logger updated by TryCreate().");
                    }
                    engine = Instance;
                    return true;
                }

                if (!Initialize(pythonDllPath, logger, out var initError))
                {
                    error = initError;
                    return false;
                }

                try
                {
                    var created = new InferenceEngine
                    {
                        Logger = logger ?? ClearEngine.Logging.Logger.Instance
                    };

                    Instance = created;
                    engine = created;
                    logger?.LogInfo("InferenceEngine singleton instance created via TryCreate().");
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    logger?.LogError($"Failed to create InferenceEngine instance: {ex}");
                    return false;
                }
            }
        }

        // Convenience Create that throws on failure
        public static InferenceEngine Create(string? pythonDllPath = null, ILogger? logger = null)
        {
            if (!TryCreate(pythonDllPath, logger, out var engine, out var error))
                throw new InvalidOperationException($"Failed to create InferenceEngine: {error}");
            return engine!;
        }

        // Prewarm with dummy frame
        public void PrewarmFirstFrameAsync(string modelPath, string logDir)
        {
            Mat? firstMat2 = null;
            try
            {
                firstMat2 = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
                Logger?.LogInfo("PrewarmFirstFrameAsync: created dummy black frame.");
                this.Detect(firstMat2!, modelPath, logDir);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"PrewarmFirstFrameAsync error: {ex}");
            }
            finally
            {
                try { firstMat2?.Dispose(); } catch { }
            }
        }

        public void PrewarmFirstFrameAsync()
        {
            Mat? firstMat2 = null;
            try
            {
                firstMat2 = new Mat(480, 640, MatType.CV_8UC3, Scalar.All(0));
                Logger?.LogInfo("PrewarmFirstFrameAsync: created dummy black frame.");
                this.Detect(firstMat2!, this.modelPath, this.logDir);
            }
            catch (Exception ex)
            {
                Logger?.LogError($"PrewarmFirstFrameAsync error: {ex}");
            }
            finally
            {
                try { firstMat2?.Dispose(); } catch { }
            }
        }

        // Detect using an OpenCv Mat
        public DetectionResult[] Detect(Mat mat, string modelPath, string? logDir = null)
        {
            if (mat == null) return Array.Empty<DetectionResult>();
            Cv2.ImEncode(".jpg", mat, out var buf);
            return Detect(buf, modelPath, logDir);
        }

        // NEW: Detect with error information
        public InferenceResponse DetectWithError(Mat mat, string modelPath, string? logDir = null)
        {
            if (mat == null) 
                return new InferenceResponse 
                { 
                    Success = false, 
                    Error = new PythonInferenceError 
                    { 
                        ErrorType = "InvalidInput", 
                        Message = "Input Mat is null" 
                    } 
                };
            
            Cv2.ImEncode(".jpg", mat, out var buf);
            return DetectWithError(buf, modelPath, logDir);
        }

        // NEW: Detect with comprehensive error handling
        // Replace the entire DetectWithError(byte[] imageBytes...) method with this:
        public InferenceResponse DetectWithError(byte[] imageBytes, string modelPath, string? logDir = null)
        {
            Debug.WriteLine($"[INFERENCE-ENGINE] DetectWithError() called, buffer size: {imageBytes?.Length ?? 0}");

            if (imageBytes == null || imageBytes.Length == 0)
                return new InferenceResponse
                {
                    Success = false,
                    Error = new PythonInferenceError { ErrorType = "InvalidInput", Message = "Image bytes is null or empty" }
                };

            string? tempFile = null;

            try
            {
                // Save to temp file
                tempFile = Path.Combine(Path.GetTempPath(), $"inference_{Guid.NewGuid():N}.jpg");
                File.WriteAllBytes(tempFile, imageBytes);

                Debug.WriteLine($"[INFERENCE-ENGINE] Saved temp file: {tempFile}");
                Debug.WriteLine($"[INFERENCE-ENGINE] Attempting to acquire Python GIL...");

                List<DetectionResult> detections = new List<DetectionResult>();

                using (Py.GIL())
                {
                    Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Acquired Python GIL");

                    // Get Python script directory
                    string scriptDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PythonScripts");
                    if (!Directory.Exists(scriptDir))
                    {
                        scriptDir = @"C:\ClearEngine\VisionAICam\PythonScripts";
                    }

                    Debug.WriteLine($"[INFERENCE-ENGINE] Python script directory: {scriptDir}");

                    string inferenceScript = Path.Combine(scriptDir, "inference.py");
                    bool scriptExists = File.Exists(inferenceScript);
                    Debug.WriteLine($"[INFERENCE-ENGINE] inference.py exists: {scriptExists}");

                    if (!scriptExists)
                        throw new FileNotFoundException($"inference.py not found at: {inferenceScript}");

                    Debug.WriteLine($"[INFERENCE-ENGINE] Importing Python modules...");

                    // Import Python modules
                    dynamic sys = Py.Import("sys");
                    dynamic importlib = Py.Import("importlib");

                    Debug.WriteLine($"[INFERENCE-ENGINE] Adding script dir to sys.path...");

                    // Add script directory to Python path
                    sys.path.insert(0, scriptDir);
                    Debug.WriteLine($"[INFERENCE-ENGINE] Added {scriptDir} to sys.path");

                    Debug.WriteLine($"[INFERENCE-ENGINE] Importing inference module...");

                    // Import inference module
                    if (_cachedInferenceModule == null)
                    {
                        Debug.WriteLine($"[INFERENCE-ENGINE] 🔥 Loading inference module for FIRST TIME...");
                        _cachedInferenceModule = importlib.import_module("inference");
                        _cachedDetectFunction = _cachedInferenceModule.detect;
                    }
                    else
                    {
                        Debug.WriteLine($"[INFERENCE-ENGINE] ⚡ Using CACHED inference module");
                    }

                    dynamic detect = _cachedDetectFunction;

                    Debug.WriteLine($"[INFERENCE-ENGINE] Calling detect...");
                    Debug.WriteLine($"[INFERENCE-ENGINE]   Image: {tempFile}");
                    Debug.WriteLine($"[INFERENCE-ENGINE]   Model: {modelPath}");

                    // Pass both image_path AND model_path
                    dynamic result = detect(tempFile, modelPath);

                    Debug.WriteLine($"[INFERENCE-ENGINE] ✅ detect(file, model) succeeded");

                    // Parse results
                    Debug.WriteLine($"[INFERENCE-ENGINE] Parsing result");

                    int count = 0;
                    foreach (var item in result)
                    {
                        try
                        {
                            string className = item.class_name?.ToString() ?? "";
                            double confidence = (double)(item.confidence ?? 0.0);
                            string box = item.box?.ToString() ?? "";
                            string task = item.task?.ToString() ?? "";

                            detections.Add(new DetectionResult
                            {
                                ClassName = className,
                                Confidence = confidence,
                                Box = box,
                                Task = task
                            });

                            count++;
                        }
                        catch (Exception itemEx)
                        {
                            Debug.WriteLine($"[INFERENCE-ENGINE] Failed to parse detection item: {itemEx.Message}");
                        }
                    }

                    Debug.WriteLine($"[INFERENCE-ENGINE] Found {count} detections");
                } // Release GIL

                Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Released Python GIL");
                Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Successfully parsed {detections.Count} detections");

                // Cleanup temp file
                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                Debug.WriteLine($"[INFERENCE-ENGINE] ✅✅✅ Returning {detections.Count} detections");

                return new InferenceResponse
                {
                    Success = true,
                    Detections = detections.ToArray()
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[INFERENCE-ENGINE] ❌ Exception: {ex.GetType().Name}: {ex.Message}");
                Debug.WriteLine($"[INFERENCE-ENGINE] Stack trace: {ex.StackTrace}");
                Logger?.LogError($"DetectWithError failed: {ex}");

                // Cleanup temp file
                try { if (tempFile != null && File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                return new InferenceResponse
                {
                    Success = false,
                    Error = new PythonInferenceError
                    {
                        ErrorType = ex.GetType().Name,
                        Message = ex.Message,
                        Traceback = ex.StackTrace ?? ""
                    }
                };
            }
        }

        // Legacy Detect method - backward compatible
        public DetectionResult[] Detect(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            // ✅ FIX CS8604: Add null check
            if (jpegBuffer == null)
            {
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ jpegBuffer is null");
                return Array.Empty<DetectionResult>();
            }
            
            System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Detect() called, buffer size: {jpegBuffer.Length}");

            var response = DetectWithError(jpegBuffer, modelPath, logDir);

            // 🔥 ADD DEBUG OUTPUT
            System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] DetectWithError() returned Success={response.Success}, Detections={response.Detections.Length}");

            if (!response.Success && response.Error != null)
            {
                // 🔥 ADD DEBUG OUTPUT
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ Detection failed: {response.Error}");
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Error Type: {response.Error.ErrorType}");
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Error Message: {response.Error.Message}");

                Logger?.LogError($"Detection failed: {response.Error}");
                if (!string.IsNullOrEmpty(response.Error.Traceback))
                {
                    System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Traceback:\n{response.Error.Traceback}");
                    Logger?.LogError($"Python traceback:\n{response.Error.Traceback}");
                }
            }
            else
            {
                // 🔥 ADD DEBUG OUTPUT FOR SUCCESS
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Detection succeeded with {response.Detections.Length} results");
            }

            return response.Detections;
        }

        // Dispose pattern
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;

            lock (_initLock)
            {
                try
                {
                    if (_initialized)
                    {
                        try
                        {   
                            PythonEngine.Shutdown();
                            Logger?.LogInfo("PythonEngine.Shutdown() called.");
                        }
                        catch (Exception ex)
                        {
                            Logger?.LogError($"PythonEngine.Shutdown threw: {ex}");
                        }
                        _initialized = false;
                    }
                }
                finally
                {
                    if (ReferenceEquals(Instance, this))
                        Instance = null;
                }
            }

            _disposed = true;
        }
    }
}