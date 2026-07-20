using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Management;
using VisionAICam;
using System.IO;
using VisionAICam.Core;
using VisionAICam.Services;
using System.Linq;
using ClearEngine.Devices.Camera;
namespace VisionAICam.Pages
{
    public partial class SettingPage : Page
    {
        private AppSettings? _appSettings;

        public SettingPage()
        {
            InitializeComponent();
            DiscoverAndPopulateCameras();
            LoadSettings();
        }

        private void DiscoverAndPopulateCameras()
        {
            DefaultCameraComboBox.Items.Clear();
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
            catch { }

            foreach (var device in devices)
                DefaultCameraComboBox.Items.Add(device);
        }

        private void LoadSettings()
        {
            _appSettings = SettingsManager.Load();
            if (_appSettings == null) return;

            // ⭐ Camera Backend
            foreach (ComboBoxItem item in CameraBackendComboBox.Items)
            {
                if (item.Content.ToString() == _appSettings.CameraBackend.ToString())
                {
                    CameraBackendComboBox.SelectedItem = item;
                    break;
                }
            }

            // ⭐ OpenCV Camera
            if (DefaultCameraComboBox.Items.Count > _appSettings.CameraIndex)
                DefaultCameraComboBox.SelectedIndex = _appSettings.CameraIndex;

            BrightnessSlider.Value = _appSettings.Brightness;
            ContrastSlider.Value = _appSettings.Contrast;
            ExposureSlider.Value = _appSettings.Exposure;

            // ⭐ Hikvision
            HikCameraIndexTextBox.Text = _appSettings.HikCameraIndex.ToString();
            HikExposureTextBox.Text = _appSettings.HikExposureTime.ToString();
            HikGainTextBox.Text = _appSettings.HikGain.ToString();
            HikGammaTextBox.Text = _appSettings.HikGamma.ToString();
            HikBlackLevelTextBox.Text = _appSettings.HikBlackLevel.ToString();

            // ⭐ Model
            DefaultModelPathText.Text = string.IsNullOrEmpty(_appSettings.DefaultModelPath)
                ? "(none)"
                : _appSettings.DefaultModelPath;

            // ⭐ Capture folder
            CaptureFolderPathText.Text = string.IsNullOrEmpty(_appSettings.DefaultImagePath)
                ? "(none)"
                : _appSettings.DefaultImagePath;

            // ⭐ Tolerances
            TxtToleranceR.Text = _appSettings.TolerancePercentR.ToString("F2");
            TxtToleranceG.Text = _appSettings.TolerancePercentG.ToString("F2");
            TxtToleranceB.Text = _appSettings.TolerancePercentB.ToString("F2");
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appSettings == null) return;

            // ⭐ Camera Backend
            var backendItem = CameraBackendComboBox.SelectedItem as ComboBoxItem;
            if (backendItem != null)
                _appSettings.CameraBackend =
                    Enum.Parse<CameraBackend>(backendItem.Content.ToString());

            // ⭐ OpenCV
            _appSettings.CameraIndex = DefaultCameraComboBox.SelectedIndex;
            _appSettings.Brightness = BrightnessSlider.Value;
            _appSettings.Contrast = ContrastSlider.Value;
            _appSettings.Exposure = ExposureSlider.Value;

            // ⭐ Hikvision
            if (int.TryParse(HikCameraIndexTextBox.Text, out int hikIndex))
                _appSettings.HikCameraIndex = hikIndex;

            if (double.TryParse(HikExposureTextBox.Text, out double exp))
                _appSettings.HikExposureTime = exp;

            if (double.TryParse(HikGainTextBox.Text, out double gain))
                _appSettings.HikGain = gain;

            if (double.TryParse(HikGammaTextBox.Text, out double gamma))
                _appSettings.HikGamma = gamma;

            if (double.TryParse(HikBlackLevelTextBox.Text, out double black))
                _appSettings.HikBlackLevel = black;

            // ⭐ Model
            _appSettings.DefaultModelPath = DefaultModelPathText.Text;

            // ⭐ Capture folder
            _appSettings.DefaultImagePath = CaptureFolderPathText.Text;

            // ⭐ Tolerances
            if (double.TryParse(TxtToleranceR.Text, out double r))
                _appSettings.TolerancePercentR = Math.Clamp(r, 0, 100);

            if (double.TryParse(TxtToleranceG.Text, out double g))
                _appSettings.TolerancePercentG = Math.Clamp(g, 0, 100);

            if (double.TryParse(TxtToleranceB.Text, out double b))
                _appSettings.TolerancePercentB = Math.Clamp(b, 0, 100);

            SettingsManager.Save(_appSettings);

            MessageBox.Show("Settings saved.", "Settings",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BrowseModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Model Files|*.onnx;*.pb;*.pt;*.tflite|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
                DefaultModelPathText.Text = dialog.FileName;
        }

        private void BrowseCaptureFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a folder to save captured images or videos.",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                CaptureFolderPathText.Text = dialog.SelectedPath;
        }
    }
}
