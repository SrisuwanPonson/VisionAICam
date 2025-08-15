using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Collections.Generic;
using OpenCvSharp;
using OpenCvSharp.WpfExtensions;
using System.Management;
using VisionAICam; // For AppSettings and SettingsManager

namespace VisionAICam.Pages
{
    public partial class CameraPage : Page
    {
        private VideoCapture? _capture;
        private Thread? _cameraThread;
        private bool _isRunning;
        private AppSettings? _appSettings;

        public CameraPage()
        {
            InitializeComponent();
            _appSettings = SettingsManager.Load();
            DiscoverAndPopulateCameras();
            this.Unloaded += CameraPage_Unloaded;
            this.IsVisibleChanged += CameraPage_IsVisibleChanged; // Handle visibility changes

            // Set sliders from settings (guard against null)
            if (_appSettings != null)
            {
                BrightnessSlider.Value = _appSettings.Brightness;
                ContrastSlider.Value = _appSettings.Contrast;
                ExposureSlider.Value = _appSettings.Exposure;
            }
        }

        private void CameraPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!this.IsVisible)
            {
                StopCamera();
            }
        }

        private void CameraPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCamera();
        }

        private void StopCamera()
        {
            _isRunning = false;
            _cameraThread?.Join();
            _capture?.Release();
            _capture?.Dispose();
            _capture = null;
            _cameraThread = null;
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
                // Restore last used camera index if available
                if (_appSettings != null && _appSettings.CameraIndex >= 0 && _appSettings.CameraIndex < CameraComboBox.Items.Count)
                    CameraComboBox.SelectedIndex = _appSettings.CameraIndex;
                else
                    CameraComboBox.SelectedIndex = 0;
            }
        }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_appSettings != null)
            {
                _appSettings.CameraIndex = CameraComboBox.SelectedIndex;
                SettingsManager.Save(_appSettings);
            }
        }

        private void SelectCameraButton_Click_1(object sender, RoutedEventArgs e)
        {
            StopCamera();

            // Always reload the latest settings
            _appSettings = SettingsManager.Load();

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

            // Apply settings to camera
            if (_appSettings != null)
            {
                _capture.Set(VideoCaptureProperties.Brightness, _appSettings.Brightness);
                _capture.Set(VideoCaptureProperties.Contrast, _appSettings.Contrast);
                _capture.Set(VideoCaptureProperties.Exposure, _appSettings.Exposure);
            }

            // Initialize sliders with current camera values (in case camera overrides)
            Dispatcher.Invoke(() =>
            {
                BrightnessSlider.Value = _capture.Get(VideoCaptureProperties.Brightness);
                ContrastSlider.Value = _capture.Get(VideoCaptureProperties.Contrast);
                ExposureSlider.Value = _capture.Get(VideoCaptureProperties.Exposure);
            });

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

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_capture != null && _capture.IsOpened())
            {
                _capture.Set(VideoCaptureProperties.Brightness, e.NewValue);
            }
            if (_appSettings != null)
            {
                _appSettings.Brightness = e.NewValue;
                SettingsManager.Save(_appSettings);
            }
        }

        private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_capture != null && _capture.IsOpened())
            {
                _capture.Set(VideoCaptureProperties.Contrast, e.NewValue);
            }
            if (_appSettings != null)
            {
                _appSettings.Contrast = e.NewValue;
                SettingsManager.Save(_appSettings);
            }
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_capture != null && _capture.IsOpened())
            {
                _capture.Set(VideoCaptureProperties.Exposure, e.NewValue);
            }
            if (_appSettings != null)
            {
                _appSettings.Exposure = e.NewValue;
                SettingsManager.Save(_appSettings);
            }
        }
    }
}
