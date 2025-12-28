using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Collections.Generic;
using OpenCvSharp;
using System.Management;
using VisionAICam; // For AppSettings and SettingsManager
using ClearEngine.Devices.Camera; // class library

namespace VisionAICam.Pages
{
    public partial class CameraPage : Page
    {
        private ICamera? _camera;
        private AppSettings? _appSettings;

        // Prevent re-running initialClass more than once per application run
        private bool _initialClassesLoaded = false;

        // Suppress saving while programmatically setting slider values
        private bool _suspendSliderSave = false;

        public CameraPage()
        {
            InitializeComponent();

            _appSettings = SettingsManager.Load();

            // Discover cameras once on construction
            DiscoverAndPopulateCameras();

            // Initialize class/category lists exactly once per app run
            initialClass();

            Unloaded += CameraPage_Unloaded;
            IsVisibleChanged += CameraPage_IsVisibleChanged;
            Loaded += CameraPage_Loaded;

            if (_appSettings != null)
            {
                // Prevent slider change handlers from persisting these programmatic sets
                _suspendSliderSave = true;
                BrightnessSlider.Value = _appSettings.Brightness;
                ContrastSlider.Value = _appSettings.Contrast;
                ExposureSlider.Value = _appSettings.Exposure;
                _suspendSliderSave = false;
            }
        }

        private void initialClass()
        {
            if (_initialClassesLoaded)
                return;

            string classFile = "class_list.txt";
            string categoryFile = "category_list.txt";

            if (System.IO.File.Exists(classFile))
            {
                var classes = System.IO.File.ReadAllLines(classFile);
                ClassComboBox.Items.Clear();
                ClassComboBox.ItemsSource = classes;
            }
            else
            {
                System.IO.File.WriteAllLines(classFile, new List<string> { "WallPlug", "Screw", "Anchor", "Bracket", "Clip" });
                // After creating default file, load them into UI
                var classes = System.IO.File.ReadAllLines(classFile);
                ClassComboBox.Items.Clear();
                ClassComboBox.ItemsSource = classes;
            }

            if (System.IO.File.Exists(categoryFile))
            {
                var categories = System.IO.File.ReadAllLines(categoryFile);
                CategoryComboBox.Items.Clear();
                CategoryComboBox.ItemsSource = categories;
            }
            else
            {
                System.IO.File.WriteAllLines(categoryFile, new List<string>
               {
                   "Fastener",
                   "Anchor",
                   "Fixture",
                   "Electrical",
                   "Tool"
               });
                var categories = System.IO.File.ReadAllLines(categoryFile);
                CategoryComboBox.Items.Clear();
                CategoryComboBox.ItemsSource = categories;
            }

            _initialClassesLoaded = true;
        }

        private void CameraPage_Loaded(object sender, RoutedEventArgs e) { }

        private void CameraPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (!IsVisible)
                StopCamera();
            // do not re-run initialClass here - it is run once in ctor
        }

        private void CameraPage_Unloaded(object sender, RoutedEventArgs e) => StopCamera();

        private void StopCamera()
        {
            if (_camera != null)
            {
                _camera.FrameReady -= OnFrameReady;
                _camera.Stop();
                _camera.Dispose();
                _camera = null;
            }
        }

        private void OnFrameReady(BitmapSource bitmap)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;
                CameraImage.Source = bitmap;
            });
        }

        private void DiscoverAndPopulateCameras()
        {
            CameraComboBox.Items.Clear();
            var devices = new List<string>();
            try
            {
                // must use this WMI query
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
                CameraComboBox.Items.Add(new ComboBoxItem { Content = device });

            if (CameraComboBox.Items.Count == 0)
            {
                MessageBox.Show("No imaging devices or cameras detected.");
                CameraComboBox.SelectedIndex = -1;
            }
            else
            {
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
            _appSettings = SettingsManager.Load();

            int camIndex = CameraComboBox.SelectedIndex;
            if (camIndex < 0)
            {
                MessageBox.Show("Please select a camera.");
                return;
            }

            _camera = CameraFactory.Create(CameraBackend.OpenCv);
            _camera.FrameReady += OnFrameReady;

            var options = _appSettings != null
                ? new CameraOptions { Brightness = _appSettings.Brightness, Contrast = _appSettings.Contrast, Exposure = _appSettings.Exposure }
                : null;

            _camera.Start(camIndex, options);

            if (!_camera.IsOpened)
            {
                MessageBox.Show("Could not open selected camera.");
                StopCamera();
                return;
            }

            // When updating sliders from camera properties, avoid persisting those updates as user actions
            _suspendSliderSave = true;
            Dispatcher.Invoke(() =>
            {
                BrightnessSlider.Value = _camera.GetProperty(VideoCaptureProperties.Brightness);
                ContrastSlider.Value = _camera.GetProperty(VideoCaptureProperties.Contrast);
                ExposureSlider.Value = _camera.GetProperty(VideoCaptureProperties.Exposure);
            });
            _suspendSliderSave = false;
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e) => StopCamera();

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            var mat = _camera?.CaptureCurrentFrame();
            if (mat != null)
            {
                try
                {
                    string basePath = _appSettings?.DefaultImagePath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                    string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                    string savePath = System.IO.Path.Combine(basePath, folderName);

                    if (!System.IO.Directory.Exists(savePath))
                        System.IO.Directory.CreateDirectory(savePath);

                    string ClassName = ClassComboBox.Text;
                    string Category = CategoryComboBox.Text;
                    string fileName = $"{ClassName}_{Category}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                    string filePath = System.IO.Path.Combine(savePath, fileName);

                    mat.SaveImage(filePath);
                    MessageBox.Show($"Snapshot saved to {filePath}.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Failed to save snapshot: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
                finally
                {
                    mat.Dispose();
                }
            }
            else
            {
                MessageBox.Show("Camera is not running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendSliderSave)
                return;

            if (_camera != null && _camera.IsOpened)
                _camera.SetProperty(VideoCaptureProperties.Brightness, e.NewValue);

            if (_appSettings != null)
            {
                _appSettings.Brightness = e.NewValue;
                SettingsManager.Save(_appSettings);
            }
        }

        private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendSliderSave)
                return;

            if (_camera != null && _camera.IsOpened)
                _camera.SetProperty(VideoCaptureProperties.Contrast, e.NewValue);

            if (_appSettings != null)
            {
                _appSettings.Contrast = e.NewValue;
                SettingsManager.Save(_appSettings);
            }
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendSliderSave)
                return;

            if (_camera != null && _camera.IsOpened)
                _camera.SetProperty(VideoCaptureProperties.Exposure, e.NewValue);

            if (_appSettings != null)
            {
                _appSettings.Exposure = e.NewValue;
                SettingsManager.Save(_appSettings);
            }
        }
    }
}
