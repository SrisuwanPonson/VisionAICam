using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using VisionAICam.Core;
using VisionAICam.Services;

namespace VisionAICam
{
    public partial class App : Application
    {
        private const string MutexName = "VisionAICam_SingleInstance_Mutex_v1";
        private static Mutex? _singleInstanceMutex;

        protected override void OnStartup(StartupEventArgs e)
        {
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, MutexName, out createdNew);

            if (!createdNew)
            {
                MessageBox.Show("Another instance is already running.", "Duplicate Detected", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }

            base.OnStartup(e);

            // show simple splash
            var splash = new Views.SplashWindow();
            splash.Show();

            // run a minimal initialization on a background thread and then show main window
            _ = Task.Run(async () =>
            {
                try
                {
                    // Determine whether to prewarm inference from persisted settings (fallback to true).
                    bool prewarm = true;
                    try
                    {
                        var settings = SettingsManager.Load();
                        if (settings != null)
                        {
                            var prop = settings.GetType().GetProperty("InferencePrewarm");
                            if (prop != null)
                                prewarm = Convert.ToBoolean(prop.GetValue(settings) ?? true);
                        }
                    }
                    catch
                    {
                        // ignore and keep default prewarm = true
                    }

                    // basic initialization (no progress reporting)
                    await MasterController.Instance.InitializeAsync(prewarmInferenceEngine: prewarm).ConfigureAwait(false);

                    // show main window on UI thread
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        var main = new MainWindow();
                        Application.Current.MainWindow = main;
                        main.Show();

                        try { splash.Close(); } catch { }
                    });
                }
                catch (Exception ex)
                {
                    Application.Current.Dispatcher.Invoke(() =>
                    {
                        try { splash.Close(); } catch { }
                        MessageBox.Show($"Startup failed: {ex.Message}", "Startup Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        Application.Current.Shutdown();
                    });
                }
            });
        }

        protected override void OnExit(ExitEventArgs e)
        {
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
                _singleInstanceMutex?.Dispose();
            }
            catch { }

            base.OnExit(e);
        }
    }
}