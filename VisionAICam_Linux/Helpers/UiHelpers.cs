using System;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace VisionAICam_Linux.Helpers
{
    public static class UiHelpers
    {
        /// <summary>
        /// Safely set an Image.Source from any thread.
        /// Assigns the image on the Avalonia UI thread with Render priority.
        /// </summary>
        public static void SetImageSourceSafe(Image image, IImage bitmap)
        {
            if (image == null || bitmap == null) return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                ApplyImage(image, bitmap);
                return;
            }

            // Post to UI thread with Render priority so visuals update promptly.
            Dispatcher.UIThread.Post(() => ApplyImage(image, bitmap), DispatcherPriority.Render);
        }

        private static void ApplyImage(Image image, IImage bitmap)
        {
            try
            {
                image.Source = bitmap;
            }
            catch
            {
                // Best-effort: swallow exceptions to avoid throwing from background cleanup/threads.
            }
        }
    }
}