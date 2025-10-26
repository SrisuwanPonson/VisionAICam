using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ClearEngine.DataSet
{
    // UI-agnostic annotation record intended for the shared dataset library.
    public class AnnotationRecord
    {
        public int Id { get; set; }
        public int ClassId { get; set; } = -1;
        public string ImageName { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public AnnotationType AnnotationType { get; set; }
        public List<PointF> Points { get; set; } = new();
        public List<double>? RawValues { get; set; }

        // Coordinates: prefer RawValues if present, otherwise flattened Points.
        public string Coordinates =>
            RawValues != null && RawValues.Count > 0
                ? string.Join(',', RawValues.Select(v => v.ToString("F2")))
                : string.Join(',', Points.SelectMany(p => new[] { p.X.ToString("F2"), p.Y.ToString("F2") }));

        // Resolve ClassId from provided class labels (case-insensitive). Sets -1 if not found.
        public void ResolveClassId(List<string>? classLabels)
        {
            if (classLabels == null) { ClassId = -1; return; }
            ClassId = classLabels.FindIndex(s => string.Equals(s, Label, StringComparison.OrdinalIgnoreCase));
        }

        // Basic YOLO rectangle output. Returns empty string for unsupported types.
        public string ToYoloFormat(Size imageSize, List<string>? classLabels, YoloExportFormat format = YoloExportFormat.YoloV8)
        {
            ResolveClassId(classLabels ?? new List<string>());
            if (ClassId < 0) return string.Empty;

            if (AnnotationType == AnnotationType.Rectangle && Points.Count >= 2)
            {
                var p0 = Points[0];
                var p1 = Points[1];
                double x1 = Math.Min(p0.X, p1.X);
                double y1 = Math.Min(p0.Y, p1.Y);
                double x2 = Math.Max(p0.X, p1.X);
                double y2 = Math.Max(p0.Y, p1.Y);

                double cx = (x1 + x2) / 2.0;
                double cy = (y1 + y2) / 2.0;
                double w = x2 - x1;
                double h = y2 - y1;

                if (imageSize.Width <= 0 || imageSize.Height <= 0) return string.Empty;

                double nx = cx / imageSize.Width;
                double ny = cy / imageSize.Height;
                double nw = w / imageSize.Width;
                double nh = h / imageSize.Height;

                return $"{ClassId} {nx:F6} {ny:F6} {nw:F6} {nh:F6}";
            }

            // Simple fallback for rotated box stored in RawValues[8] — convert to axis-aligned center box.
            if (AnnotationType == AnnotationType.RotatedBox && RawValues != null && RawValues.Count == 8)
            {
                var xs = RawValues.Where((v, i) => i % 2 == 0).ToArray();
                var ys = RawValues.Where((v, i) => i % 2 == 1).ToArray();
                double minX = xs.Min();
                double maxX = xs.Max();
                double minY = ys.Min();
                double maxY = ys.Max();

                double cx = (minX + maxX) / 2.0;
                double cy = (minY + maxY) / 2.0;
                double w = maxX - minX;
                double h = maxY - minY;

                if (imageSize.Width <= 0 || imageSize.Height <= 0) return string.Empty;

                double nx = cx / imageSize.Width;
                double ny = cy / imageSize.Height;
                double nw = w / imageSize.Width;
                double nh = h / imageSize.Height;

                return $"{ClassId} {nx:F6} {ny:F6} {nw:F6} {nh:F6}";
            }

            // Polygons / segmentation need specialized export (not handled here).
            return string.Empty;
        }
    }
}