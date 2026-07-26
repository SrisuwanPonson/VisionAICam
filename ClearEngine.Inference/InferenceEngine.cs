using System;
using System.Collections.ObjectModel;
using System.IO;
using OpenCvSharp;
using Python.Runtime;
using ClearEngine.Logging;

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
                    _initialized = true;
                    logger?.LogInfo("PythonEngine.Initialize() succeeded.");
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
        public InferenceResponse DetectWithError(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            if (jpegBuffer == null) 
                return new InferenceResponse 
                { 
                    Success = false, 
                    Error = new PythonInferenceError 
                    { 
                        ErrorType = "InvalidInput", 
                        Message = "Input buffer is null" 
                    } 
                };

            // Ensure Python is initialized
            if (!_initialized || !PythonEngine.IsInitialized)
            {
                lock (_initLock)
                {
                    if (!_initialized || !PythonEngine.IsInitialized)
                    {
                        if (!Initialize(null, Logger, out var initError))
                        {
                            Logger?.LogError($"Detect aborted: PythonEngine not initialized: {initError}");
                            return new InferenceResponse 
                            { 
                                Success = false, 
                                Error = new PythonInferenceError 
                                { 
                                    ErrorType = "InitializationError", 
                                    Message = initError 
                                } 
                            };
                        }
                    }
                }
            }

            try
            {
                using (Py.GIL())
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                    
                    // ✅ Try multiple possible locations
                    string pythonScriptDir = @"C:\ClearEngine\VisionAICam\PythonScripts";
                    
                    // Fallback to local Script folder if PythonScripts doesn't exist
                    if (!Directory.Exists(pythonScriptDir))
                    {
                        pythonScriptDir = Path.Combine(baseDir, "Script");
                    }
                    
                    // Verify inference.py exists
                    string inferenceFile = Path.Combine(pythonScriptDir, "inference.py");
                    System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Python script directory: {pythonScriptDir}");
                    System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] inference.py exists: {File.Exists(inferenceFile)}");
                    
                    if (!File.Exists(inferenceFile))
                    {
                        string errorMsg = $"inference.py not found at: {inferenceFile}";
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ {errorMsg}");
                        Logger?.LogError(errorMsg);
                        return new InferenceResponse 
                        { 
                            Success = false, 
                            Error = new PythonInferenceError 
                            { 
                                ErrorType = "FileNotFound", 
                                Message = errorMsg 
                            } 
                        };
                    }
                    

                    // Import sys module
                    dynamic sys;
                    try
                    {
                        sys = Py.Import("sys");
                    }
                    catch (PythonException pex)
                    {
                        Logger?.LogError($"Failed to import 'sys' module: {pex.Message}");
                        return new InferenceResponse 
                        { 
                            Success = false, 
                            Error = new PythonInferenceError 
                            { 
                                ErrorType = "PythonImportError", 
                                Message = $"Failed to import sys: {pex.Message}" 
                            } 
                        };
                    }

                    // Ensure script folder on sys.path
                    try
                    {
                        bool pathExists = false;
                        foreach (dynamic p in sys.path)
                        {
                            try
                            {
                                if (pythonScriptDir.Equals((string)p.ToString(), StringComparison.OrdinalIgnoreCase))
                                {
                                    pathExists = true;
                                    break;
                                }
                            }
                            catch { }
                        }
                        if (!pathExists)
                        {
                            sys.path.append(pythonScriptDir);
                            Logger?.LogInfo($"Added '{pythonScriptDir}' to Python sys.path");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogWarning($"Error modifying sys.path: {ex.Message}");
                    }

                    // Import inference module
                    dynamic inference;
                    try
                    {
                        inference = Py.Import("inference");
                    }
                    catch (PythonException pex)
                    {
                        string errorMsg = $"Failed to import 'inference' module: {pex.Message}";
                        Logger?.LogError(errorMsg);
                        return new InferenceResponse 
                        { 
                            Success = false, 
                            Error = new PythonInferenceError 
                            { 
                                ErrorType = "ModuleImportError", 
                                Message = errorMsg,
                                Traceback = pex.StackTrace ?? ""
                            } 
                        };
                    }

                    // Call detect using temp file (most reliable method)
                    dynamic? result = null;
                    string tempFile = "";

                    try
                    {
                        // Write buffer to temp file
                        tempFile = Path.Combine(Path.GetTempPath(), $"inference_{Guid.NewGuid():N}.jpg");
                        File.WriteAllBytes(tempFile, jpegBuffer);
                        
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Calling detect with temp file: {tempFile}");
                        
                        result = inference.detect(tempFile, modelPath, logDir);
                        
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ✅ detect(file) succeeded");
                    }
                    catch (PythonException pex)
                    {
                        Logger?.LogError($"detect(file) failed: {pex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ detect(file) failed: {pex.Message}");
                        
                        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                        
                        return new InferenceResponse 
                        { 
                            Success = false, 
                            Error = new PythonInferenceError 
                            { 
                                ErrorType = "DetectionError", 
                                Message = pex.Message,
                                Traceback = pex.StackTrace ?? ""
                            } 
                        };
                    }

                    // Check if result indicates error
                    try
                    {
                        bool success = (bool)result.success;
                        
                        if (!success)
                        {
                            // Extract error information from Python
                            string? errorType = result.error_type?.ToString();
                            string? errorMessage = result.message?.ToString();
                            string? errorTraceback = result.traceback?.ToString();
                            
                            Logger?.LogError($"Python inference error: [{errorType}] {errorMessage}");
                            
                            try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                            
                            return new InferenceResponse 
                            { 
                                Success = false, 
                                Error = new PythonInferenceError 
                                { 
                                    ErrorType = errorType ?? "UnknownError",
                                    Message = errorMessage ?? "Unknown error occurred",
                                    Traceback = errorTraceback ?? ""
                                } 
                            };
                        }
                    }
                    catch
                    {
                        // If success attribute doesn't exist, assume old-style list return
                    }

                    // Parse detections
                    // Replace lines 431-521 with this improved parsing logic:

                    // Parse detections
                    var detections = new Collection<DetectionResult>();
                    try
                    {
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Parsing result, type: {result.GetType().Name}");

                        dynamic detectionsData = result;

                        // Try to access .detections property (new format)
                        try
                        {
                            if (result.HasAttr("detections"))
                            {
                                detectionsData = result.detections;
                                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Using result.detections");
                            }
                            else
                            {
                                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Using result directly as list");
                            }
                        }
                        catch
                        {
                            // result is already a list, use it directly
                            System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] result.detections failed, using result directly");
                        }

                        if (detectionsData != null)
                        {
                            int detCount = 0;
                            try
                            {
                                // Try to get length
                                detCount = (int)detectionsData.__len__();
                                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Found {detCount} detections");
                            }
                            catch
                            {
                                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Could not get detection count");
                            }

                            foreach (dynamic det in detectionsData)
                            {
                                string? task = null;
                                string? className = null;
                                double confidence = 0.0;

                                try { task = det["Task"]?.ToString(); } catch { try { task = det.GetAttr("Task")?.ToString(); } catch { } }
                                try { className = det["class"]?.ToString(); } catch { try { className = det.GetAttr("class")?.ToString(); } catch { } }

                                try { confidence = (double)det["confidence"]; }
                                catch { try { double.TryParse(det["confidence"]?.ToString(), out confidence); } catch { } }

                                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Processing detection: class={className}, task={task}, conf={confidence}");

                                if (task == "detect")
                                {
                                    try
                                    {
                                        var box = det["box"];
                                        if (box != null)
                                        {
                                            double b0 = Convert.ToDouble(box[0]);
                                            double b1 = Convert.ToDouble(box[1]);
                                            double b2 = Convert.ToDouble(box[2]);
                                            double b3 = Convert.ToDouble(box[3]);

                                            detections.Add(new DetectionResult
                                            {
                                                ClassName = className ?? "",
                                                Confidence = confidence,
                                                Box = $"{b0},{b1},{b2},{b3}",
                                                Task = "detect"
                                            });

                                            System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Added detection: {className} at {b0},{b1},{b2},{b3}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger?.LogWarning($"Skipping malformed 'box' for class '{className}': {ex.Message}");
                                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ Box parse failed: {ex.Message}");
                                    }
                                }
                                else if (task == "obb")
                                {
                                    try
                                    {
                                        var rotateBox = det["rotate_box"];
                                        if (rotateBox != null)
                                        {
                                            double r0 = Convert.ToDouble(rotateBox[0]);
                                            double r1 = Convert.ToDouble(rotateBox[1]);
                                            double r2 = Convert.ToDouble(rotateBox[2]);
                                            double r3 = Convert.ToDouble(rotateBox[3]);
                                            double r4 = Convert.ToDouble(rotateBox[4]);

                                            detections.Add(new DetectionResult
                                            {
                                                ClassName = className ?? "",
                                                Confidence = confidence,
                                                Box = $"{r0},{r1},{r2},{r3},{r4}",
                                                Task = "obb"
                                            });

                                            System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Added OBB detection: {className}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger?.LogWarning($"Skipping malformed 'rotate_box' for class '{className}': {ex.Message}");
                                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ OBB parse failed: {ex.Message}");
                                    }
                                }
                            }

                            System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ✅ Successfully parsed {detections.Count} detections");
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger?.LogError($"Failed to parse inference results: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ Parse error: {ex.Message}");
                        System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Stack: {ex.StackTrace}");

                        try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                        return new InferenceResponse
                        {
                            Success = false,
                            Error = new PythonInferenceError
                            {
                                ErrorType = "ParseError",
                                Message = $"Failed to parse detection results: {ex.Message}"
                            }
                        };
                    }

                    // Cleanup temp file
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                    System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ✅✅✅ Returning {detections.Count} detections");

                    return new InferenceResponse
                    {
                        Success = true,
                        Detections = detections.ToArray()
                    };

                    // Cleanup temp file
                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                    return new InferenceResponse 
                    { 
                        Success = true, 
                        Detections = detections.ToArray() 
                    };
                }
            }
            catch (Exception ex)
            {
                // 🔥 ADD DEBUG OUTPUT
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] ❌ Outer exception caught");
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Type: {ex.GetType().Name}");
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Message: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"[INFERENCE-ENGINE] Stack:\n{ex.StackTrace}");

                Logger?.LogError($"Detection error: {ex.Message}");
                Logger?.LogError($"Full exception: {ex}");

                return new InferenceResponse
                {
                    Success = false,
                    Error = new PythonInferenceError
                    {
                        ErrorType = "UnexpectedError",
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