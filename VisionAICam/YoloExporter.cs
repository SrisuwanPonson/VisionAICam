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
            TaskType taskType = exportFormat switch
            {
                YoloExportFormat.YoloV5 or YoloExportFormat.YoloV8 => TaskType.Detection,
                YoloExportFormat.YoloV5_OBB or YoloExportFormat.YoloV8_OBB => TaskType.Obb,
                YoloExportFormat.YoloV8_SEG => TaskType.Detection,
                _ => TaskType.Detection
            };

            // if ( annotations type=polygon and tasktype=obb convert rotated box before export)  
            if ((exportFormat == YoloExportFormat.YoloV5_OBB || exportFormat == YoloExportFormat.YoloV8_OBB))
            {
                for (int i = 0; i < project.Annotations.Count; i++)
                {
                    var ann = project.Annotations[i];
                    if (ann.AnnotationType == AnnotationType.Polygon)
                    {
                        var rotatedBox = ConvertPolygonToRotatedBox(ann.Points);
                        if (rotatedBox != null)
                        {
                            project.Annotations[i] = new AnnotationRecord
                            {
                                ImageName = ann.ImageName,
                                Label = ann.Label,
                                AnnotationType = AnnotationType.RotatedBox,
                                Points = rotatedBox
                            };
                        }
                    }
                }
            }

            ExportSet(project, train, Path.Combine(outputFolder, "train"), getImageSize, exportFormat);
            ExportSet(project, val, Path.Combine(outputFolder, "valid"), getImageSize, exportFormat);
            ExportSet(project, test, Path.Combine(outputFolder, "test"), getImageSize, exportFormat);
          
            WriteDataYaml(outputFolder, project.ClassLabels, project.ProjectName,taskType);
        }

        /// <summary>
        /// Converts a polygon to a rotated rectangle (OBB) represented as 4 points.
        /// </summary>
        // This is a simplified placeholder. For production, use a proper minimum-area rectangle algorithm.
        private static List<Point>? ConvertPolygonToRotatedBox(List<Point> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return null;

            // TODO: Implement minimum-area rectangle (rotating calipers or similar).
            // For now, return axis-aligned bounding box as fallback.
            double minX = polygon.Min(p => p.X);
            double minY = polygon.Min(p => p.Y);
            double maxX = polygon.Max(p => p.X);
            double maxY = polygon.Max(p => p.Y);

            return new List<Point>
            {
                new Point(minX, minY),
                new Point(maxX, minY),
                new Point(maxX, maxY),
                new Point(minX, maxY)
            };
        }

        public static List<float>? ConvertPolygonToObbData(List<Point> polygon)
        {
            if (polygon == null || polygon.Count < 3)
                return null;

            // Convert WPF Point to OpenCvSharp.Point2f and scale for precision
            var points = polygon.Select(p => new OpenCvSharp.Point2f((float)p.X * 1000f, (float)p.Y * 1000f)).ToArray();
            var rect = OpenCvSharp.Cv2.MinAreaRect(points);
            var box = rect.Points(); // 4 points in consistent order

            // Convert back to float and scale down, flatten to [x1, y1, ..., x4, y4]
            var obbData = new List<float>(8);
            foreach (var p in box)
            {
                obbData.Add(p.X / 1000f);
                obbData.Add(p.Y / 1000f);
            }
            return obbData;
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

            var annotations = project.Annotations
                .Where(a => imageFileNames.Contains(a.ImageName))
                .Where(a => supportedTypes.Contains(a.AnnotationType))
                .GroupBy(a => a.ImageName);

            foreach (var group in annotations)
            {
                var imageSize = getImageSize(group.Key);
                if (imageSize.Width == 0 || imageSize.Height == 0)
                    continue;

                var lines = group
                    .Select(a => a.ToYoloFormat(imageSize, project.ClassLabels, exportFormat))
                    .Where(line => !string.IsNullOrEmpty(line))
                    .ToList();

                var imageFileName = Path.GetFileName(group.Key);
                var labelFileName = Path.ChangeExtension(imageFileName, ".txt");
                var labelFile = Path.Combine(labelsFolder, labelFileName);
                File.WriteAllLines(labelFile, lines);

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
                YoloExportFormat.YoloV5_OBB or YoloExportFormat.YoloV8_OBB => new List<AnnotationType> { AnnotationType.Polygon, AnnotationType.RotatedBox },
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