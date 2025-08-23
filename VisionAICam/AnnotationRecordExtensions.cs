using System;
using System.Collections.Generic;
using System.Windows;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using VisionAICam.Pages;

namespace VisionAICam
{
    public static class AnnotationRecordExtensions
    {
        public static string ToYoloFormat(this AnnotationRecord record, Size imageSize, List<string> classLabels)
        {
            if (record.AnnotationType != AnnotationType.Rectangle || record.Points.Count != 2)
                return string.Empty;

            var xMin = Math.Min(record.Points[0].X, record.Points[1].X);
            var yMin = Math.Min(record.Points[0].Y, record.Points[1].Y);
            var xMax = Math.Max(record.Points[0].X, record.Points[1].X);
            var yMax = Math.Max(record.Points[0].Y, record.Points[1].Y);

            var xCenter = ((xMin + xMax) / 2.0) / imageSize.Width;
            var yCenter = ((yMin + yMax) / 2.0) / imageSize.Height;
            var width = (xMax - xMin) / imageSize.Width;
            var height = (yMax - yMin) / imageSize.Height;

            int classId = classLabels.IndexOf(record.Label);
            if (classId < 0) return string.Empty;

            return $"{classId} {xCenter:F6} {yCenter:F6} {width:F6} {height:F6}";
        }
    }
}
