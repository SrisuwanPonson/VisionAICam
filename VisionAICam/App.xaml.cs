using System.Diagnostics;
using System.Windows;
using VisionAICam;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (IsAnotherInstanceRunning())
        {
            MessageBox.Show("Another instance is already running.", "Duplicate Detected", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        new MainWindow().Show();
    }
    public static bool IsAnotherInstanceRunning()
    {
        var current = Process.GetCurrentProcess();
        var running = Process.GetProcessesByName(current.ProcessName);
        return running.Any(p => p.Id != current.Id);
    }

}