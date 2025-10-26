using System;
using System.Threading;
using System.Windows.Media.Imaging;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;

namespace ClearEngine.Devices.Camera
{
    public class OpenCvCamera : ICamera
    {
        private VideoCapture? _capture;
        private Thread? _thread;
        private volatile bool _running;

        public event Action<BitmapSource>? FrameReady;
        public bool IsOpened => _capture != null && _capture.IsOpened();

        public void Start(int cameraIndex, CameraOptions? options = null)
        {
            Stop();

            _capture = new VideoCapture(cameraIndex, VideoCaptureAPIs.DSHOW);
            if (!IsOpened) return;

            if (options != null)
            {
                _capture.Set(VideoCaptureProperties.Brightness, options.Brightness);
                _capture.Set(VideoCaptureProperties.Contrast, options.Contrast);
                _capture.Set(VideoCaptureProperties.Exposure, options.Exposure);
            }

            _running = true;
            _thread = new Thread(() =>
            {
                using var mat = new Mat();
                while (_running && _capture != null && _capture.IsOpened())
                {
                    _capture.Read(mat);
                    if (!mat.Empty())
                    {
                        var bitmap = mat.ToBitmapSource();
                        bitmap.Freeze();
                        FrameReady?.Invoke(bitmap);
                    }
                    Thread.Sleep(30);
                }
            })
            { IsBackground = true };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _thread?.Join(); } catch { }
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            _thread = null;
        }

        public double GetProperty(VideoCaptureProperties prop) => _capture?.Get(prop) ?? 0.0;
        public void SetProperty(VideoCaptureProperties prop, double value) => _capture?.Set(prop, value);

        public Mat? CaptureCurrentFrame()
        {
            if (!IsOpened) return null;
            var mat = new Mat();
            _capture!.Read(mat);
            if (mat.Empty()) { mat.Dispose(); return null; }
            return mat;
        }

        public void Dispose() => Stop();
    }
}
