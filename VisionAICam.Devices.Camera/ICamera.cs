using System;
using System.Windows.Media.Imaging;
using OpenCvSharp;

namespace ClearEngine.Devices.Camera
{
    public interface ICamera : IDisposable
    {
        event Action<BitmapSource>? FrameReady;
        bool IsOpened { get; }
        void Start(int cameraIndex, CameraOptions? options = null);
        void Stop();
        double GetProperty(VideoCaptureProperties prop);
        void SetProperty(VideoCaptureProperties prop, double value);
        Mat? CaptureCurrentFrame();
    }
}