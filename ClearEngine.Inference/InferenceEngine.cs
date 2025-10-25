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

    public sealed class InferenceEngine
    {
        private static readonly object _initLock = new();
        private static bool _initialized = false;

        // Instance logger used by instance methods. Host should set this to supply logging.
        // Use fully-qualified Logger.Instance to avoid any ambiguity with the property name.
        public ILogger Logger { get; set; } = ClearEngine.Logging.Logger.Instance;

        // Backward-compatible Initialize: uses the global ClearEngine.Logging.Logger.Instance
        public static bool Initialize(string? pythonDllPath, out string error)
            => Initialize(pythonDllPath, ClearEngine.Logging.Logger.Instance, out error);

        // Preferred Initialize overload that accepts an ILogger to receive init logs.
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

        // Detect using an OpenCv Mat
        public DetectionResult[] Detect(Mat mat, string modelPath, string? logDir = null)
        {
            if (mat == null) return Array.Empty<DetectionResult>();
            Cv2.ImEncode(".jpg", mat, out var buf);
            return Detect(buf, modelPath, logDir);
        }

        // Detect using JPEG bytes. Calls Python module 'inference.detect'.
        // Attempts in-memory call first then falls back to a temp file if required.
        public DetectionResult[] Detect(byte[] jpegBuffer, string modelPath, string? logDir = null)
        {
            if (jpegBuffer == null) return Array.Empty<DetectionResult>();

            try
            {
                using (Py.GIL())
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory ?? ".";
                    string pythonScriptDir = Path.Combine(baseDir, "Script");
                    dynamic sys = Py.Import("sys");

                    // Ensure script folder on sys.path
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
                        Logger.LogInfo($"Added '{pythonScriptDir}' to Python sys.path");
                    }
                    else
                    {
                        Logger.LogInfo($"Python sys.path already contains '{pythonScriptDir}'");
                    }

                    // Import inference module
                    dynamic inference;
                    try
                    {
                        inference = Py.Import("inference");
                        Logger.LogInfo("Imported Python module 'inference'.");
                    }
                    catch (PythonException pex)
                    {
                        try
                        {
                            dynamic tb = Py.Import("traceback");
                            string trace = tb.format_exc();
                            string logPath = Path.Combine(baseDir, "python_error_inference_import.log");
                            File.WriteAllText(logPath, trace);
                            Logger.LogError($"Failed to import 'inference'. Trace saved to {logPath}");
                        }
                        catch
                        {
                            Logger.LogError($"Failed to import 'inference': {pex}");
                        }
                        return Array.Empty<DetectionResult>();
                    }

                    // Try calling detect with bytes first, fallback to tempfile
                    dynamic results = null;
                    bool usedFallbackFile = false;
                    string tempFile = "";

                    try
                    {
                        Logger.LogInfo("Calling inference.detect with in-memory buffer");
                        results = inference.detect(jpegBuffer, modelPath, logDir);
                    }
                    catch (PythonException firstEx)
                    {
                        try
                        {
                            dynamic tb = Py.Import("traceback");
                            string trace = tb.format_exc();
                            string logPath = Path.Combine(baseDir, "python_error_inference_detect_first.log");
                            File.WriteAllText(logPath, trace);
                            Logger.LogError($"detect(buf, ...) failed. Trace saved to {logPath}");
                        }
                        catch
                        {
                            Logger.LogError($"detect(buf, ...) raised: {firstEx}");
                        }

                        // Fallback: write bytes to temp file and call detect(tempPath,...)
                        try
                        {
                            tempFile = Path.Combine(Path.GetTempPath(), $"inference_fallback_{Guid.NewGuid():N}.jpg");
                            File.WriteAllBytes(tempFile, jpegBuffer);
                            usedFallbackFile = true;
                            Logger.LogInfo($"Retrying detect with temp file {tempFile}");
                            results = inference.detect(tempFile, modelPath, logDir);
                        }
                        catch (PythonException secondEx)
                        {
                            try
                            {
                                dynamic tb = Py.Import("traceback");
                                string trace = tb.format_exc();
                                string logPath = Path.Combine(baseDir, "python_error_inference_detect_second.log");
                                File.WriteAllText(logPath, trace);
                                Logger.LogError($"detect(tempFile, ...) failed. Trace saved to {logPath}");
                            }
                            catch
                            {
                                Logger.LogError($"detect(tempFile, ...) raised: {secondEx}");
                            }
                            try { if (usedFallbackFile && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                            return Array.Empty<DetectionResult>();
                        }
                        catch (Exception ex)
                        {
                            string logPath = Path.Combine(baseDir, "python_error_inference_detect_second_nonpython.log");
                            File.WriteAllText(logPath, ex.ToString());
                            Logger.LogError($"detect(tempFile, ...) non-Python error: {ex} (see {logPath})");
                            try { if (usedFallbackFile && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                            return Array.Empty<DetectionResult>();
                        }
                    }
                    catch (Exception ex)
                    {
                        string logPath = Path.Combine(baseDir, "python_error_inference_detect_first_nonpython.log");
                        File.WriteAllText(logPath, ex.ToString());
                        Logger.LogError($"detect(buf, ...) non-Python error: {ex} (see {logPath})");
                        return Array.Empty<DetectionResult>();
                    }

                    // Parse results (robust: do not use .Length on Python sequences)
                    var detections = new Collection<DetectionResult>();
                    try
                    {
                        if (results != null)
                        {
                            foreach (dynamic det in results)
                            {
                                string task = null;
                                string className = null;
                                double confidence = 0.0;

                                try { task = det["Task"]?.ToString(); } catch { task = det.GetAttr("Task")?.ToString(); }
                                try { className = det["class"]?.ToString(); } catch { className = det.GetAttr("class")?.ToString(); }

                                // parse confidence defensively
                                try
                                {
                                    confidence = (double)det["confidence"];
                                }
                                catch
                                {
                                    double.TryParse(det["confidence"]?.ToString(), out confidence);
                                }

                                if (task == "detect")
                                {
                                    var box = det["box"];
                                    if (box != null)
                                    {
                                        try
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
                                        catch (Exception)
                                        {
                                            Logger.LogWarning($"Skipping malformed 'box' for class '{className}'");
                                        }
                                    }
                                }
                                else if (task == "obb")
                                {
                                    var rotateBox = det["rotate_box"];
                                    if (rotateBox != null)
                                    {
                                        try
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
                                        catch (Exception)
                                        {
                                            Logger.LogWarning($"Skipping malformed 'rotate_box' for class '{className}'");
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        string logPath = Path.Combine(baseDir, "python_parse_results.log");
                        File.WriteAllText(logPath, ex.ToString());
                        Logger.LogError($"Failed to parse inference results: {ex} (see {logPath})");
                        try { if (usedFallbackFile && File.Exists(tempFile)) File.Delete(tempFile); } catch { }
                        return Array.Empty<DetectionResult>();
                    }

                    // cleanup fallback
                    try { if (usedFallbackFile && File.Exists(tempFile)) File.Delete(tempFile); } catch { }

                    Logger.LogInfo($"detect returned {detections.Count} results (usedFallbackFile={usedFallbackFile})");
                    return detections.ToArray();
                }
            }
            catch (Exception ex)
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "python_error.log");
                File.WriteAllText(logPath, ex.ToString());
                Logger.LogError($"Detection error: {ex} (see {logPath})");
            }

            return Array.Empty<DetectionResult>();
        }

        // Default no-op logger so engine works even if host doesn't wire a logger
        private class NullLogger : ILogger
        {
            public void Info(string message) { }
            public void Error(string message) { }
            public void LogInfo(string message) { }
            public void LogWarning(string message) { }
            public void LogError(string message) { }
            public string GetLogDirectory() => null!;
            public void LogInfo(object export, string tag) { }
        }
    }
}
