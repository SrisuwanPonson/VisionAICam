using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using System.Windows.Media;
using System.Windows;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace ClearEngine.Devices.Camera
{
    public class HikCamera : ICamera
    {
        public event Action<BitmapSource>? FrameReady;
        public bool IsOpened { get; private set; }

        private Process? _pythonProcess;
        private Thread? _readerThread;
        private volatile bool _running;

        private readonly string pythonExe =
            @"C:\Program Files\Python313\python.exe";

        private readonly string scriptPath =
            @"C:\ClearEngine\VisionAICam\PythonScripts\hik_stream.py";

        public void Start(int cameraIndex, CameraOptions? options = null)
        {
            Stop();

            if (!File.Exists(scriptPath))
                throw new FileNotFoundException("Python Hikvision stream script not found.", scriptPath);

            var psi = new ProcessStartInfo
            {
                FileName = pythonExe,
                Arguments = $"\"{scriptPath}\" \"{cameraIndex}\" {options?.Brightness ?? 0} {options?.Contrast ?? 0} {options?.Exposure ?? 0}",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = false,
                CreateNoWindow = true
            };

            _pythonProcess = new Process { StartInfo = psi };
            _pythonProcess.Start();

            _running = true;
            _readerThread = new Thread(ReadFramesLoop)
            {
                IsBackground = true
            };
            _readerThread.Start();

            IsOpened = true;
        }

        private void ReadFramesLoop()
        {
            try
            {
                while (_running && _pythonProcess != null && !_pythonProcess.HasExited)
                {
                    string? line = _pythonProcess.StandardOutput.ReadLine();
                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    try
                    {
                        byte[] bytes = Convert.FromBase64String(line);
                        using var ms = new MemoryStream(bytes);

                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.StreamSource = ms;
                        bitmap.EndInit();
                        bitmap.Freeze();

                        FrameReady?.Invoke(bitmap);
                    }
                    catch
                    {
                        // ignore corrupted frame
                    }
                }
            }
            catch { }
        }

        public void Stop()
        {
            _running = false;

            try { _readerThread?.Join(200); } catch { }

            if (_pythonProcess != null && !_pythonProcess.HasExited)
                _pythonProcess.Kill();

            _pythonProcess?.Dispose();
            _pythonProcess = null;
            _readerThread = null;

            IsOpened = false;
        }

        // ⭐ HikCamera ไม่รองรับ VideoCaptureProperties → return default
        public double GetProperty(VideoCaptureProperties prop) => 0;

        public void SetProperty(VideoCaptureProperties prop, double value)
        {
            // Hikvision Python backend ไม่รองรับ property mapping
            // ทำเป็น no-op
        }

        public BitmapSource? CaptureCurrentFrame()
        {
            // Python streaming backend ไม่มี snapshot API
            return null;
        }

        public void Dispose() => Stop();
    }

}
