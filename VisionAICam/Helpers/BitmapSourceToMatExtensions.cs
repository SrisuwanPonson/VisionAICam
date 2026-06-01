using OpenCvSharp;
using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;

namespace VisionAICam.Helpers
{
    public static class BitmapSourceToMatExtensions
    {
        public static Mat ToMat(this BitmapSource bitmap)
        {
            if (bitmap == null)
                return null;

            int bytesPerPixel = bitmap.Format.BitsPerPixel / 8;
            int stride = bitmap.PixelWidth * bytesPerPixel;

            byte[] pixels = new byte[bitmap.PixelHeight * stride];
            bitmap.CopyPixels(pixels, stride, 0);

            // Create Mat
            Mat mat = new Mat(bitmap.PixelHeight, bitmap.PixelWidth, MatType.CV_8UC3);

            // Copy byte[] → Mat.Data
            Marshal.Copy(pixels, 0, mat.Data, pixels.Length);

            return mat;
        }
    }
}
