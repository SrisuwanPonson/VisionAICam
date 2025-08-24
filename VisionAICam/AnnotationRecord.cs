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
                if (AnnotationType == AnnotationType.Rectangle && Points.Count == 2)
                {
                    var x = Math.Min(Points[0].X, Points[1].X);
                    var y = Math.Min(Points[0].Y, Points[1].Y);
                    var w = Math.Abs(Points[1].X - Points[0].X);
                    var h = Math.Abs(Points[1].Y - Points[0].Y);
                    return $"({x:0},{y:0},{w:0},{h:0})";
                }
                return string.Join(";", Points.Select(p => $"({p.X:0},{p.Y:0})"));
            }
        }

        /// <summary>
        /// Returns YOLOv8 format: <class_id> <x_center> <y_center> <width> <height>
        /// All values are normalized (0..1) relative to image size.
        /// </summary>
        public string ToYoloFormat(Size imageSize, List<string> classLabels, YoloExportFormat format = YoloExportFormat.YoloV8)
        {
            if (AnnotationType != AnnotationType.Rectangle || Points.Count != 2)
                return string.Empty;

            int classId = classLabels.IndexOf(Label);
            if (classId < 0) classId = 0; // fallback to 0 if not found

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

            double imgW = imageSize.Width;
            double imgH = imageSize.Height;
            if (imgW == 0 || imgH == 0) return string.Empty;

            double xCenterNorm = xCenter / imgW;
            double yCenterNorm = yCenter / imgH;
            double widthNorm = width / imgW;
            double heightNorm = height / imgH;

            // For now, YOLOv5 and YOLOv8 are the same for bounding boxes.
            // If you want to support segmentation or other differences, add logic here.
            return $"{classId} {xCenterNorm:F6} {yCenterNorm:F6} {widthNorm:F6} {heightNorm:F6}";
        }

    }
}
