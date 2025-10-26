using System.Windows;
using System.Windows.Controls;
using VisionAICam.Pages;
using System.ComponentModel;
using Python.Runtime;
using System;
using System.Reflection;

namespace VisionAICam
{
    public partial class MainWindow : Window
    {
        private Production? _productionPage;

        public MainWindow()
        {
            InitializeComponent();

            if (IsAnotherInstanceRunning())
            {
                //MessageBox.Show("Another instance of the application is already running.", "Instance Detected", MessageBoxButton.OK, MessageBoxImage.Warning);
                Application.Current.Shutdown();
                return;
            }

            if (MainContent.Content is not Production)
            {
                _productionPage ??= new Production();
                MainContent.Navigate(_productionPage);
            }

            // Subscribe to window closing to perform final cleanup
            this.Closing -= MainWindow_Closing;
            this.Closing += MainWindow_Closing;

            //MessageBox.Show("MainWindow has been created.", "Startup", MessageBoxButton.OK, MessageBoxImage.Information);
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
            NavigateIfNotDuplicate<UserPage>(new UserPage(), "User");
        }

        private void DataButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<DataPage>(new DataPage(), "Data");
        }

        private void DiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<DiagnosticsPage>(new DiagnosticsPage(), "Diagnostics");
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<SettingPage>(new SettingPage(), "Settings");
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<CameraPage>(new CameraPage(), "Camera");
        }

        private void ProductionButton_Click(object sender, RoutedEventArgs e)
        {
            if (MainContent.Content is Production)
            {
                MessageBox.Show("The Production page is already open.", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (_productionPage == null)
                _productionPage = new Production();

            MainContent.Navigate(_productionPage);
        }

        private void ExitButton_Click(object sender, RoutedEventArgs e)
        {
            if (_productionPage != null && _productionPage.IsRunning)
            {
                _productionPage.StopProduction();
            }

            Application.Current.Shutdown();
        }

        private void DataSetButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<DataSetPage>(new DataSetPage(), "Dataset");
        }

        private void ModelButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateIfNotDuplicate<ModelPage>(new ModelPage(), "Model");
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
                    if (_productionPage != null && _productionPage.IsRunning)
                        _productionPage.StopProduction();
                }
                catch { }

                try
                {
                    var currentContent = MainContent?.Content;
                    if (currentContent != null)
                    {
                        // If it's DiagnosticsPage, call its public cleanup helper so resources are released deterministically.
                        if (currentContent is VisionAICam.Pages.DiagnosticsPage diagPage)
                        {
                            try { diagPage.CleanupResourcesPublic(); } catch { }
                        }

                        // Best-effort: call CleanupResources() if defined (public or non-public) for other page types
                        InvokeCleanupIfExists(currentContent, "CleanupResources");

                        // If page implements IDisposable, dispose it
                        if (currentContent is IDisposable disp)
                        {
                            try { disp.Dispose(); } catch { }
                        }

                        // Also try to call parameterless "Dispose" via reflection if not castable
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

                // Attempt quick, bounded disposal of heavy resources that might block.
                // Use short timeouts so window close / debugger stop isn't blocked indefinitely.
                try
                {
                    // Dispose inference engine (sync, bounded)
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
                    // Shutdown PythonEngine with a short timeout (some Python shutdowns block; don't hang close)
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

        /// <summary>
        /// Long-running cleanup performed off the UI thread.
        /// Dispose singletons, shutdown Python, force GC and wait briefly for finalizers.
        /// </summary>
        private async Task LongRunningCleanupAsync()
        {
            // Dispose inference engine singleton if present (direct access + safe reflection clearing)
            try
            {
                var engineType = typeof(ClearEngine.Model.Inference.InferenceEngine);

                // Try to get the public Instance property if available
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

                // If the Instance property is writable, clear it via the property.
                if (instanceProp != null && instanceProp.CanWrite)
                {
                    try { instanceProp.SetValue(null, null); } catch { }
                }
                else
                {
                    // Otherwise attempt to clear common private static backing fields (_instance, s_instance, instance)
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

            // Shutdown Python runtime if initialized.
            try
            {
                // PythonEngine calls can require the main thread in some environments.
                // Attempt shutdown from background thread; if it fails, ignore to avoid blocking close.
                if (PythonEngine.IsInitialized)
                {
                    try { PythonEngine.Shutdown(); } catch { }
                }
            }
            catch { }

            // Give native resources a chance to finalize — do this with small delays so OS has time.
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                await Task.Delay(200).ConfigureAwait(false);
                GC.Collect();
            }
            catch { }
        }

        /// <summary>
        /// If an object exposes a cleanup method name, invoke it (best-effort).
        /// </summary>
        private void InvokeCleanupIfExists(object target, string methodName)
        {
            if (target == null) return;

            try
            {
                var type = target.GetType();

                // Look for public or non-public instance method without parameters
                var method = type.GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    try { method.Invoke(target, null); } catch { }
                    return;
                }

                // Also check for async Task CleanupResourcesAsync() pattern
                var asyncMethod = type.GetMethod(methodName + "Async", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (asyncMethod != null)
                {
                    try
                    {
                        var result = asyncMethod.Invoke(target, null);
                        // if returns a Task, attempt to Wait briefly
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

        /// <summary>
        /// Run an async action and block up to <paramref name="timeoutMs"/> milliseconds waiting for completion.
        /// Returns true if the action completed within the timeout, false on timeout or exception.
        /// This keeps window close responsive while giving critical disposals a bounded chance to finish.
        /// </summary>
        private bool TryRunWithTimeout(Func<Task> asyncAction, int timeoutMs = 1000)
        {
            if (asyncAction == null) return false;

            try
            {
                var t = Task.Run(asyncAction);
                if (t.Wait(timeoutMs))
                {
                    // Completed within timeout
                    return true;
                }
                else
                {
                    // Timed out - do not block further
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
    }
}