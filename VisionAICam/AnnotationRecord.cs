using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using VisionAICam.Pages;

namespace VisionAICam
{
    public class AnnotationRecord
    {
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
                        // OBB: 4 points (x1,y1,x2,y2,x3,y3,x4,y4)
                        return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));
                    case AnnotationType.Polygon:
                    case AnnotationType.FreePen:
                        // Polygon or freehand: all points
                        return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));
                    default:
                        return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));
                }
            }
        }

        /// <summary>
        /// Returns YOLO format string for Rectangle, OBB, or Segmentation.
        /// </summary>
        public string ToYoloFormat(Size imageSize, List<string> classLabels, YoloExportFormat format = YoloExportFormat.YoloV8)
        {
            int classId = classLabels.IndexOf(Label);
            if (classId < 0) classId = 0;

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

                        return $"{classId} {xCenterNorm:F6} {yCenterNorm:F6} {widthNorm:F6} {heightNorm:F6}";
                    }
                case AnnotationType.RotatedBox when Points.Count == 4:
                    {
                        // YOLO OBB: <class_id> x1 y1 x2 y2 x3 y3 x4 y4 (normalized)
                        var normPoints = Points.Select(p => $"{(p.X / imgW):F6} {(p.Y / imgH):F6}");
                        return $"{classId} {string.Join(" ", normPoints)}";
                    }
                case AnnotationType.Polygon when Points.Count >= 3:
                    {
                        // YOLO Segmentation: <class_id> x1 y1 x2 y2 ... xn yn (normalized)
                        var normPoints = Points.Select(p => $"{(p.X / imgW):F6} {(p.Y / imgH):F6}");
                        return $"{classId} {string.Join(" ", normPoints)}";
                    }
                case AnnotationType.FreePen when Points.Count >= 2:
                    {
                        // Treat freehand as segmentation
                        var normPoints = Points.Select(p => $"{(p.X / imgW):F6} {(p.Y / imgH):F6}");
                        return $"{classId} {string.Join(" ", normPoints)}";
                    }
                default:
                    return string.Empty;
            }
        }
    }
}
