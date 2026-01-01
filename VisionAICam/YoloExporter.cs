using ClearEngine.Logging;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using VisionAICam.Pages;

namespace VisionAICam
{
    public enum TaskType
    {
        [Description("Object Detection (AABB)")]
        Detection,

        [Description("Object Detection (OBB)")]
        Obb,

        [Description("Image Classification")]
        Classification,

        [Description("Segmentation")]
        Segmentation,

        [Description("Keypoint Detection")]
        Keypoints
    }

    public static class TaskTypeExtensions
    {
        public static string ToYamlString(this TaskType taskType)
        {
            return taskType switch
            {
                TaskType.Detection => "detection",
                TaskType.Classification => "classification",
                TaskType.Obb => "obb",
                _ => "unknown"
            };
        }
    }

    public enum YoloExportFormat
    {
        YoloV5,
        YoloV8,
        YoloV5_OBB,
        YoloV8_OBB,
        YoloV8_SEG,
    }

    public static class YoloExporter
    {
        public static void ExportWithSplit(
            AnnotationProject project,
            string outputFolder,
            Func<string, Size> getImageSize,
            double trainRatio = 0.7,
            double valRatio = 0.2,
            double testRatio = 0.1,
            YoloExportFormat exportFormat = YoloExportFormat.YoloV8)
        {
            if (project == null || project.ImagePaths.Count == 0)
                return;

            if (Directory.Exists(outputFolder))
            {
                Directory.Delete(outputFolder, recursive: true);
            }
            Directory.CreateDirectory(outputFolder);

            var (train, val, test) = DatasetSplitter.Split(project.ImagePaths, trainRatio, valRatio, testRatio);

            ExportSet(project, train, Path.Combine(outputFolder, "train"), getImageSize, exportFormat);
            ExportSet(project, val, Path.Combine(outputFolder, "valid"), getImageSize, exportFormat);
            ExportSet(project, test, Path.Combine(outputFolder, "test"), getImageSize, exportFormat);
            TaskType taskType = exportFormat switch
            {
                YoloExportFormat.YoloV5 or YoloExportFormat.YoloV8 => TaskType.Detection,
                YoloExportFormat.YoloV5_OBB or YoloExportFormat.YoloV8_OBB => TaskType.Obb,
                YoloExportFormat.YoloV8_SEG => TaskType.Detection,
                _ => TaskType.Detection
            };
            WriteDataYaml(outputFolder, project.ClassLabels, project.ProjectName,taskType);
        }

        private static void ExportSet(
    AnnotationProject project,
    List<string> imagePaths,
    string setFolder,
    Func<string, Size> getImageSize,
    YoloExportFormat exportFormat)
        {
            var imagesFolder = Path.Combine(setFolder, "images");
            var labelsFolder = Path.Combine(setFolder, "labels");
            Directory.CreateDirectory(imagesFolder);
            Directory.CreateDirectory(labelsFolder);

            var imageFileNames = new HashSet<string>(
                imagePaths.Select(p => Path.GetFileName(p)),
                StringComparer.OrdinalIgnoreCase);

            var supportedTypes = GetSupportedAnnotationTypes(exportFormat);
            var readableTypes = string.Join(", ", supportedTypes.Select(t => t.ToString()));
            Logger.Instance.LogInfo($"Supported types: {readableTypes}");

            var annotationsByImage = project.Annotations
                .Where(a => imageFileNames.Contains(a.ImageName))
                .GroupBy(a => a.ImageName);

            foreach (var group in annotationsByImage)
            {
                var imageSize = getImageSize(group.Key);
                if (imageSize.Width == 0 || imageSize.Height == 0)
                {
                    Logger.Instance.LogWarning($"Skipped image '{group.Key}' due to invalid size: {imageSize.Width}x{imageSize.Height}");
                    continue;
                }

                var imageFileName = Path.GetFileName(group.Key);
                var labelFileName = Path.ChangeExtension(imageFileName, ".txt");
                var labelFile = Path.Combine(labelsFolder, labelFileName);

                var lines = new List<string>();

                foreach (var a in group)
                {
                    if (!supportedTypes.Contains(a.AnnotationType))
                    {
                        Logger.Instance.LogWarning($"Skipped annotation in '{a.ImageName}' due to unsupported type: {a.AnnotationType}");
                        continue;
                    }

                    var line = a.ToYoloFormat(imageSize, project.ClassLabels, exportFormat);
                    if (string.IsNullOrEmpty(line))
                    {
                        string labelText = (a.ClassId >= 0 && a.ClassId < project.ClassLabels.Count)
                            ? project.ClassLabels[a.ClassId]
                            : "(unknown)";

                        Logger.Instance.LogWarning(
                            $"Skipped empty annotation output in '{a.ImageName}' for Annotation ID: {a.Id}, ClassId: {a.ClassId}, Label: {labelText}, Type: {a.AnnotationType}"
                        );
                        continue;
                    }

                    lines.Add(line);

                    if ((exportFormat == YoloExportFormat.YoloV5_OBB || exportFormat == YoloExportFormat.YoloV8_OBB)
                        && a.AnnotationType == AnnotationType.RotatedBox)
                    {
                        string labelText = (a.ClassId >= 0 && a.ClassId < project.ClassLabels.Count)
                            ? project.ClassLabels[a.ClassId]
                            : "(unknown)";

                        Logger.Instance.LogInfo($"ClassId: {a.ClassId}, Label: {labelText}, OBB 8-Data: {line}");
                    }
                }

                if (lines.Count > 0)
                {
                    File.WriteAllLines(labelFile, lines);
                    Logger.Instance.LogInfo($"Label file written: {labelFile} with {lines.Count} annotations");
                }
                else
                {
                    Logger.Instance.LogInfo($"Processed 0 annotations for {imageFileName}, skipped writing label file: {labelFile}");
                }

                var srcImagePath = project.ImagePaths.FirstOrDefault(p => Path.GetFileName(p) == imageFileName);
                if (!string.IsNullOrEmpty(srcImagePath))
                {
                    var destImagePath = Path.Combine(imagesFolder, imageFileName);
                    if (!File.Exists(destImagePath))
                        File.Copy(srcImagePath, destImagePath, overwrite: false);
                }
            }
        }

        private static List<AnnotationType> GetSupportedAnnotationTypes(YoloExportFormat format)
        {
            return format switch
            {
                YoloExportFormat.YoloV5 or YoloExportFormat.YoloV8 => new List<AnnotationType> { AnnotationType.Rectangle },
                YoloExportFormat.YoloV5_OBB or YoloExportFormat.YoloV8_OBB => new List<AnnotationType> { AnnotationType.Polygon,AnnotationType.RotatedBox },
                YoloExportFormat.YoloV8_SEG => new List<AnnotationType> { AnnotationType.Polygon, AnnotationType.FreePen },
                _ => new List<AnnotationType>()
            };
        }

        private static void WriteDataYaml(string outputFolder, List<string> classLabels, string projectName, TaskType taskType)
        {
            var classNames = string.Join(", ", classLabels.Select(n => $"'{n}'"));
            var yamlBuilder = new StringBuilder();

            yamlBuilder.AppendLine("train: ../train/images");
            yamlBuilder.AppendLine("val: ../valid/images");
            yamlBuilder.AppendLine("test: ../test/images");
            yamlBuilder.AppendLine();
            yamlBuilder.AppendLine($"nc: {classLabels.Count}");
            yamlBuilder.AppendLine($"names: [{classNames}]");
            yamlBuilder.AppendLine($"task: {taskType.ToYamlString()}");
            yamlBuilder.AppendLine();
            yamlBuilder.AppendLine("workspace: ");
            yamlBuilder.AppendLine($"project: {projectName ?? "UnnamedProject"}");
            yamlBuilder.AppendLine("version: ");
            yamlBuilder.AppendLine("license: ");
            yamlBuilder.AppendLine("url: ");

            var yamlPath = Path.Combine(outputFolder, "data.yaml");
            File.WriteAllText(yamlPath, yamlBuilder.ToString().Trim());
        }

    }
}