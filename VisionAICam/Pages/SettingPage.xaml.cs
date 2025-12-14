using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Management;
using VisionAICam; // For AppSettings and SettingsManager
using System.IO;

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

            // Wire up handlers for controls added in XAML
            if (BrowsePythonDllButton != null)
                BrowsePythonDllButton.Click += BrowsePythonDllButton_Click;

            // In the constructor (after InitializeComponent) wire slider <-> textbox (if controls exist in XAML):
            if (PolygonAutoCloseThresholdSlider != null)
            {
                // keep UI in sync: slider -> textbox
                PolygonAutoCloseThresholdSlider.ValueChanged += (s, ev) =>
                {
                    if (PolygonAutoCloseThresholdTextBox != null)
                        PolygonAutoCloseThresholdTextBox.Text = PolygonAutoCloseThresholdSlider.Value.ToString("0.##");
                };
            }
        }

        private static string GetDefaultPythonDllPath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script", "NewEnv", "Python313", "python313.dll");
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

            // Set DataContext so bindings (SamplingInterval etc.) work directly against AppSettings
            this.DataContext = _appSettings;

            // Set camera selection by index
            if (_appSettings != null && DefaultCameraComboBox.Items.Count > _appSettings.CameraIndex)
                DefaultCameraComboBox.SelectedIndex = _appSettings.CameraIndex;
            else if (DefaultCameraComboBox.Items.Count > 0)
                DefaultCameraComboBox.SelectedIndex = 0;

            BrightnessSlider.Value = _appSettings?.Brightness ?? 128;
            ContrastSlider.Value = _appSettings?.Contrast ?? 128;
            ExposureSlider.Value = _appSettings?.Exposure ?? -6;
            DefaultModelPathText.Text = string.IsNullOrEmpty(_appSettings?.DefaultModelPath) ? "(none)" : _appSettings.DefaultModelPath;
            CaptureFolderPathText.Text = string.IsNullOrEmpty(_appSettings?.DefaultImagePath) ? "(none)" : _appSettings.DefaultImagePath;
            // Load theme
            foreach (ComboBoxItem item in ThemeComboBox.Items)
            {
                if ((item.Content?.ToString() ?? "") == (_appSettings?.Theme ?? "Light"))
                {
                    ThemeComboBox.SelectedItem = item;
                    break;
                }
            }

            // Load Python DLL path from settings or use same hardcoded default as Production
            string pythonPath = !string.IsNullOrWhiteSpace(_appSettings?.PythonDllPath)
                ? _appSettings!.PythonDllPath
                : GetDefaultPythonDllPath();

            if (PythonDllPathText != null)
                PythonDllPathText.Text = pythonPath;

            // Ensure SamplingInterval control reflects current value (binding already set, but keep defensive)
            if (_appSettings != null)
            {
                // DataContext binding updates slider/textbox automatically; this is a no-op but ensures value exists
                SamplingIntervalSlider.Value = _appSettings.SamplingInterval;
            }

            // Enable Test button only if a valid path exists
            if (TestInferenceButton != null)
                TestInferenceButton.IsEnabled = File.Exists(pythonPath);

            // --- NEW: reflect inference options in UI from AppSettings ---
            if (_appSettings != null)
            {
                InferenceEnableCachingCheckBox.IsChecked = _appSettings.InferenceEnableCaching;
                InferencePrewarmCheckBox.IsChecked = _appSettings.InferencePrewarm;
            }
            else
            {
                // sensible defaults if settings missing
                InferenceEnableCachingCheckBox.IsChecked = true;
                InferencePrewarmCheckBox.IsChecked = true;
            }

            // In LoadSettings(), after loading _appSettings and other controls, set UI from settings:
            if (_appSettings != null)
            {
                // ensure control names match your XAML (PolygonAutoCloseThresholdSlider, PolygonAutoCloseThresholdTextBox)
                if (PolygonAutoCloseThresholdSlider != null)
                    PolygonAutoCloseThresholdSlider.Value = _appSettings.PolygonAutoCloseThreshold;
                if (PolygonAutoCloseThresholdTextBox != null)
                    PolygonAutoCloseThresholdTextBox.Text = _appSettings.PolygonAutoCloseThreshold.ToString("0.##");
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
            _appSettings.DefaultImagePath = CaptureFolderPathText.Text;

            // SamplingInterval is two-way bound to _appSettings.SamplingInterval; ensure numeric safety
            if (int.TryParse(SamplingIntervalTextBox?.Text, out var si))
                _appSettings.SamplingInterval = Math.Max(1, si);

            // Persist Python DLL path from settings page
            if (PythonDllPathText != null)
                _appSettings.PythonDllPath = PythonDllPathText.Text ?? "";

            // --- NEW: persist inference engine options ---
            _appSettings.InferenceEnableCaching = InferenceEnableCachingCheckBox.IsChecked ?? false;
            _appSettings.InferencePrewarm = InferencePrewarmCheckBox.IsChecked ?? false;

            // In SaveButton_Click(), persist the value back into _appSettings before calling SettingsManager.Save(_appSettings);
            if (_appSettings != null)
            {
                double parsed;
                if (PolygonAutoCloseThresholdTextBox != null && double.TryParse(PolygonAutoCloseThresholdTextBox.Text, out parsed))
                {
                    _appSettings.PolygonAutoCloseThreshold = Math.Max(0.0, parsed);
                }
                else if (PolygonAutoCloseThresholdSlider != null)
                {
                    _appSettings.PolygonAutoCloseThreshold = Math.Max(0.0, PolygonAutoCloseThresholdSlider.Value);
                }
                // SettingsManager.Save(_appSettings) already called below in method
            }

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

        private void SelectFromModelManagementButton_Click(object sender, RoutedEventArgs e)
        {
            // For now, use a file dialog. You can later open a custom model management window.
            var dialog = new OpenFileDialog
            {
                Title = "Select Model from Model Management",
                Filter = "Model Files|*.onnx;*.pb;*.pt;*.tflite|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                DefaultModelPathText.Text = dialog.FileName;
            }
        }

        private void BrowseCaptureFolderButton_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select a folder to save captured images or videos.",
                ShowNewFolderButton = true
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                if (CaptureFolderPathText != null)
                {
                    CaptureFolderPathText.Text = dialog.SelectedPath;
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine("DefaultCaptureFolderPathText is not defined or accessible.");
                }
            }
        }

        private void BrowsePythonDllButton_Click(object? sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select Python DLL",
                Filter = "Python DLL|python*.dll;*.dll|All Files|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                if (PythonDllPathText != null)
                {
                    PythonDllPathText.Text = dialog.FileName;
                    // enable test if file exists
                    if (TestInferenceButton != null)
                        TestInferenceButton.IsEnabled = File.Exists(dialog.FileName);
                }
            }
        }

        
    }
}
