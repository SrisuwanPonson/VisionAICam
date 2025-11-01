using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VisionAICam.Helpers
{
    public static class UiHelpers
    {
        /// <summary>
        /// Safely set an Image.Source from any thread. Freezes the bitmap for fast rendering
        /// and assigns it on the UI thread with Render priority.
        /// </summary>
        public static void SetImageSourceSafe(Image image, BitmapSource bitmap)
        {
            if (image == null || bitmap == null) return;

            BitmapSource frozen = bitmap;
            if (bitmap.CanFreeze && !bitmap.IsFrozen)
            {
                try
                {
                    bitmap.Freeze();
                    frozen = bitmap;
                }
                catch
                {
                    try
                    {
                        // Clone and freeze as fallback
                        frozen = bitmap.Clone();
                        frozen.Freeze();
                    }
                    catch
                    {
                        // If freezing fails, still attempt to use original bitmap on UI thread
                        frozen = bitmap;
                    }
                }
            }

            var dispatcher = image.Dispatcher ?? Application.Current?.Dispatcher;
            if (dispatcher == null || dispatcher.CheckAccess())
            {
                ApplyImage(image, frozen);
                return;
            }

            dispatcher.BeginInvoke((Action)(() => ApplyImage(image, frozen)), DispatcherPriority.Render);
        }

        private static void ApplyImage(Image image, BitmapSource frozen)
        {
            // Prefer high-quality scaling for smoother visuals when resized
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            image.Source = frozen;
        }
    }
}