using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using VisionAICam.Core;

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
    }
}