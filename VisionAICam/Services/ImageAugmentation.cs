
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VisionAICam.Services
{
    public static class ImageAugmentation
    {
        // Render transformed bitmap and return frozen BitmapSource.
        public static BitmapSource TransformBitmap(BitmapSource src, Matrix m)
        {
            if (src == null) throw new ArgumentNullException(nameof(src));

            double w = src.PixelWidth;
            double h = src.PixelHeight;

            var corners = new[]
            {
                new Point(0,0),
                new Point(w,0),
                new Point(0,h),
                new Point(w,h)
            }.Select(p => m.Transform(p)).ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);
            double maxX = corners.Max(p => p.X);
            double maxY = corners.Max(p => p.Y);

            int targetW = Math.Max(1, (int)Math.Ceiling(maxX - minX));
            int targetH = Math.Max(1, (int)Math.Ceiling(maxY - minY));

            // offset so image fits into positive coordinates
            var offset = new Matrix(1, 0, 0, 1, -minX, -minY);
            var finalMatrix = Matrix.Multiply(m, offset);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                dc.PushTransform(new MatrixTransform(finalMatrix));
                dc.DrawImage(src, new Rect(0, 0, src.PixelWidth, src.PixelHeight));
                dc.Pop();
            }

            var rtb = new RenderTargetBitmap(targetW, targetH, src.DpiX, src.DpiY, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }

        // Transform points using same finalMatrix used for image rendering.
        public static List<Point> TransformPoints(IEnumerable<Point> points, Matrix m, double srcWidth, double srcHeight)
        {
            if (points == null) return new List<Point>();

            var corners = new[]
            {
                new Point(0,0),
                new Point(srcWidth,0),
                new Point(0,srcHeight),
                new Point(srcWidth,srcHeight)
            }.Select(p => m.Transform(p)).ToList();

            double minX = corners.Min(p => p.X);
            double minY = corners.Min(p => p.Y);

            var offset = new Matrix(1, 0, 0, 1, -minX, -minY);
            var final = Matrix.Multiply(m, offset);

            return points.Select(p => final.Transform(p)).ToList();
        }

        public static List<double> TransformRawValuesOBB(IEnumerable<double> raw8, Matrix m, double srcWidth, double srcHeight)
        {
            if (raw8 == null) return null;
            var arr = raw8.ToArray();
            if (arr.Length != 8) return arr.ToList();

            var pts = new List<Point>();
            for (int i = 0; i < 8; i += 2)
                pts.Add(new Point(arr[i], arr[i + 1]));

            var t = TransformPoints(pts, m, srcWidth, srcHeight);
            var outList = new List<double>();
            foreach (var p in t)
            {
                outList.Add(p.X);
                outList.Add(p.Y);
            }
            return outList;
        }

        // Matrices
        public static Matrix FlipHorizontalMatrix(double width) => new Matrix(-1, 0, 0, 1, width, 0);
        public static Matrix FlipVerticalMatrix(double height) => new Matrix(1, 0, 0, -1, 0, height);
        public static Matrix Rotate90CWMatrix(double width, double height) => new Matrix(0, -1, 1, 0, height, 0);
        public static Matrix Rotate180Matrix(double width, double height) => new Matrix(-1, 0, 0, -1, width, height);
        public static Matrix Rotate270CWMatrix(double width, double height) => new Matrix(0, 1, -1, 0, 0, width);

        public static Matrix ShearXMatrix(double width, double height, double k)
        {
            double cx = width * 0.5;
            double cy = height * 0.5;
            var T1 = new Matrix(1, 0, 0, 1, -cx, -cy);
            var shear = new Matrix(1, 0, k, 1, 0, 0);
            var T2 = new Matrix(1, 0, 0, 1, cx, cy);
            return Matrix.Multiply(T2, Matrix.Multiply(shear, T1));
        }

        public static Matrix ShearYMatrix(double width, double height, double k)
        {
            double cx = width * 0.5;
            double cy = height * 0.5;
            var T1 = new Matrix(1, 0, 0, 1, -cx, -cy);
            var shear = new Matrix(1, k, 0, 1, 0, 0);
            var T2 = new Matrix(1, 0, 0, 1, cx, cy);
            return Matrix.Multiply(T2, Matrix.Multiply(shear, T1));
        }

        public static Matrix TranslateMatrix(double tx, double ty) => new Matrix(1, 0, 0, 1, tx, ty);

        // Mild perspective-like affine approximation
        public static Matrix PerspectiveMatrix(double width, double height, double strength = 0.05)
        {
            double cx = width * 0.5;
            double cy = height * 0.5;
            double scaleY = 1.0 - Math.Abs(strength) * 0.5;
            double shear = strength;
            var T1 = new Matrix(1, 0, 0, 1, -cx, -cy);
            var scale = new Matrix(1, 0, 0, scaleY, 0, 0);
            var shearMat = new Matrix(1, 0, shear, 1, 0, 0);
            var T2 = new Matrix(1, 0, 0, 1, cx, cy);
            return Matrix.Multiply(T2, Matrix.Multiply(shearMat, Matrix.Multiply(scale, T1)));
        }

        public static Matrix ScaleMatrix(double scale) => new Matrix(scale, 0, 0, scale, 0, 0);
    }
}