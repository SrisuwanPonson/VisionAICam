using ClearEngine.Logging;
using OpenCvSharp;
using Python.Runtime;
using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;

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

    public sealed class InferenceEngine : IDisposable
    {
        private static readonly object _initLock = new();
        private static bool _initialized = false;
        public string modelPath { get; set; } = string.Empty;
        public string logDir { get; set; } = string.Empty;

        public static InferenceEngine? Instance { get; private set; }
        public ILogger Logger { get; set; } = ClearEngine.Logging.Logger.Instance;
        private bool _disposed;

        // Separate cached modules for OpenCV and Hikvision
        private dynamic? _cachedOpenCvModule;
        private dynamic? _cachedOpenCvDetect;
        private dynamic? _cachedHikvisionModule;
        private dynamic? _cachedHikvisionDetect;

        private InferenceEngine() { }

        public static bool Initialize(string? pythonDllPath, out string error)
            => Initialize(pythonDllPath, ClearEngine.Logging.Logger.Instance, out error);

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

        public static InferenceEngine Create(string? pythonDllPath = null, ILogger? logger = null)
        {
            if (!TryCreate(pythonDllPath, logger, out var engine, out var error))
                throw new InvalidOperationException($"Failed to create InferenceEngine: {error}");
            return engine!;
        }

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

        // ========== OPENCV METHODS ==========

        /// <summary>
        /// OpenCV detection from Mat
        /// </summary>
        public DetectionResult[] Detect(Mat mat, string modelPath, string? logDir = null)
        {
            if (mat == null) return Array.Empty<DetectionResult>();
            Cv2.ImEncode(".jpg", mat, out var buf);
            return DetectOpenCV(buf, modelPath, logDir);
        }

        /// <summary>
        /// OpenCV detection with error handling
        /// </summary>
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
            return DetectOpenCVWithError(buf, modelPath, logDir);
        }

        /// <summary>
        /// OpenCV legacy detection from byte array
        /// </summary>
        public DetectionResult[] Detect(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            return DetectOpenCV(jpegBuffer, modelPath, logDir);
        }

        /// <summary>
        /// OpenCV detection implementation
        /// </summary>
        private DetectionResult[] DetectOpenCV(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            if (jpegBuffer == null)
            {
                Debug.WriteLine($"[OPENCV-INFERENCE] ❌ jpegBuffer is null");
                return Array.Empty<DetectionResult>();
            }

            Debug.WriteLine($"[OPENCV-INFERENCE] Detect() called, buffer size: {jpegBuffer.Length}");

            var response = DetectOpenCVWithError(jpegBuffer, modelPath, logDir);

            Debug.WriteLine($"[OPENCV-INFERENCE] Result: Success={response.Success}, Detections={response.Detections.Length}");

            if (!response.Success && response.Error != null)
            {
                Debug.WriteLine($"[OPENCV-INFERENCE] ❌ Detection failed: {response.Error}");
                Logger?.LogError($"OpenCV detection failed: {response.Error}");
            }
            else
            {
                Debug.WriteLine($"[OPENCV-INFERENCE] ✅ Detection succeeded with {response.Detections.Length} results");
            }

            return response.Detections;
        }

        /// <summary>
        /// OpenCV detection with comprehensive error handling
        /// </summary>
        private InferenceResponse DetectOpenCVWithError(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            if (jpegBuffer == null || jpegBuffer.Length == 0)
                return new InferenceResponse
                {
                    Success = false,
                    Error = new PythonInferenceError { ErrorType = "InvalidInput", Message = "Image buffer is null or empty" }
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
                            Logger?.LogError($"OpenCV detect aborted: PythonEngine not initialized: {initError}");
                            return new InferenceResponse
                            {
                                Success = false,
                                Error = new PythonInferenceError { ErrorType = "InitializationError", Message = initError }
                            };
                        }
                    }
                }
            }

            string? tempFile = null;

            try
            {
                using (Py.GIL())
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                    string pythonScriptDir = @"C:\ClearEngine\VisionAICam\PythonScripts";

                    if (!Directory.Exists(pythonScriptDir))
                    {
                        pythonScriptDir = Path.Combine(baseDir, "Script");
                    }

                    string inferenceFile = Path.Combine(pythonScriptDir, "inference.py");
                    Debug.WriteLine($"[OPENCV-INFERENCE] Python script directory: {pythonScriptDir}");

                    if (!File.Exists(inferenceFile))
                    {
                        string errorMsg = $"inference.py not found at: {inferenceFile}";
                        Debug.WriteLine($"[OPENCV-INFERENCE] ❌ {errorMsg}");
                        return new InferenceResponse
                        {
                            Success = false,
                            Error = new PythonInferenceError { ErrorType = "FileNotFound", Message = errorMsg }
                        };
                    }

                    dynamic sys = Py.Import("sys");

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

                    // Import inference module (cached for OpenCV)
                    if (_cachedOpenCvModule == null)
                    {
                        Debug.WriteLine($"[OPENCV-INFERENCE] 🔥 Loading inference module for FIRST TIME...");
                        _cachedOpenCvModule = Py.Import("inference");
                        _cachedOpenCvDetect = _cachedOpenCvModule.detect;
                    }
                    else
                    {
                        Debug.WriteLine($"[OPENCV-INFERENCE] ⚡ Using CACHED inference module");
                    }

                    // Write buffer to temp file
                    tempFile = Path.Combine(Path.GetTempPath(), $"opencv_inference_{Guid.NewGuid():N}.jpg");
                    File.WriteAllBytes(tempFile, jpegBuffer);

                    Debug.WriteLine($"[OPENCV-INFERENCE] Calling detect with temp file: {tempFile}");

                    dynamic result = _cachedOpenCvDetect(tempFile, modelPath, logDir ?? Path.GetTempPath());

                    Debug.WriteLine($"[OPENCV-INFERENCE] ✅ detect() succeeded");

                    // Parse detections
                    var detections = new Collection<DetectionResult>();
                    dynamic detectionsData = result;

                    try
                    {
                        if (result.HasAttr("detections"))
                        {
                            detectionsData = result.detections;
                        }
                    }
                    catch { }

                    if (detectionsData != null)
                    {
                        foreach (dynamic det in detectionsData)
                        {
                            string? task = null;
                            string? className = null;
                            double confidence = 0.0;

                            try { task = det["Task"]?.ToString(); } catch { try { task = det.GetAttr("Task")?.ToString(); } catch { } }
                            try { className = det["class"]?.ToString(); } catch { try { className = det.GetAttr("class")?.ToString(); } catch { } }
                            try { confidence = (double)det["confidence"]; } catch { try { double.TryParse(det["confidence"]?.ToString(), out confidence); } catch { } }

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
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger?.LogWarning($"Skipping malformed 'box' for class '{className}': {ex.Message}");
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
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Logger?.LogWarning($"Skipping malformed 'rotate_box' for class '{className}': {ex.Message}");
                                }
                            }
                        }
                    }

                    try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                    Debug.WriteLine($"[OPENCV-INFERENCE] ✅ Returning {detections.Count} detections");

                    return new InferenceResponse
                    {
                        Success = true,
                        Detections = detections.ToArray()
                    };
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[OPENCV-INFERENCE] ❌ Exception: {ex.Message}");
                Logger?.LogError($"OpenCV detection error: {ex}");

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

        // ========== HIKVISION METHODS ==========

        /// <summary>
        /// Hikvision detection with error handling
        /// </summary>
        public InferenceResponse DetectHikvisionWithError(byte[] imageBytes, string modelPath, string? logDir = null)
        {
            Debug.WriteLine($"[HIK-INFERENCE] DetectHikvisionWithError() called, buffer size: {imageBytes?.Length ?? 0}");

            if (imageBytes == null || imageBytes.Length == 0)
                return new InferenceResponse
                {
                    Success = false,
                    Error = new PythonInferenceError { ErrorType = "InvalidInput", Message = "Image bytes is null or empty" }
                };

            string? tempFile = null;

            try
            {
                tempFile = Path.Combine(Path.GetTempPath(), $"hik_inference_{Guid.NewGuid():N}.jpg");
                File.WriteAllBytes(tempFile, imageBytes);

                Debug.WriteLine($"[HIK-INFERENCE] Saved temp file: {tempFile}");

                var detections = new Collection<DetectionResult>();

                using (Py.GIL())
                {
                    Debug.WriteLine($"[HIK-INFERENCE] ✅ Acquired Python GIL");

                    string scriptDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "PythonScripts");
                    if (!Directory.Exists(scriptDir))
                    {
                        scriptDir = @"C:\ClearEngine\VisionAICam\PythonScripts";
                    }

                    Debug.WriteLine($"[HIK-INFERENCE] Python script directory: {scriptDir}");

                    string inferenceScript = Path.Combine(scriptDir, "inference.py");
                    if (!File.Exists(inferenceScript))
                        throw new FileNotFoundException($"inference.py not found at: {inferenceScript}");

                    dynamic sys = Py.Import("sys");
                    dynamic importlib = Py.Import("importlib");

                    sys.path.insert(0, scriptDir);

                    // Import inference module (cached for Hikvision)
                    if (_cachedHikvisionModule == null)
                    {
                        Debug.WriteLine($"[HIK-INFERENCE] 🔥 Loading inference module for FIRST TIME...");
                        _cachedHikvisionModule = importlib.import_module("inference");
                        _cachedHikvisionDetect = _cachedHikvisionModule.detect;
                    }
                    else
                    {
                        Debug.WriteLine($"[HIK-INFERENCE] ⚡ Using CACHED inference module");
                    }

                    Debug.WriteLine($"[HIK-INFERENCE] Calling detect...");
                    Debug.WriteLine($"[HIK-INFERENCE]   Image: {tempFile}");
                    Debug.WriteLine($"[HIK-INFERENCE]   Model: {modelPath}");
                    Debug.WriteLine($"[HIK-INFERENCE]   LogDir: {logDir ?? Path.GetTempPath()}");

                    // ✅ Pass 3 arguments: image_path, model_path, log_directory
                    dynamic result = _cachedHikvisionDetect(tempFile, modelPath, logDir ?? Path.GetTempPath());

                    Debug.WriteLine($"[HIK-INFERENCE] ✅ detect(file, model, logDir) succeeded");

                    // Parse results
                    dynamic detectionsData = result;

                    try
                    {
                        if (result.HasAttr("detections"))
                        {
                            detectionsData = result.detections;
                        }
                    }
                    catch { }

                    if (detectionsData != null)
                    {
                        foreach (dynamic det in detectionsData)
                        {
                            string? task = null;
                            string? className = null;
                            double confidence = 0.0;

                            try { task = det["Task"]?.ToString(); } catch { try { task = det.GetAttr("Task")?.ToString(); } catch { } }
                            try { className = det["class"]?.ToString(); } catch { try { className = det.GetAttr("class")?.ToString(); } catch { } }
                            try { confidence = (double)det["confidence"]; } catch { try { double.TryParse(det["confidence"]?.ToString(), out confidence); } catch { } }

                            Debug.WriteLine($"[HIK-INFERENCE] Processing: class={className}, task={task}, conf={confidence}");

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

                                        Debug.WriteLine($"[HIK-INFERENCE] ✅ Added: {className}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[HIK-INFERENCE] ❌ Box parse failed: {ex.Message}");
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

                                        Debug.WriteLine($"[HIK-INFERENCE] ✅ Added OBB: {className}");
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Debug.WriteLine($"[HIK-INFERENCE] ❌ OBB parse failed: {ex.Message}");
                                }
                            }
                        }
                    }
                }

                try { if (File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                Debug.WriteLine($"[HIK-INFERENCE] ✅✅✅ Returning {detections.Count} detections");

                return new InferenceResponse
                {
                    Success = true,
                    Detections = detections.ToArray()
                };
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[HIK-INFERENCE] ❌ Exception: {ex.GetType().Name}: {ex.Message}");
                Logger?.LogError($"Hikvision detection failed: {ex}");

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