using ClearEngine.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using VisionAICam.Pages;

namespace VisionAICam
{
    public class AnnotationRecord
    {
        public int Id { get; set; } // Unique identifier for traceability
        public int ClassId { get; set; } = -1; // Will be resolved from Label

        internal List<double> RawValues;

        public required string ImageName { get; set; }
        public required string Label { get; set; }
        public AnnotationType AnnotationType { get; set; }
        public List<Point> Points { get; set; } = new();

        public string Coordinates
        {
            get
            {
                switch (AnnotationType)
                {
                    case AnnotationType.Rectangle when Points.Count == 2:
                        var x = Math.Min(Points[0].X, Points[1].X);
                        var y = Math.Min(Points[0].Y, Points[1].Y);
                        var w = Math.Abs(Points[1].X - Points[0].X);
                        var h = Math.Abs(Points[1].Y - Points[0].Y);
                        return $"({x:0},{y:0},{w:0},{h:0})";

                    case AnnotationType.RotatedBox when Points.Count == 4:
                        return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));

                    case AnnotationType.Polygon:
                    case AnnotationType.FreePen:
                        return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));

                    default:
                        return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));
                }
            }
        }

        /// <summary>
        /// Resolves ClassId from Label using provided classLabels list.
        /// </summary>
        public void ResolveClassId(List<string> classLabels)
        {
            if (classLabels == null || string.IsNullOrWhiteSpace(Label))
            {
                Logger.Instance.LogWarning($"Label is missing or classLabels not provided for record ID={Id}");
                ClassId = 0;
                return;
            }

            var index = classLabels.FindIndex(c => string.Equals(c.Trim(), Label.Trim(), StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                ClassId = index;
                Logger.Instance.LogInfo($"Resolved label '{Label}' → ClassId={ClassId} for record ID={Id}");
            }
            else
            {
                Logger.Instance.LogWarning($"Unmapped label '{Label}' — defaulting ClassId to 0 for record ID={Id}");
                ClassId = 0;
            }
        }

        /// <summary>
        /// Returns YOLO format string for Rectangle, OBB, or Segmentation.
        /// </summary>
        public string ToYoloFormat(Size imageSize, List<string> classLabels, YoloExportFormat format = YoloExportFormat.YoloV8)
        {
            ResolveClassId(classLabels);

            if (ClassId < 0 || ClassId >= classLabels.Count)
                return string.Empty;

            double imgW = imageSize.Width;
            double imgH = imageSize.Height;
            if (imgW == 0 || imgH == 0) return string.Empty;

            switch (AnnotationType)
            {
                case AnnotationType.Rectangle when Points.Count == 2:
                    {
                        double x1 = Points[0].X;
                        double y1 = Points[0].Y;
                        double x2 = Points[1].X;
                        double y2 = Points[1].Y;

                        double xMin = Math.Min(x1, x2);
                        double yMin = Math.Min(y1, y2);
                        double xMax = Math.Max(x1, x2);
                        double yMax = Math.Max(y1, y2);

                        double width = xMax - xMin;
                        double height = yMax - yMin;
                        double xCenter = xMin + width / 2.0;
                        double yCenter = yMin + height / 2.0;

                        double xCenterNorm = xCenter / imgW;
                        double yCenterNorm = yCenter / imgH;
                        double widthNorm = width / imgW;
                        double heightNorm = height / imgH;

                        return format == YoloExportFormat.YoloV8_OBB
                            ? $"{ClassId} {xCenterNorm:F6} {yCenterNorm:F6} {widthNorm:F6} {heightNorm:F6} 0.000000"
                            : $"{ClassId} {xCenterNorm:F6} {yCenterNorm:F6} {widthNorm:F6} {heightNorm:F6}";
                    }

                case AnnotationType.RotatedBox when Points.Count == 4:
                    {
                        var normalizedPoints = Points
                            .Select(p => $"{(p.X / imgW):F6} {(p.Y / imgH):F6}")
                            .ToArray();

                        return $"{ClassId} {string.Join(" ", normalizedPoints)}";
                    }

                case AnnotationType.Polygon when Points.Count >= 3:
                case AnnotationType.FreePen when Points.Count >= 2:
                    {
                        var normPoints = Points.Select(p => $"{(p.X / imgW):F6} {(p.Y / imgH):F6}");
                        return $"{ClassId} {string.Join(" ", normPoints)}";
                    }

                default:
                    return string.Empty;
            }
        }
    }
}