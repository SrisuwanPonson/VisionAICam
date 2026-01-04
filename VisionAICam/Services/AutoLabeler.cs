using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using ClearEngine.Logging;
using ClearEngine.Model.Inference;

namespace VisionAICam.Services
{
    /// <summary>
    /// Lightweight helper that runs model inference for the UI services.
    /// Called by AutoLabelerService.RunAutoLabelingAsync.
    /// </summary>
    public static class AutoLabeler
    {
        private static readonly ILogger _log = Logger.Instance;

        /// <summary>
        /// Run inference on the currently selected image (ProjectSession.CurrentImagePath expected to be set).
        /// Returns true when inference ran and at least one annotation was added.
        /// </summary>
        public static async Task<bool> PerformAutoLabelingInferenceAsync(string modelPath, double confidenceThreshold = 0.5)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(modelPath))
                {
                    _log.LogWarning("AutoLabeler: modelPath is empty.");
                    return false;
                }

                var imagePath = ProjectSession.CurrentImagePath;
                if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                {
                    _log.LogWarning("AutoLabeler: no current image to run inference on.");
                    return false;
                }

                _log.LogInfo($"AutoLabeler: running inference on '{imagePath}' using '{modelPath}', threshold={confidenceThreshold:F2}");

                // Ensure inference engine is available (use TryCreate so we do not throw)
                if (!ClearEngine.Model.Inference.InferenceEngine.TryCreate(null, Logger.Instance, out var engine, out var initError))
                {
                    _log.LogError($"AutoLabeler: failed to create inference engine: {initError}");
                    return false;
                }

                // Read image bytes and call engine.Detect on background thread
                byte[] imageBytes = await Task.Run(() => File.ReadAllBytes(imagePath)).ConfigureAwait(false);

                DetectionResult[] results;
                try
                {
                    // Use engine.Detect(byte[], modelPath, logDir)
                    string logDir = Logger.Instance.GetLogDirectory();
                    results = await Task.Run(() => engine.Detect(imageBytes, modelPath, logDir)).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _log.LogError($"AutoLabeler: inference Detect failed: {ex}");
                    return false;
                }

                if (results == null || results.Length == 0)
                {
                    _log.LogInfo("AutoLabeler: no detections returned.");
                    return false;
                }

                // Prepare annotations container (project level)
                var project = ProjectSession.CurrentProject;
                if (project == null)
                {
                    _log.LogWarning("AutoLabeler: no active project; cannot add annotations.");
                    return false;
                }

                var added = new List<AnnotationRecord>();
                var imageName = Path.GetFileName(imagePath);

                foreach (var det in results)
                {
                    try
                    {
                        if (det == null) continue;
                        if (string.IsNullOrWhiteSpace(det.ClassName)) continue;
                        if (det.Confidence < confidenceThreshold) continue;

                        if (det.Task == "detect")
                        {
                            // Expect "x1,y1,x2,y2"
                            var parts = det.Box?.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (parts == null || parts.Length != 4) continue;
                            if (!double.TryParse(parts[0], out double x1)) continue;
                            if (!double.TryParse(parts[1], out double y1)) continue;
                            if (!double.TryParse(parts[2], out double x2)) continue;
                            if (!double.TryParse(parts[3], out double y2)) continue;

                            var rec = new AnnotationRecord
                            {
                                ImageName = imageName,
                                Label = det.ClassName,
                                AnnotationType = VisionAICam.Pages.AnnotationType.Rectangle,
                                Points = new List<Point>
                                {
                                    new Point(x1, y1),
                                    new Point(x2, y2)
                                }
                            };

                            added.Add(rec);
                        }
                        else if (det.Task == "obb")
                        {
                            // Expect "cx,cy,w,h,angle"
                            var parts = det.Box?.Split(',', StringSplitOptions.RemoveEmptyEntries);
                            if (parts == null || parts.Length != 5) continue;
                            if (!double.TryParse(parts[0], out double cx)) continue;
                            if (!double.TryParse(parts[1], out double cy)) continue;
                            if (!double.TryParse(parts[2], out double w)) continue;
                            if (!double.TryParse(parts[3], out double h)) continue;
                            if (!double.TryParse(parts[4], out double angleDeg)) continue;

                            // Convert rotated box center/size/angle -> 4 corner points (order: TL, TR, BR, BL)
                            var values8 = GetRotatedBoxAs8Values(cx, cy, w, h, angleDeg);
                            var points = new List<Point>
                            {
                                new Point(values8[0], values8[1]),
                                new Point(values8[2], values8[3]),
                                new Point(values8[4], values8[5]),
                                new Point(values8[6], values8[7])
                            };

                            var rec = new AnnotationRecord
                            {
                                ImageName = imageName,
                                Label = det.ClassName,
                                AnnotationType = VisionAICam.Pages.AnnotationType.RotatedBox,
                                RawValues = values8,
                                Points = points
                            };

                            added.Add(rec);
                        }
                        else
                        {
                            // Unknown task -> skip
                            _log.LogInfo($"AutoLabeler: skipping result with unknown Task='{det.Task}'");
                        }
                    }
                    catch (Exception ex)
                    {
                        _log.LogWarning($"AutoLabeler: failed to convert detection to annotation: {ex.Message}");
                    }
                }

                if (added.Count == 0)
                {
                    _log.LogInfo("AutoLabeler: no annotations passed threshold or were convertible.");
                    return false;
                }

                // Assign simple incremental Ids and resolve ClassId later if necessary
                int nextId = (project.Annotations?.Count ?? 0) + 1;
                foreach (var a in added)
                {
                    a.Id = nextId++;
                    project.Annotations.Add(a);
                }

                // Persist to session so UI can pick them up
                ProjectSession.Annotations = project.Annotations;

                _log.LogInfo($"AutoLabeler: added {added.Count} annotations to project '{project.ProjectName}' for image '{imageName}'.");

                return true;
            }
            catch (Exception ex)
            {
                try { _log.LogError($"AutoLabeler.PerformAutoLabelingInferenceAsync failed: {ex}"); } catch { }
                return false;
            }
        }

        // Utility: compute four corner points from center/size/angle (degrees).
        // Returns [x0,y0,x1,y1,x2,y2,x3,y3] in the same order used elsewhere.
        private static List<double> GetRotatedBoxAs8Values(double cx, double cy, double w, double h, double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            double cosA = Math.Cos(angle);
            double sinA = Math.Sin(angle);

            double w2 = w / 2.0;
            double h2 = h / 2.0;

            var corners = new List<Point>
            {
                new Point(cx - w2 * cosA + h2 * sinA, cy - w2 * sinA - h2 * cosA), // top-left
                new Point(cx + w2 * cosA + h2 * sinA, cy + w2 * sinA - h2 * cosA), // top-right
                new Point(cx + w2 * cosA - h2 * sinA, cy + w2 * sinA + h2 * cosA), // bottom-right
                new Point(cx - w2 * cosA - h2 * sinA, cy - w2 * sinA + h2 * cosA)  // bottom-left
            };

            return corners.SelectMany(p => new[] { p.X, p.Y }).ToList();
        }

        /// <summary>
        /// Helper used by UI/service to prepare a training dataset (delegates to YoloExporter).
        /// Kept here for convenience.
        /// </summary>
        public static bool PrepareTrainingDataset(int minImagesPerClass = 10, YoloExportFormat format = YoloExportFormat.YoloV8)
        {
            try
            {
                var project = ProjectSession.CurrentProject;
                if (project == null)
                {
                    _log.LogWarning("AutoLabeler.PrepareTrainingDataset: no current project.");
                    return false;
                }

                // Choose an output folder under application base
                string outRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "DatasetExports", $"{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(outRoot);

                // Provide a simple getImageSize func
                Size GetSize(string imageName)
                {
                    var path = project.ImagePaths.FirstOrDefault(p => Path.GetFileName(p) == imageName);
                    if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return new Size(0, 0);
                    try
                    {
                        using var img = System.Drawing.Image.FromFile(path);
                        return new Size(img.Width, img.Height);
                    }
                    catch { return new Size(0, 0); }
                }

                YoloExporter.ExportWithSplit(project, outRoot, GetSize, trainRatio: 0.7, valRatio: 0.2, testRatio: 0.1, exportFormat: format);
                _log.LogInfo($"AutoLabeler: prepared training dataset at {outRoot}");
                return true;
            }
            catch (Exception ex)
            {
                try { _log.LogError($"PrepareTrainingDataset failed: {ex}"); } catch { }
                return false;
            }
        }
    }
}