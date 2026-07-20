using OpenCvSharp;
using System;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Media;
namespace VisionAICam.Helpers
{
    public static class BitmapSourceToMatExtensions
    {
        public static Mat ToMat(this BitmapSource bitmap)
        {
            if (bitmap == null)
                return null;

            // ⭐ Force conversion to BGR24 (3 channels)
            var converted = new FormatConvertedBitmap(bitmap, PixelFormats.Bgr24, null, 0);
            converted.Freeze();

            int stride = converted.PixelWidth * 3;
            byte[] pixels = new byte[converted.PixelHeight * stride];
            converted.CopyPixels(pixels, stride, 0);

            Mat mat = new Mat(converted.PixelHeight, converted.PixelWidth, MatType.CV_8UC3);
            Marshal.Copy(pixels, 0, mat.Data, pixels.Length);

            return mat;
        }
    }

}
