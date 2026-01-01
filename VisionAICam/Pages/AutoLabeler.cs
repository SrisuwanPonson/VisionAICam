using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using VisionAICam.Core;
using VisionAICam.Utilities; // for TrainingHelper
using System.Net;
using ClearEngine.Model.Inference;
using System.Reflection;
using ClearEngine.Logging;

namespace VisionAICam.Pages
{
    public static partial class AutoLabeler
    {
        /// <summary>
        /// Run model-backed auto-labeling for the current image.
        /// (Existing placeholder left minimal — inference-based labeling belongs in a different helper.)
        /// </summary>
        public static void PerformAutoLabeling()
        {
            var master = MasterController.Instance;
            if (master == null)
                throw new InvalidOperationException("MasterController.Instance is not initialized. Initialize MasterController before calling PerformAutoLabeling.");

            var dataSetPage = master.DataSetPage;
            var modelPage = master.ModelPage;

            if (dataSetPage == null || modelPage == null)
                throw new InvalidOperationException("DataSetPage or ModelPage is not available from MasterController.");

            // NOTE: this method intentionally does not perform long-running work on the UI thread.
            // Implement inference flow separately (see DiagnosticsPage/Production for examples).
        }

        /// <summary>
        /// Prepares a YOLO-style training dataset from the current project's annotations only when
        /// each class has at least <paramref name="minImagesPerClass"/> distinct images annotated.
        /// Exports into a timestamped folder under Documents and invokes YoloExporter.ExportWithSplit.
        /// Returns true when export succeeded.
        /// </summary>
        public static bool PrepareTrainingDataset(int minImagesPerClass = 10, YoloExportFormat exportFormat = YoloExportFormat.YoloV8)
        {
            try
            {
                // Resolve current project and annotations
                var project = ProjectSession.CurrentProject;
                var allAnnotations = ProjectSession.Annotations ?? new List<AnnotationRecord>();
                var allImagePaths = ProjectSession.ImagePaths ?? new List<string>();

                if (project == null || allImagePaths.Count == 0 || allAnnotations.Count == 0)
                {
                    MessageBox.Show("No project / images / annotations available. Load or create a project first.", "Prepare Training", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Ensure we have class labels to evaluate; fall back to labels discovered in annotations
                var classLabels = project.ClassLabels != null && project.ClassLabels.Count > 0
                    ? project.ClassLabels
                    : allAnnotations.Select(a => a.Label ?? "").Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                if (classLabels.Count == 0)
                {
                    MessageBox.Show("No class labels found. Add labels before preparing a training dataset.", "Prepare Training", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                // Count distinct images per class
                var imagesPerClass = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (var lbl in classLabels) imagesPerClass[lbl] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var ann in allAnnotations)
                {
                    var img = ann.ImageName ?? "";
                    if (string.IsNullOrWhiteSpace(img) || string.IsNullOrWhiteSpace(ann.Label)) continue;
                    if (!imagesPerClass.ContainsKey(ann.Label)) imagesPerClass[ann.Label] = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    imagesPerClass[ann.Label].Add(img);
                }

                // Find classes that fail the minimum-image requirement
                var failing = imagesPerClass
                    .Where(kv => kv.Value.Count < minImagesPerClass)
                    .Select(kv => (Label: kv.Key, Count: kv.Value.Count))
                    .ToList();

                if (failing.Count > 0)
                {
                    string msg = "The following classes do not meet the minimum image requirement:\n\n" +
                                 string.Join("\n", failing.Select(f => $"{f.Label}: {f.Count} images (need {minImagesPerClass})")) +
                                 "\n\nPlease label more images before preparing training data.";
                    MessageBox.Show(msg, "Insufficient Images", MessageBoxButton.OK, MessageBoxImage.Information);
                    return false;
                }

                // Build a filtered AnnotationProject that includes only annotations for classes in classLabels
                // and only the images that contain at least one such annotation.
                var selectedLabels = classLabels.ToList(); // keep original order

                var filteredAnnotations = allAnnotations
                    .Where(a => !string.IsNullOrWhiteSpace(a.Label) && selectedLabels.Contains(a.Label, StringComparer.OrdinalIgnoreCase))
                    .ToList();

                var includedImageNames = filteredAnnotations.Select(a => a.ImageName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                var filteredImagePaths = allImagePaths
                    .Where(p => includedImageNames.Contains(Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                    .ToList();

                if (filteredImagePaths.Count == 0)
                {
                    MessageBox.Show("No images found for the selected annotations.", "Prepare Training", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return false;
                }

                var exportProject = new AnnotationProject
                {
                    ProjectName = project.ProjectName ?? $"Export_{DateTime.Now:yyyyMMdd_HHmmss}",
                    ImagePaths = filteredImagePaths,
                    ClassLabels = selectedLabels,
                    Annotations = filteredAnnotations,
                    SelectedImageIndex = 0
                };

                // Choose an output folder inside user's Documents
                string docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                string outFolder = Path.Combine(docs, $"TrainingExport_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(outFolder);

                // Helper to determine image size
                System.Windows.Size GetImageSize(string imageName)
                {
                    // find full path by matching file name
                    var full = filteredImagePaths.FirstOrDefault(p => string.Equals(Path.GetFileName(p), imageName, StringComparison.OrdinalIgnoreCase));
                    if (string.IsNullOrWhiteSpace(full) || !File.Exists(full))
                        return new System.Windows.Size(0, 0);

                    try
                    {
                        using var img = Image.FromFile(full);
                        return new System.Windows.Size(img.Width, img.Height);
                    }
                    catch
                    {
                        return new System.Windows.Size(0, 0);
                    }
                }

                // Export dataset with split (train/valid/test)
                YoloExporter.ExportWithSplit(exportProject, outFolder, imageName => GetImageSize(imageName), trainRatio: 0.7, valRatio: 0.2, testRatio: 0.1, exportFormat: exportFormat);

                MessageBox.Show($"Training dataset exported to:\n{outFolder}\n\nFormat: {exportFormat}\nClasses: {selectedLabels.Count}\nImages: {filteredImagePaths.Count}", "Export Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to prepare training dataset: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }

        // --- Add these methods inside the existing AutoLabeler static class ---

        /// <summary>
        /// Run inference on the currently selected image (via MasterController/DataSetPage),
        /// convert detection results into AnnotationRecord entries and add them to the project.
        /// Returns true when inference ran and at least one annotation was added.
        /// This method runs heavy work off the UI thread and updates UI via Dispatcher when needed.
        /// </summary>
        // Replace the existing PerformAutoLabelingInferenceAsync method with this version
        public static async Task<bool> PerformAutoLabelingInferenceAsync(string modelPath, double confidenceThreshold = 0.5)
        {
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                MessageBox.Show("Model file not found. Provide a valid model path before running inference.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var master = MasterController.Instance;
            if (master == null)
            {
                MessageBox.Show("MasterController.Instance is not initialized.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var dataSetPage = master.DataSetPage;
            if (dataSetPage == null)
            {
                MessageBox.Show("DataSetPage is not available.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var currentImagePath = ProjectSession.CurrentImagePath ?? master.DataSetPage?.GetType().GetProperty("CurrentImagePath", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(master.DataSetPage) as string;
            if (string.IsNullOrWhiteSpace(currentImagePath) || !File.Exists(currentImagePath))
            {
                MessageBox.Show("No current image available for auto-labeling.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            byte[] imageBytes;
            try
            {
                imageBytes = await Task.Run(() => File.ReadAllBytes(currentImagePath));
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to read image bytes: {ex.Message}", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // Ensure InferenceEngine exists (Create if necessary)
            if (!InferenceEngine.TryCreate(null, Log, out var engine, out var createError))
            {
                MessageBox.Show($"Failed to initialize inference engine: {createError}", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (engine == null)
            {
                MessageBox.Show("Inference engine instance could not be created.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            // Use fully-qualified ClearEngine.Model.Inference.DetectionResult to avoid conflict
            ClearEngine.Model.Inference.DetectionResult[] results = Array.Empty<ClearEngine.Model.Inference.DetectionResult>();
            try
            {
                results = await Task.Run(() =>
                {
                    var res = engine.Detect(imageBytes, modelPath, Log.GetLogDirectory());

                    if (res == null)
                        return Array.Empty<ClearEngine.Model.Inference.DetectionResult>();

                    if (res is ClearEngine.Model.Inference.DetectionResult[] arr)
                        return arr;

                    if (res is IEnumerable<ClearEngine.Model.Inference.DetectionResult> ie)
                        return ie.ToArray();

                    try
                    {
                        return (res as IEnumerable<ClearEngine.Model.Inference.DetectionResult>)?.ToArray() ?? Array.Empty<ClearEngine.Model.Inference.DetectionResult>();
                    }
                    catch
                    {
                        return Array.Empty<ClearEngine.Model.Inference.DetectionResult>();
                    }
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Inference failed: {ex.Message}", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            if (results == null || results.Length == 0)
            {
                MessageBox.Show("Inference returned no detections.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Information);
                return false;
            }

            var addedAny = false;
            var project = ProjectSession.CurrentProject;
            if (project == null)
            {
                MessageBox.Show("No active project to add annotations to.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }

            var projectAnnotations = ProjectSession.Annotations ?? new List<AnnotationRecord>();
            var imageName = Path.GetFileName(currentImagePath);

            foreach (var det in results)
            {
                try
                {
                    if (det == null) continue;
                    if (det.Confidence < confidenceThreshold) continue;

                    bool isDuplicate = projectAnnotations.Any(a =>
                        string.Equals(a.ImageName, imageName, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(a.Label, det.ClassName, StringComparison.OrdinalIgnoreCase) &&
                        AreBoxesSimilar(a, det.Box, a.AnnotationType)
                    );
                    if (isDuplicate) continue;

                    AnnotationRecord rec = null;

                    if (string.Equals(det.Task, "obb", StringComparison.OrdinalIgnoreCase))
                    {
                        var parts = det.Box?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                        if (parts.Length >= 4)
                        {
                            double cx = Double.Parse(parts[0]);
                            double cy = Double.Parse(parts[1]);
                            double w = Double.Parse(parts[2]);
                            double h = Double.Parse(parts[3]);
                            double angle = parts.Length >= 5 ? Double.Parse(parts[4]) : 0.0;

                            var raw = GetRotatedBoxAs8Values(cx, cy, w, h, angle);
                            var points = new List<System.Windows.Point>
                    {
                        new System.Windows.Point((int)raw[0], (int)raw[1]),
                        new System.Windows.Point((int)raw[2], (int)raw[3]),
                        new System.Windows.Point((int)raw[4], (int)raw[5]),
                        new System.Windows.Point((int)raw[6], (int)raw[7])
                    };

                            rec = new AnnotationRecord
                            {
                                ImageName = imageName,
                                Label = det.ClassName,
                                AnnotationType = AnnotationType.RotatedBox,
                                RawValues = raw,
                                Points = points
                            };
                        }
                    }
                    else
                    {
                        var parts = det.Box?.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
                        if (parts.Length >= 4)
                        {
                            double x1 = Double.Parse(parts[0]);
                            double y1 = Double.Parse(parts[1]);
                            double x2 = Double.Parse(parts[2]);
                            double y2 = Double.Parse(parts[3]);

                            var p0 = new System.Windows.Point(x1, y1);
                            var p1 = new System.Windows.Point(x2, y2);

                            rec = new AnnotationRecord
                            {
                                ImageName = imageName,
                                Label = det.ClassName,
                                AnnotationType = AnnotationType.Rectangle,
                                Points = new List<System.Windows.Point> { p0, p1 }
                            };
                        }
                    }

                    if (rec != null)
                    {
                        projectAnnotations.Add(rec);
                        addedAny = true;
                    }
                }
                catch
                {
                    // continue
                }
            }

            if (addedAny)
            {
                ProjectSession.Annotations = projectAnnotations;

                try
                {
                    var refreshMethod = dataSetPage.GetType().GetMethod("RefreshAnnotations", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (refreshMethod != null)
                    {
                        dataSetPage.Dispatcher.Invoke(() => refreshMethod.Invoke(dataSetPage, null));
                    }
                    else
                    {
                        var saveMethod = dataSetPage.GetType().GetMethod("SaveAnnotations_Click", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new Type[] { typeof(object), typeof(RoutedEventArgs) }, null);
                        if (saveMethod != null)
                        {
                            dataSetPage.Dispatcher.Invoke(() => saveMethod.Invoke(dataSetPage, new object[] { null, new RoutedEventArgs() }));
                        }
                    }
                }
                catch { }

                MessageBox.Show($"Auto-labeling added {projectAnnotations.Count(a => a.ImageName == imageName)} annotations for image {imageName}.", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show("No new annotations created from inference (confidence threshold / duplicates).", "Auto Labeler", MessageBoxButton.OK, MessageBoxImage.Information);
            }

            return addedAny;
        }

        /// <summary>
        /// Small utility: determines if an existing annotation's box is similar to an inferred box (simple string compare or numeric tolerance).
        /// </summary>
        private static bool AreBoxesSimilar(AnnotationRecord existing, string inferredBox, AnnotationType existingType)
        {
            try
            {
                if (existingType == AnnotationType.RotatedBox && existing.RawValues != null && inferredBox != null)
                {
                    // Compare center coordinates and size within a small tolerance
                    var parts = inferredBox.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s)).ToArray();
                    if (parts.Length >= 4)
                    {
                        double cx = parts[0], cy = parts[1], w = parts[2], h = parts[3];
                        var raw = existing.RawValues;
                        if (raw.Count >= 8)
                        {
                            double exCx = (raw[0] + raw[2] + raw[4] + raw[6]) / 4.0;
                            double exCy = (raw[1] + raw[3] + raw[5] + raw[7]) / 4.0;
                            return Math.Abs(exCx - cx) < 4.0 && Math.Abs(exCy - cy) < 4.0;
                        }
                    }
                }
                else if (existingType == AnnotationType.Rectangle && existing.Points != null && existing.Points.Count >= 2)
                {
                    var parts = inferredBox.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(s => double.Parse(s)).ToArray();
                    if (parts.Length >= 4)
                    {
                        double ix1 = parts[0], iy1 = parts[1], ix2 = parts[2], iy2 = parts[3];
                        var e0 = existing.Points[0];
                        var e1 = existing.Points[1];
                        double ex1 = Math.Min(e0.X, e1.X), ey1 = Math.Min(e0.Y, e1.Y), ex2 = Math.Max(e0.X, e1.X), ey2 = Math.Max(e0.Y, e1.Y);
                        // overlap / proximity heuristic
                        return Math.Abs(ex1 - ix1) < 4.0 && Math.Abs(ey1 - iy1) < 4.0 && Math.Abs(ex2 - ix2) < 4.0 && Math.Abs(ey2 - iy2) < 4.0;
                    }
                }
            }
            catch
            {
                // ignore parse errors
            }
            return false;
        }

        /// <summary>
        /// Converts rotated box specified by center cx,cy, width, height and angle (degrees)
        /// into 8 double values [x1,y1,x2,y2,x3,y3,x4,y4] for the rectangle corners.
        /// </summary>
        private static List<double> GetRotatedBoxAs8Values(double cx, double cy, double w, double h, double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            double cosA = Math.Cos(angle);
            double sinA = Math.Sin(angle);

            double w2 = w / 2.0;
            double h2 = h / 2.0;

            var corners = new List<System.Windows.Point>
    {
        new System.Windows.Point(cx - w2 * cosA + h2 * sinA, cy - w2 * sinA - h2 * cosA), // top-left
        new System.Windows.Point(cx + w2 * cosA + h2 * sinA, cy + w2 * sinA - h2 * cosA), // top-right
        new System.Windows.Point(cx + w2 * cosA - h2 * sinA, cy + w2 * sinA + h2 * cosA), // bottom-right
        new System.Windows.Point(cx - w2 * cosA - h2 * sinA, cy - w2 * sinA + h2 * cosA)  // bottom-left
    };

            return corners.SelectMany(p => new[] { p.X, p.Y }).ToList();
        }

        // --- Add inside the AutoLabeler class (near top with other helpers/fields) ---
        private static ILogger Log => Logger.Instance;
    }
}