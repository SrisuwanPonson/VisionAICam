using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Collections.Generic;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Management;

namespace VisionAICam.Pages
{
    public partial class CameraPage : Page
    {
        private VideoCapture? _capture;
        private Thread? _cameraThread;
        private bool _isRunning;

        public CameraPage()
        {
            InitializeComponent();
            DiscoverAndPopulateCameras();
            this.Unloaded += CameraPage_Unloaded;
        }

        private void CameraPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void StopCamera()
        {
            if (_isRunning)
            {
                _isRunning = false;
                _cameraThread?.Join();
                _capture?.Release();
                _capture?.Dispose();
                _capture = null;
                _cameraThread = null;
            }
        }

        private void CameraLoop()
        {
            using var mat = new Mat();
            while (_isRunning && _capture != null && _capture.IsOpened())
            {
                _capture.Read(mat);
                if (!mat.Empty())
                {
                    var bitmapSource = mat.ToBitmapSource();
                    bitmapSource.Freeze();
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (!this.IsLoaded) return;
                        CameraImage.Source = bitmapSource;
                    });
                }
                Thread.Sleep(30); // ~30 FPS
            }
        }


        private void DiscoverAndPopulateCameras()
        {
            CameraComboBox.Items.Clear();
            var devices = new List<string>();
            try
            {
                using (var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE (PNPClass = 'Image' OR PNPClass = 'Camera')"))
                {
                    foreach (ManagementObject device in searcher.Get())
                    {
                        var name = device["Name"]?.ToString();
                        if (!string.IsNullOrEmpty(name))
                            devices.Add(name);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI device discovery error: {ex.Message}");
            }

            foreach (var device in devices)
            {
                CameraComboBox.Items.Add(new ComboBoxItem { Content = device });
            }
            if (CameraComboBox.Items.Count == 0)
            {
                MessageBox.Show("No imaging devices or cameras detected.");
                CameraComboBox.SelectedIndex = -1;
            }
            else
            {
                CameraComboBox.SelectedIndex = 0;
            }
        }

        private void SelectCameraButton_Click_1(object sender, RoutedEventArgs e)
        {
            StopCamera();

            int camIndex = CameraComboBox.SelectedIndex;
            if (camIndex < 0)
            {
                MessageBox.Show("Please select a camera.");
                return;
            }

            _capture = new VideoCapture(camIndex, VideoCaptureAPIs.DSHOW);
            if (!_capture.IsOpened())
            {
                MessageBox.Show("Could not open selected camera.");
                return;
            }

            _isRunning = true;
            _cameraThread = new Thread(CameraLoop);
            _cameraThread.Start();
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            if (_capture != null && _capture.IsOpened())
            {
                using var mat = new OpenCvSharp.Mat();
                _capture.Read(mat);
                if (!mat.Empty())
                {
                    var dialog = new Microsoft.Win32.SaveFileDialog
                    {
                        Filter = "PNG Image|*.png|JPEG Image|*.jpg",
                        FileName = "snapshot.png"
                    };
                    if (dialog.ShowDialog() == true)
                    {
                        mat.SaveImage(dialog.FileName);
                        MessageBox.Show("Snapshot saved.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
                    }
                }
                else
                {
                    MessageBox.Show("Failed to capture snapshot.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show("Camera is not running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
