using System;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions; // NuGet/OpenCvSharp extension

public class OpenCvCamera : ICamera
{
    public event Action<BitmapSource>? FrameReady; // library-level event (no UI dependency)

    // Called on the capture thread when a new Mat is available
    private void RaiseFrame(Mat mat)
    {
        try
        {
            if (mat == null || mat.Empty()) return;

            // Convert Mat to BitmapSource for WPF consumers
            var bmp = mat.ToBitmapSource();
            bmp.Freeze(); // freeze to allow cross-thread use
            FrameReady?.Invoke(bmp);
        }
        catch
        {
            // swallow or log internal camera errors
        }
    }

    // ... other ICamera members ...
}