using System;
using System.Threading;
using System.Windows.Media.Imaging;
using ClearEngine.Devices.Camera.Hikvision;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace ClearEngine.Devices.Camera
{
    public class HikvisionCamera : ICamera
    {
        private HikCamera? _camera;
        private Thread? _thread;
        private volatile bool _running;

        public bool IsOpened => _camera?.IsOpened ?? false;

        public event Action<BitmapSource>? FrameReady;

        public void Start(int index, CameraOptions? options = null)
        {
            Stop();

            // TODO: Set pixelType to your actual camera format (e.g., BGR8)
            _camera = HikCameraFactory.Open(index, width: 1920, height: 1080, pixelType: 0);

            _running = true;

            _thread = new Thread(() =>
            {
                while (_running && _camera != null)
                {
                    if (_camera.TryGetFrame(out var frame))
                    {
                        var bmp = ConvertToBitmapSource(frame);
                        bmp.Freeze();
                        FrameReady?.Invoke(bmp);
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            })
            {
                IsBackground = true
            };

            _thread.Start();
        }

        public void Stop()
        {
            _running = false;

            try { _thread?.Join(); } catch { }

            _camera?.Dispose();
            _camera = null;

            _thread = null;
        }

        public void Dispose() => Stop();

        public double GetProperty(VideoCaptureProperties prop)
        {
            throw new NotSupportedException("Hikvision properties are not mapped yet.");
        }

        public void SetProperty(VideoCaptureProperties prop, double value)
        {
            throw new NotSupportedException("Hikvision properties are not mapped yet.");
        }

        public Mat? CaptureCurrentFrame()
        {
            if (!IsOpened || _camera == null)
                return null;

            if (!_camera.TryGetFrame(out var frame))
                return null;

            unsafe
            {
                fixed (byte* p = frame.Buffer)
                {
                    using var mat = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC3, (IntPtr)p);
                    return mat.Clone(); // clone so we don't depend on pooled buffer
                }
            }
        }

        private BitmapSource ConvertToBitmapSource(HikFrame frame)
        {
            unsafe
            {
                fixed (byte* p = frame.Buffer)
                {
                    using var mat = Mat.FromPixelData(frame.Height, frame.Width, MatType.CV_8UC3, (IntPtr)p);
                    return mat.ToBitmapSource();
                }
            }
        }
    }
}