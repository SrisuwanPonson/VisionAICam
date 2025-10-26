using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;

namespace ClearEngine.DataSet
{
    public struct MinAreaRect
    {
        public PointF Center;
        public SizeF Size;
        public float Angle; // degrees
    }

    public static class GeometryUtils
    {
        // PCA-based approximate minimum area rectangle for polygon points
        public static MinAreaRect GetMinAreaRect(IList<PointF> points)
        {
            if (points == null || points.Count < 3)
                throw new ArgumentException("At least 3 points are required.");

            double cx = points.Average(p => p.X);
            double cy = points.Average(p => p.Y);

            double sumXX = 0, sumXY = 0, sumYY = 0;
            foreach (var p in points)
            {
                double dx = p.X - cx;
                double dy = p.Y - cy;
                sumXX += dx * dx;
                sumXY += dx * dy;
                sumYY += dy * dy;
            }

            double covXX = sumXX / points.Count;
            double covXY = sumXY / points.Count;
            double covYY = sumYY / points.Count;

            double theta = 0.5 * Math.Atan2(2 * covXY, covXX - covYY);
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            var rotated = points.Select(p =>
            {
                double dx = p.X - cx;
                double dy = p.Y - cy;
                return new PointF(
                    (float)(dx * cosT + dy * sinT),
                    (float)(-dx * sinT + dy * cosT)
                );
            }).ToList();

            float minX = rotated.Min(p => p.X);
            float maxX = rotated.Max(p => p.X);
            float minY = rotated.Min(p => p.Y);
            float maxY = rotated.Max(p => p.Y);

            return new MinAreaRect
            {
                Center = new PointF((float)cx, (float)cy),
                Size = new SizeF(maxX - minX, maxY - minY),
                Angle = (float)(theta * 180.0 / Math.PI)
            };
        }

        // Produce 8 values x1,y1,..,x4,y4 from center,w,h,angle
        public static List<double> GetRotatedBoxAs8Values(double cx, double cy, double w, double h, double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            double cosA = Math.Cos(angle);
            double sinA = Math.Sin(angle);
            double w2 = w / 2.0;
            double h2 = h / 2.0;

            var corners = new List<(double X, double Y)>
            {
                (cx - w2 * cosA + h2 * sinA, cy - w2 * sinA - h2 * cosA), // top-left
                (cx + w2 * cosA + h2 * sinA, cy + w2 * sinA - h2 * cosA), // top-right
                (cx + w2 * cosA - h2 * sinA, cy + w2 * sinA + h2 * cosA), // bottom-right
                (cx - w2 * cosA - h2 * sinA, cy - w2 * sinA + h2 * cosA)  // bottom-left
            };

            var list = new List<double>();
            foreach (var c in corners)
            {
                list.Add(c.X);
                list.Add(c.Y);
            }
            return list;
        }
    }
}