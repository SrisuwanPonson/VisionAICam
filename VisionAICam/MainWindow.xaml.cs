using System.Linq;
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

namespace VisionAICam
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            // Attach to navigation events so we can show/hide side panels depending on the page
            MainContent.Navigated -= MainContent_Navigated;
            MainContent.Navigated += MainContent_Navigated;

            // Responsive nav buttons: adjust sizes on load and when window resizes
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

            // Ensure side panels reflect the current content initially
            UpdateSidePanelVisibility();

            // Subscribe to window closing to perform final cleanup
            this.Closing -= MainWindow_Closing;
            this.Closing += MainWindow_Closing;
        }

        private void MainContent_Navigated(object? sender, NavigationEventArgs e)
        {
            UpdateSidePanelVisibility();
        }

        /// <summary>
        /// Shows side panels only when the Production page is displayed; hides them for all other pages.
        /// Also updates the small expand buttons visibility.
        /// </summary>
        private void UpdateSidePanelVisibility()
        {
            try
            {
                bool isProduction = MainContent?.Content is Production;

                if (LeftPanel != null)
                {
                    LeftPanel.Visibility = isProduction ? Visibility.Visible : Visibility.Collapsed;
                }

                if (RightPanel != null)
                {
                    RightPanel.Visibility = isProduction ? Visibility.Visible : Visibility.Collapsed;
                }

                // Expand buttons shown only when corresponding panel is collapsed and Production page is active
                if (LeftExpandButton != null)
                {
                    LeftExpandButton.Visibility = (isProduction && (LeftPanel == null || LeftPanel.Visibility == Visibility.Collapsed))
                                                  ? Visibility.Visible : Visibility.Collapsed;
                }

                if (RightExpandButton != null)
                {
                    RightExpandButton.Visibility = (isProduction && (RightPanel == null || RightPanel.Visibility == Visibility.Collapsed))
                                                   ? Visibility.Visible : Visibility.Collapsed;
                }

                // Also keep Start/Stop and Pause buttons consistent when panels hidden
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
            var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            var processes = System.Diagnostics.Process.GetProcessesByName(currentProcess.ProcessName);
            return processes.Length > 1;
        }

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainContent.Content is Production production)
            {
                if (!production.IsRunning)
                {
                    production.StartProduction();
                    StartStopButton.Content = "\uE71A"; // Stop icon
                    PauseButton.IsEnabled = true;
                }
                else
                {
                    production.StopProduction();
                    StartStopButton.Content = "\uE768"; // Play icon
                    PauseButton.IsEnabled = false;
                    PauseButton.Content = "\uE769"; // Reset to pause icon
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
                        PauseButton.Content = "\uE768"; // Play icon
                    }
                    else
                    {
                        production.ResumeProduction();
                        PauseButton.Content = "\uE769"; // Pause icon
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
                {
                    prod.StopProduction();
                }
            }
            catch { }

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

        // Left / Right collapse/expand handlers ------------------------------------

        private void LeftCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LeftPanel != null)
                {
                    LeftPanel.Visibility = Visibility.Collapsed;
                }
                if (LeftExpandButton != null)
                {
                    LeftExpandButton.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private void LeftExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (LeftPanel != null)
                {
                    LeftPanel.Visibility = Visibility.Visible;
                }
                if (LeftExpandButton != null)
                {
                    LeftExpandButton.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        private void RightCollapseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RightPanel != null)
                {
                    RightPanel.Visibility = Visibility.Collapsed;
                }
                if (RightExpandButton != null)
                {
                    RightExpandButton.Visibility = Visibility.Visible;
                }
            }
            catch { }
        }

        private void RightExpandButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RightPanel != null)
                {
                    RightPanel.Visibility = Visibility.Visible;
                }
                if (RightExpandButton != null)
                {
                    RightExpandButton.Visibility = Visibility.Collapsed;
                }
            }
            catch { }
        }

        /// <summary>
        /// MainWindow_Closing: perform only quick synchronous cleanup and schedule heavier disposal on a background thread.
        /// This keeps window close fast while still doing final cleanup asynchronously.
        /// </summary>
        private void MainWindow_Closing(object? sender, CancelEventArgs e)
        {
            try
            {
                // Quick synchronous work (must be fast)
                try
                {
                    var prod = MasterController.Instance.Production;
                    if (prod != null && prod.IsRunning)
                        prod.StopProduction();
                }
                catch { }

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
                            var disposeMethod = currentContent.GetType().GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                            if (disposeMethod != null)
                            {
                                try { disposeMethod.Invoke(currentContent, null); } catch { }
                            }
                        }
                    }
                }
                catch { }

                try
                {
                    if (MainContent != null)
                        MainContent.Content = null; // trigger Unloaded handlers
                }
                catch { }

                try
                {
                    TryRunWithTimeout(() =>
                    {
                        try
                        {
                            var engineType = typeof(ClearEngine.Model.Inference.InferenceEngine);
                            var instanceProp = engineType.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            object? engineInstance = instanceProp != null ? instanceProp.GetValue(null) : null;

                            if (engineInstance is ClearEngine.Model.Inference.InferenceEngine engine)
                            {
                                engine.Dispose();
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
                        return Task.CompletedTask;
                    }, timeoutMs: 1000);
                }
                catch { }

                try
                {
                    TryRunWithTimeout(() =>
                    {
                        try
                        {
                            if (PythonEngine.IsInitialized)
                            {
                                PythonEngine.Shutdown();
                            }
                        }
                        catch { }
                        return Task.CompletedTask;
                    }, timeoutMs: 1000);
                }
                catch { }

                // Schedule remaining cleanup off-UI (non-blocking)
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await LongRunningCleanupAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        try { System.Diagnostics.Debug.WriteLine($"LongRunningCleanupAsync error: {ex}"); } catch { }
                    }
                });
            }
            catch { }
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
                        if (result is System.Threading.Tasks.Task t)
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
                    try { System.Diagnostics.Debug.WriteLine("TryRunWithTimeout: operation timed out."); } catch { }
                    return false;
                }
            }
            catch (Exception ex)
            {
                try { System.Diagnostics.Debug.WriteLine($"TryRunWithTimeout exception: {ex}"); } catch { }
                return false;
            }
        }

        private void UserButton_Click_1(object sender, RoutedEventArgs e) { NavigateIfNotDuplicate<UserPage>(MasterController.Instance.UserPage, "User"); }

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

                // approximate total horizontal margins for children
                double totalMargins = buttons.Sum(b => b.Margin.Left + b.Margin.Right);

                double target = Math.Floor((available - totalMargins) / buttons.Length);

                double min = 56;
                double max = 180;
                double width = Math.Max(min, Math.Min(max, target));

                foreach (var btn in buttons)
                {
                    btn.Width = width;
                    btn.FontSize = width < 80 ? 13 : 16;
                }
            }
            catch
            {
                // best-effort; don't throw on resize
            }
        }
    }
}