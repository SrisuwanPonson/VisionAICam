using OpenCvSharp;
using System;
using System.Windows.Media.Imaging;

namespace ClearEngine.Devices.Camera
{
    public interface ICamera : IDisposable
    {
        /// <summary>
        /// Fired when a new frame is ready (BitmapSource for WPF).
        /// </summary>
        event Action<BitmapSource>? FrameReady;

        /// <summary>
        /// True if the camera backend is opened and streaming.
        /// </summary>
        bool IsOpened { get; }

        /// <summary>
        /// Start camera using a backend-specific cameraId.
        /// Examples:
        ///   "0"            = OpenCV webcam index 0
        ///   "HIK:SN12345"  = Hikvision camera by serial
        /// </summary>
        void Start(int cameraId, CameraOptions? options = null);

        /// <summary>
        /// Stop camera streaming.
        /// </summary>
        void Stop();

        /// <summary>
        /// Get property value (OpenCV only).
        /// Hikvision backend may return null.
        /// </summary>
        double GetProperty(VideoCaptureProperties prop);

        /// <summary>
        /// Set property value (OpenCV only).
        /// Hikvision backend may ignore this.
        /// </summary>
        void SetProperty(VideoCaptureProperties prop, double value);

        /// <summary>
        /// Capture a single frame (BitmapSource).
        /// </summary>
        BitmapSource? CaptureCurrentFrame();
    }
}
