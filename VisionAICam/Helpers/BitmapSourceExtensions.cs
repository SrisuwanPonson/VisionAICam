using System;
using System.IO;
using System.Windows.Media.Imaging;

namespace VisionAICam.Helpers
{
    public static class BitmapSourceExtensions
    {
        public static void SaveImage(this BitmapSource image, string filePath)
        {
            if (image == null)
                throw new ArgumentNullException(nameof(image));

            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentNullException(nameof(filePath));

            BitmapEncoder encoder;

            string ext = Path.GetExtension(filePath).ToLower();
            switch (ext)
            {
                case ".jpg":
                case ".jpeg":
                    encoder = new JpegBitmapEncoder();
                    break;

                case ".bmp":
                    encoder = new BmpBitmapEncoder();
                    break;

                case ".png":
                default:
                    encoder = new PngBitmapEncoder();
                    break;
            }

            // Freeze image for thread safety
            if (image.CanFreeze)
                image.Freeze();

            encoder.Frames.Add(BitmapFrame.Create(image));

            // Ensure directory exists
            string? dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            using (var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write))
            {
                encoder.Save(fs);
            }
        }
    }
}
