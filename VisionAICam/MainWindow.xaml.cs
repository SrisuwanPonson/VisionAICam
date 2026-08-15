using System.Windows;
using System.Windows.Controls;
using VisionAICam.Pages;
using System.ComponentModel;
using Python.Runtime;
using System;
using System.Reflection;
using VisionAICam.Core;
using System.Windows.Navigation;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Linq;

namespace VisionAICam
{
    public partial class MainWindow : Window
    {
        private Process? _hikServerProcess;
        private static readonly string AppVersion = "1.1.0";
        private static readonly string AppRevision = "2026-08-15";
        private int _totalDetectionCount = 0;
        private int _sessionDetectionCount = 0;
        private int _frameCount = 0;

        public MainWindow()
        {
            InitializeComponent();
            // ⭐ เพิ่ม: แสดง Version ที่ UI (ถ้ามี VersionTextBlock ใน XAML)
            try
            {
                if (this.FindName("VersionTextBlock") is TextBlock versionText)
                {
                    versionText.Text = $"v{AppVersion} | Rev: {AppRevision}";
                }
                this.Title = $"VisionAICam v{AppVersion}";
            }
            catch { }

            StartPythonServer();   // ⭐ Start Python server automatically

            MainContent.Navigated -= MainContent_Navigated;
            MainContent.Navigated += MainContent_Navigated;

            this.Loaded += (s, e) => { AdjustNavButtons(); UpdateSidePanelVisibility(); };
            this.SizeChanged += (s, e) => AdjustNavButtons();

            if (IsAnotherInstanceRunning())
            {
                Application.Current.Shutdown();
                return;
            }

            if (MainContent.Content is not Production)
            {
                MainContent.Navigate(MasterController.Instance.Production);
            }

            UpdateSidePanelVisibility();
            UpdateStatistics(); // Initialize statistics display

            this.Closing -= MainWindow_Closing;
            this.Closing += MainWindow_Closing;
        }

        // ⭐ Missing method: Update statistics display
        public void UpdateStatistics()
        {
            try
            {
                if (TotalCountTextBlock != null)
                    TotalCountTextBlock.Text = _totalDetectionCount.ToString();

                if (SessionCountTextBlock != null)
                    SessionCountTextBlock.Text = _sessionDetectionCount.ToString();

                if (FrameCountTextBlock != null)
                    FrameCountTextBlock.Text = _frameCount.ToString();
            }
            catch { }
        }

        // ⭐ Missing event handler: Reset statistics button
        private void ResetStatsButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var result = MessageBox.Show(
                    "Are you sure you want to reset all statistics?\n\nThis will reset:\n• Total count\n• Session count\n• Frame count",
                    "Reset Statistics",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Yes)
                {
                    _totalDetectionCount = 0;
                    _sessionDetectionCount = 0;
                    _frameCount = 0;
                    UpdateStatistics();

                    // Clear the per-frame summary grid if it exists
                    if (PerFrameSummaryGrid != null)
                    {
                        PerFrameSummaryGrid.ItemsSource = null;
                    }

                    MessageBox.Show("Statistics reset successfully.", "Reset Complete", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to reset statistics: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ⭐ Public methods for other pages to update statistics
        public void IncrementTotalCount(int count = 1)
        {
            _totalDetectionCount += count;
            UpdateStatistics();
        }

        public void IncrementSessionCount(int count = 1)
        {
            _sessionDetectionCount += count;
            UpdateStatistics();
        }

        public void IncrementFrameCount(int count = 1)
        {
            _frameCount += count;
            UpdateStatistics();
        }

        public void ResetSessionCount()
        {
            _sessionDetectionCount = 0;
            UpdateStatistics();
        }

        // ⭐ เพิ่ม: อ่าน Version จาก Assembly
        private static string GetAppVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        // ⭐ เพิ่ม: อ่าน Revision
        private static string GetAppRevision()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version != null ? $"{version.Build:D4}" : "0000";
            }
            catch
            {
                return "0000";
            }
        }
        private void StartPythonServer()
        {
            try
            {
                if (_hikServerProcess != null && !_hikServerProcess.HasExited)
                    return;

                var psi = new ProcessStartInfo
                {
                    FileName = @"C:\ClearEngine\VisionAICam\PythonEnv\Python313\python.exe",
                    Arguments = "\"C:\\ClearEngine\\VisionAICam\\PythonScripts\\hik_server.py\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false,
                    CreateNoWindow = true,
                    WorkingDirectory = @"C:\ClearEngine\VisionAICam\PythonScripts"
                };

                _hikServerProcess = Process.Start(psi);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start hik_server.py: " + ex.Message);
            }
        }

        private void StopPythonServer()
        {
            try
            {
                if (_hikServerProcess != null && !_hikServerProcess.HasExited)
                {
                    _hikServerProcess.Kill();
                    _hikServerProcess.Dispose();
                }
            }
            catch { }
        }

        private void MainContent_Navigated(object? sender, NavigationEventArgs e)
        {
            UpdateSidePanelVisibility();
        }

        private void UpdateSidePanelVisibility()
        {
            try
            {
                bool isProduction = MainContent?.Content is Production;

                if (LeftPanel != null)
                    LeftPanel.Visibility = isProduction ? Visibility.Visible : Visibility.Collapsed;

                if (RightPanel != null)
                    RightPanel.Visibility = isProduction ? Visibility.Visible : Visibility.Collapsed;

                if (LeftExpandButton != null)
                    LeftExpandButton.Visibility = (isProduction && (LeftPanel == null || LeftPanel.Visibility == Visibility.Collapsed))
                                                  ? Visibility.Visible : Visibility.Collapsed;

                if (RightExpandButton != null)
                    RightExpandButton.Visibility = (isProduction && (RightPanel == null || RightPanel.Visibility == Visibility.Collapsed))
                                                   ? Visibility.Visible : Visibility.Collapsed;

                if (!isProduction)
                {
                    StartStopButton.IsEnabled = false;
                    PauseButton.IsEnabled = false;
                }
                else
                {
                    if (MainContent.Content is Production prod)
                    {
                        StartStopButton.IsEnabled = true;
                        PauseButton.IsEnabled = prod.IsRunning;
                    }
                    else
                    {
                        StartStopButton.IsEnabled = true;
                        PauseButton.IsEnabled = false;
                    }
                }
            }
            catch { }
        }

        private bool IsAnotherInstanceRunning()
        {
            var currentProcess = Process.GetCurrentProcess();
            var processes = Process.GetProcessesByName(currentProcess.ProcessName);
            return processes.Length > 1;
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainContent.Content is Production production)
            {
                if (!production.IsRunning)
                {
                    production.StartProduction();
                    StartStopButton.Content = "\uE71A";
                    PauseButton.IsEnabled = true;
                }
                else
                {
                    production.StopProduction();
                    StartStopButton.Content = "\uE768";
                    PauseButton.IsEnabled = false;
                    PauseButton.Content = "\uE769";
                }
            }
            else
            {
                MessageBox.Show("Please open the Production page to start/stop production.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainContent.Content is Production production)
            {
                if (production.IsRunning)
                {
                    if (!production.IsPaused)
                    {
                        production.PauseProduction();
                        PauseButton.Content = "\uE768";
                    }
                    else
                    {
                        production.ResumeProduction();
                        PauseButton.Content = "\uE769";
                    }
                }
            }
            else
            {
                MessageBox.Show("Please open the Production page to pause/resume production.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        private void NavigateIfNotDuplicate<T>(Page pageInstance, string pageName) where T : Page
        {
            if (MainContent.Content is T)
            {
                MessageBox.Show($"The {pageName} page is already open.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MainContent.Navigate(pageInstance);
        }

        private void UserButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<UserPage>(MasterController.Instance.UserPage, "User");
        }

        private void DataButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<DataPage>(new DataPage(), "Data");
        }

        private void DiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<DiagnosticsPage>(MasterController.Instance.DiagnosticsPage, "Diagnostics");
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<SettingPage>(MasterController.Instance.SettingPage, "Settings");
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<CameraPage>(MasterController.Instance.CameraPage, "Camera");
        }

        private void ProductionButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainContent.Content is Production)
            {
                MessageBox.Show("The Production page is already open.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            MainContent.Navigate(MasterController.Instance.Production);
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var prod = MasterController.Instance.Production;
                if (prod != null && prod.IsRunning)
                    prod.StopProduction();
            }
            catch { }

            StopPythonServer();   // ⭐ Kill Python server on exit

            Application.Current.Shutdown();
        }

        private void DataSetButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<DataSetPage>(MasterController.Instance.DataSetPage, "Dataset");
        }

        private void ModelButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<ModelPage>(MasterController.Instance.ModelPage, "Model");
        }

        private void LeftCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LeftPanel != null)
                    LeftPanel.Visibility = Visibility.Collapsed;

                if (LeftExpandButton != null)
                    LeftExpandButton.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void LeftExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LeftPanel != null)
                    LeftPanel.Visibility = Visibility.Visible;

                if (LeftExpandButton != null)
                    LeftExpandButton.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void RightCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RightPanel != null)
                    RightPanel.Visibility = Visibility.Collapsed;

                if (RightExpandButton != null)
                    RightExpandButton.Visibility = Visibility.Visible;
            }
            catch { }
        }

        private void RightExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RightPanel != null)
                    RightPanel.Visibility = Visibility.Visible;

                if (RightExpandButton != null)
                    RightExpandButton.Visibility = Visibility.Collapsed;
            }
            catch { }
        }

        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            try
            {
                // 1. Stop Production first
                try
                {
                    var prod = MasterController.Instance.Production;
                    if (prod != null && prod.IsRunning)
                        prod.StopProduction();
                }
                catch { }

                // 2. Stop Python Server
                StopPythonServer();

                // 3. Cleanup Current Page
                try
                {
                    var currentContent = MainContent?.Content;
                    if (currentContent != null)
                    {
                        if (currentContent is VisionAICam.Pages.DiagnosticsPage diagPage)
                        {
                            try { diagPage.CleanupResourcesPublic(); } catch { }
                        }

                        InvokeCleanupIfExists(currentContent, "CleanupResources");

                        if (currentContent is IDisposable disp)
                        {
                            try { disp.Dispose(); } catch { }
                        }

                        if (!(currentContent is IDisposable))
                        {
                            var disposeMethod = currentContent.GetType().GetMethod("Dispose",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                                null, Type.EmptyTypes, null);
                            if (disposeMethod != null)
                            {
                                try { disposeMethod.Invoke(currentContent, null); } catch { }
                            }
                        }
                    }
                }
                catch { }

                // 4. Clear MainContent
                try
                {
                    if (MainContent != null)
                        MainContent.Content = null;
                }
                catch { }

                // 5. Shutdown Inference Engine (best effort - don't wait)
                try
                {
                    var engineType = typeof(ClearEngine.Model.Inference.InferenceEngine);
                    var instanceProp = engineType.GetProperty("Instance",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    object? engineInstance = instanceProp != null ? instanceProp.GetValue(null) : null;

                    if (engineInstance is ClearEngine.Model.Inference.InferenceEngine engine)
                    {
                        try { engine.Dispose(); } catch { }
                    }

                    if (instanceProp != null && instanceProp.CanWrite)
                    {
                        try { instanceProp.SetValue(null, null); } catch { }
                    }
                }
                catch { }

                // 6. Shutdown Python Engine (best effort - don't wait)
                try
                {
                    if (PythonEngine.IsInitialized)
                    {
                        try { PythonEngine.Shutdown(); } catch { }
                    }
                }
                catch { }

                // 7. Run garbage collection
                try
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                }
                catch { }

                // ⭐ 8. ไม่ทำอะไรเพิ่ม - ปล่อยให้ WPF shutdown ตามปกติ
                // Python processes จะถูก kill โดย OS เมื่อ parent process ปิด
            }
            catch
            {
                // Swallow all exceptions during closing
            }
        }

        private async Task LongRunningCleanupAsync()
        {
            try
            {
                var engineType = typeof(ClearEngine.Model.Inference.InferenceEngine);
                var instanceProp = engineType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                object? engineInstance = null;
                if (instanceProp != null)
                {
                    try { engineInstance = instanceProp.GetValue(null); } catch { engineInstance = null; }
                }

                if (engineInstance is ClearEngine.Model.Inference.InferenceEngine engine)
                {
                    try { engine.Dispose(); } catch { }
                }

                if (instanceProp != null && instanceProp.CanWrite)
                {
                    try { instanceProp.SetValue(null, null); } catch { }
                }
                else
                {
                    var field = engineType.GetField("_instance", BindingFlags.Static | BindingFlags.NonPublic)
                                ?? engineType.GetField("s_instance", BindingFlags.Static | BindingFlags.NonPublic)
                                ?? engineType.GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
                    if (field != null)
                    {
                        try { field.SetValue(null, null); } catch { }
                    }
                }
            }
            catch { }

            try
            {
                if (PythonEngine.IsInitialized)
                {
                    try { PythonEngine.Shutdown(); } catch { }
                }
            }
            catch { }

            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200).ConfigureAwait(false);
                GC.Collect();
            }
            catch { }
        }

        private void InvokeCleanupIfExists(object target, string methodName)
        {
            if (target == null) return;

            try
            {
                var type = target.GetType();

                var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    try { method.Invoke(target, null); } catch { }
                    return;
                }

                var asyncMethod = type.GetMethod(methodName + "Async", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (asyncMethod != null)
                {
                    try
                    {
                        var result = asyncMethod.Invoke(target, null);
                        if (result is Task t)
                        {
                            try { t.Wait(1500); } catch { }
                        }
                    }
                    catch { }
                }
            }
            catch { }
        }

        private bool TryRunWithTimeout(Func<Task> asyncAction, int timeoutMs = 1000)
        {
            if (asyncAction == null) return false;

            try
            {
                var t = Task.Run(asyncAction);
                if (t.Wait(timeoutMs))
                {
                    return true;
                }
                else
                {
                    try { Debug.WriteLine("TryRunWithTimeout: operation timed out."); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                try { Debug.WriteLine($"TryRunWithTimeout exception: {ex}"); } catch { }
                return false;
            }
        }

        private void UserButton_Click_1(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<UserPage>(MasterController.Instance.UserPage, "User");
        }

        private void NavigatorRobotButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var robotPage = MasterController.Instance?.RobotPage;
                if (robotPage == null)
                {
                    MessageBox.Show("Robot page not available.", "Robot", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                NavigateIfNotDuplicate<Pages.RobotPage>(robotPage, "Robot");
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to open Robot page: {ex.Message}", "Robot", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void AdjustNavButtons()
        {
            try
            {
                if (BottomNavGrid == null) return;

                var buttons = BottomNavGrid.Children.OfType<Button>().ToArray();
                if (buttons.Length == 0) return;

                double available = BottomNavGrid.ActualWidth;
                if (double.IsNaN(available) || available <= 0)
                {
                    available = this.ActualWidth;
                    if (double.IsNaN(available) || available <= 0) return;
                }

                double totalMargins = buttons.Sum(b => b.Margin.Left + b.Margin.Right);

                double target = Math.Floor((available - totalMargins) / buttons.Length);

                double min = 70;  // ✅ Changed from 56 to 70
                double max = 200; // ✅ Changed from 180 to 200
                double width = Math.Max(min, Math.Min(max, target));

                foreach (var btn in buttons)
                {
                    btn.Width = width;
                    btn.FontSize = width < 80 ? 13 : 16;
                }
            }
            catch { }
        }
    }
}