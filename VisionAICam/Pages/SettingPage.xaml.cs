using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Management;
using VisionAICam; // For AppSettings and SettingsManager

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
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI device discovery error: {ex.Message}");
            }

            foreach (var device in devices)
            {
                DefaultCameraComboBox.Items.Add(device);
            }
        }

        private void LoadSettings()
        {
            _appSettings = SettingsManager.Load();

            // Set camera selection by index
            if (_appSettings != null && DefaultCameraComboBox.Items.Count > _appSettings.CameraIndex)
                DefaultCameraComboBox.SelectedIndex = _appSettings.CameraIndex;
            else if (DefaultCameraComboBox.Items.Count > 0)
                DefaultCameraComboBox.SelectedIndex = 0;

            BrightnessSlider.Value = _appSettings?.Brightness ?? 128;
            ContrastSlider.Value = _appSettings?.Contrast ?? 128;
            ExposureSlider.Value = _appSettings?.Exposure ?? -6;
            DefaultModelPathText.Text = string.IsNullOrEmpty(_appSettings?.DefaultModelPath) ? "(none)" : _appSettings.DefaultModelPath;

            // Load theme
            foreach (ComboBoxItem item in ThemeComboBox.Items)
            {
                if ((item.Content?.ToString() ?? "") == (_appSettings?.Theme ?? "Light"))
                {
                    ThemeComboBox.SelectedItem = item;
                    break;
                }
            }
        }

        private void SaveButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appSettings == null) return;

            _appSettings.CameraIndex = DefaultCameraComboBox.SelectedIndex;
            _appSettings.Brightness = BrightnessSlider.Value;
            _appSettings.Contrast = ContrastSlider.Value;
            _appSettings.Exposure = ExposureSlider.Value;
            _appSettings.DefaultModelPath = DefaultModelPathText.Text;
            _appSettings.Theme = (ThemeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Light";

            SettingsManager.Save(_appSettings);

            MessageBox.Show("Settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BrowseModelButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "Model Files|*.onnx;*.pb;*.pt;*.tflite|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                DefaultModelPathText.Text = dialog.FileName;
            }
        }
    }
}
