using System;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using System.Collections.Generic;
using System.Management;
using VisionAICam; // For AppSettings and SettingsManager
using System.IO;
using VisionAICam.Core;
using VisionAICam.Services;
using System.Linq;

namespace VisionAICam.Pages
{
    public partial class SettingPage : Page
    {
        private AppSettings? _appSettings;
        private RobotService _robot;

        public SettingPage()
        {
            InitializeComponent();
            DiscoverAndPopulateCameras();
            LoadSettings();

            // ensure Loaded handler runs so UI reflects current RobotService state
            Loaded += SettingPage_Loaded;

            // Wire up existing handlers
            if (BrowsePythonDllButton != null)
                BrowsePythonDllButton.Click += BrowsePythonDllButton_Click;

            // New: script browse button
            if (BrowseScriptButton != null)
                BrowseScriptButton.Click += BrowseScriptButton_Click;

            // Wire robot settings buttons (if present in XAML)
            if (SaveRobotSettingsButton != null)
                SaveRobotSettingsButton.Click += SaveRobotSettings_Click;
            if (ReloadRobotSettingsButton != null)
                ReloadRobotSettingsButton.Click += ReloadRobotSettings_Click;

            // Slider <-> textbox sync for polygon threshold
            if (PolygonAutoCloseThresholdSlider != null)
            {
                PolygonAutoCloseThresholdSlider.ValueChanged += (s, ev) =>
                {
                    if (PolygonAutoCloseThresholdTextBox != null)
                        PolygonAutoCloseThresholdTextBox.Text = PolygonAutoCloseThresholdSlider.Value.ToString("0.##");
                };
            }

            // Load robot settings UI initially
            try
            {
                LoadRobotSettingsTo_ui();
            }
            catch (Exception ex)
            {
                try { MasterController.Instance.Logger.LogError($"LoadRobotSettingsTo_ui failed: {ex}"); } catch { System.Diagnostics.Debug.WriteLine(ex); }
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

            // Populate Robot tab fields
            if (_appSettings != null)
            {
                if (RobotIpTextBox != null)
                    RobotIpTextBox.Text = _appSettings.MasterControllerIp ?? "";
                if (RobotPortTextBox != null)
                    RobotPortTextBox.Text = _appSettings.MasterControllerPort.ToString();
                if (RobotSwapWordsCheckBox != null)
                    RobotSwapWordsCheckBox.IsChecked = _appSettings.SwapFloatWords;
            }

            // Camera selection
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

            // Load Python DLL path
            string pythonPath = !string.IsNullOrWhiteSpace(_appSettings?.PythonDllPath)
                ? _appSettings!.PythonDllPath
                : GetDefaultPythonDllPath();

            if (PythonDllPathText != null)
                PythonDllPathText.Text = pythonPath;

            // Script path UI population (new)
            try
            {
                string defaultScript = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script");
                if (ScriptPathText != null)
                    ScriptPathText.Text = !string.IsNullOrWhiteSpace(_appSettings?.ScriptPath) ? _appSettings.ScriptPath : defaultScript;
            }
            catch { /* tolerate missing control */ }

            // SamplingInterval control
            if (_appSettings != null)
                SamplingIntervalSlider.Value = _appSettings.SamplingInterval;

            // Enable Test button only if Python exists
            if (TestInferenceButton != null)
                TestInferenceButton.IsEnabled = File.Exists(pythonPath);

            // inference options reflect settings
            if (_appSettings != null)
            {
                InferenceEnableCachingCheckBox.IsChecked = _appSettings.InferenceEnableCaching;
                InferencePrewarmCheckBox.IsChecked = _appSettings.InferencePrewarm;
            }
            else
            {
                InferenceEnableCachingCheckBox.IsChecked = true;
                InferencePrewarmCheckBox.IsChecked = true;
            }

            // Polygon threshold reflect settings
            if (_appSettings != null)
            {
                if (PolygonAutoCloseThresholdSlider != null)
                    PolygonAutoCloseThresholdSlider.Value = _appSettings.PolygonAutoCloseThreshold;
                if (PolygonAutoCloseThresholdTextBox != null)
                    PolygonAutoCloseThresholdTextBox.Text = _appSettings.PolygonAutoCloseThreshold.ToString("0.##");
            }

            // Robot UI
            try
            {
                LoadRobotSettingsTo_ui();
            }
            catch (Exception ex)
            {
                try { MasterController.Instance.Logger.LogError($"LoadRobotSettingsTo_ui failed: {ex}"); } catch { System.Diagnostics.Debug.WriteLine(ex); }
            }

            // tolerances population
            try
            {
                if (TxtToleranceR != null) TxtToleranceR.Text = _appSettings.TolerancePercentR.ToString("F2");
                if (TxtToleranceG != null) TxtToleranceG.Text = _appSettings.TolerancePercentG.ToString("F2");
                if (TxtToleranceB != null) TxtToleranceB.Text = _appSettings.TolerancePercentB.ToString("F2");
            }
            catch { /* tolerate UI errors */ }
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

            // persist script path (new)
            if (ScriptPathText != null)
                _appSettings.ScriptPath = ScriptPathText.Text ?? _appSettings.ScriptPath;

            // SamplingInterval safety
            if (int.TryParse(SamplingIntervalTextBox?.Text, out var si))
                _appSettings.SamplingInterval = Math.Max(1, si);

            // Persist Python DLL path
            if (PythonDllPathText != null)
                _appSettings.PythonDllPath = PythonDllPathText.Text ?? "";

            // inference engine options
            _appSettings.InferenceEnableCaching = InferenceEnableCachingCheckBox.IsChecked ?? false;
            _appSettings.InferencePrewarm = InferencePrewarmCheckBox.IsChecked ?? false;

            // polygon threshold
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
            }

            // tolerances
            if (_appSettings != null)
            {
                double parsed;
                if (TxtToleranceR != null && double.TryParse(TxtToleranceR.Text, out parsed))
                    _appSettings.TolerancePercentR = Math.Clamp(parsed, 0.0, 100.0);
                if (TxtToleranceG != null && double.TryParse(TxtToleranceG.Text, out parsed))
                    _appSettings.TolerancePercentG = Math.Clamp(parsed, 0.0, 100.0);
                if (TxtToleranceB != null && double.TryParse(TxtToleranceB.Text, out parsed))
                    _appSettings.TolerancePercentB = Math.Clamp(parsed, 0.0, 100.0);
            }

            SettingsManager.Save(_appSettings);

            // Refresh CameraPage tolerances if available
            try
            {
                var camPage = MasterController.Instance?.CameraPage;
                camPage?.RefreshTolerances();
            }
            catch
            {
                try
                {
                    if (Application.Current?.MainWindow is Window win)
                    {
                        if (win.FindName("MainContent") is System.Windows.Controls.Frame frame)
                        {
                            if (frame.Content is VisionAICam.Pages.CameraPage cp)
                                cp.RefreshTolerances();
                        }
                    }
                }
                catch { /* tolerate lookup failures */ }
            }

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
                    if (TestInferenceButton != null)
                        TestInferenceButton.IsEnabled = File.Exists(dialog.FileName);
                }
            }
        }

        // New: browse for script folder
        private void BrowseScriptButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var dialog = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Select Script folder (training / utility scripts)",
                    ShowNewFolderButton = true
                };

                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                {
                    if (ScriptPathText != null)
                        ScriptPathText.Text = dialog.SelectedPath;

                    if (_appSettings != null)
                        _appSettings.ScriptPath = dialog.SelectedPath;
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"BrowseScriptButton_Click failed: {ex}");
            }
        }

        private void SettingPage_Loaded(object? sender, RoutedEventArgs e)
        {
            var s = SettingsManager.Load();
            RobotIpTextBox.Text = s.MasterControllerIp ?? "";
            RobotPortTextBox.Text = s.MasterControllerPort.ToString();
            RobotSwapWordsCheckBox.IsChecked = s.SwapFloatWords;

            try
            {
                _robot = MasterController.Instance.RobotService;
                DataContext = _robot;
                _robot.PropertyChanged += Robot_PropertyChanged;
                UpdateRobotUi(_robot.IsConnected);
            }
            catch
            {
                UpdateRobotUi(false);
            }

            try { LoadRobotSettingsTo_ui(); } catch { }
        }

        private void Robot_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(RobotService.IsConnected)) return;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    UpdateRobotUi(_robot?.IsConnected == true);
                }
                catch
                {
                    UpdateRobotUi(false);
                }
            }));
        }

        private void UpdateRobotUi(bool connected)
        {
            try
            {
                if (RobotStatusTextBlock != null)
                    RobotStatusTextBlock.Text = connected ? "Connected" : "Not connected";

                if (RobotConnectButton != null)
                    RobotConnectButton.Content = connected ? "Disconnect" : "Connect";
            }
            catch { }
        }

        private async void RobotConnectButton_Click(object sender, RoutedEventArgs e)
        {
            RobotConnectButton.IsEnabled = false;
            try
            {
                var robot = _robot ?? MasterController.Instance.RobotService;
                _robot = robot;

                if (robot.IsConnected)
                {
                    robot.Disconnect();
                    UpdateRobotUi(false);
                    return;
                }

                string host = RobotIpTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(host))
                {
                    MessageBox.Show("Please enter a host.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!int.TryParse(RobotPortTextBox.Text, out int port))
                {
                    MessageBox.Show("Port must be a number.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                robot.SwapFloatWords = RobotSwapWordsCheckBox.IsChecked == true;

                bool ok = false;
                try
                {
                    ok = await robot.ConnectTcpAsync(host, port).ConfigureAwait(false);
                }
                catch
                {
                    ok = false;
                }

                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    UpdateRobotUi(ok);
                    if (ok)
                    {
                        var s = SettingsManager.Load();
                        s.MasterControllerIp = host;
                        s.MasterControllerPort = port;
                        s.SwapFloatWords = robot.SwapFloatWords;
                        SettingsManager.Save(s);
                    }
                    else
                    {
                        MessageBox.Show("Failed to connect to robot.", "Connection", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }));
            }
            finally
            {
                RobotConnectButton.IsEnabled = true;
            }
        }

        // Robot settings UI helpers (unchanged)...
        private void LoadRobotSettingsTo_ui()
        {
            try
            {
                var s = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load();
                if (s == null)
                {
                    if (RobotRegisterAddressTextBox != null) RobotRegisterAddressTextBox.Text = "10";
                    if (ClassIdMapTextBox != null) ClassIdMapTextBox.Text = string.Empty;
                    return;
                }

                if (RobotRegisterAddressTextBox != null)
                    RobotRegisterAddressTextBox.Text = s.RobotRegisterAddress.ToString();

                var map = s.ClassIdMap ?? new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);

                if (map.Count == 0)
                {
                    if (ClassIdMapTextBox != null) ClassIdMapTextBox.Text = string.Empty;
                    return;
                }

                var ordered = map.OrderBy(kv => kv.Value).ToList();

                bool isSequential = true;
                for (int i = 0; i < ordered.Count; i++)
                {
                    if (ordered[i].Value != (ushort)(i + 1))
                    {
                        isSequential = false;
                        break;
                    }
                }

                if (ClassIdMapTextBox != null)
                {
                    if (isSequential)
                        ClassIdMapTextBox.Text = string.Join(",", ordered.Select(kv => kv.Key));
                    else
                        ClassIdMapTextBox.Text = string.Join(Environment.NewLine, ordered.Select(kv => $"{kv.Key}={kv.Value}"));
                }
            }
            catch (Exception ex)
            {
                try { MasterController.Instance.Logger.LogError($"LoadRobotSettingsTo_ui failed: {ex}"); } catch { System.Diagnostics.Debug.WriteLine(ex); }
            }
        }

        private void ReloadRobotSettings_Click(object sender, RoutedEventArgs e)
        {
            LoadRobotSettingsTo_ui();
        }

        private void SaveRobotSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var s = MasterController.Instance.GetService<AppSettings>() ?? SettingsManager.Load() ?? new AppSettings();

                if (ushort.TryParse(RobotRegisterAddressTextBox?.Text?.Trim() ?? "", out ushort reg))
                    s.RobotRegisterAddress = reg;

                var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
                var raw = (ClassIdMapTextBox?.Text ?? "").Trim();

                if (!string.IsNullOrEmpty(raw))
                {
                    if (raw.Contains('=') || raw.Contains(':'))
                    {
                        var lines = raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var rawLine in lines)
                        {
                            var line = rawLine.Trim();
                            if (string.IsNullOrEmpty(line)) continue;
                            char sep = line.Contains('=') ? '=' : (line.Contains(':') ? ':' : '\0');
                            if (sep == '\0') continue;
                            var parts = line.Split(sep);
                            if (parts.Length != 2) continue;
                            var name = parts[0].Trim();
                            if (ushort.TryParse(parts[1].Trim(), out ushort id) && !string.IsNullOrEmpty(name))
                                map[name] = id;
                        }
                    }
                    else
                    {
                        var parts = raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                                       .Select(p => p.Trim())
                                       .Where(p => !string.IsNullOrEmpty(p))
                                       .ToArray();
                        for (int i = 0; i < parts.Length; i++)
                        {
                            var name = parts[i];
                            ushort id = (ushort)(i + 1);
                            map[name] = id;
                        }
                    }
                }

                if (map.Count > 0)
                    s.ClassIdMap = map;

                SettingsManager.Save(s);
                try { MasterController.Instance.RegisterService(s); } catch { }
                MessageBox.Show("Robot settings saved.", "Settings", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save robot settings: {ex.Message}", "Settings", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
