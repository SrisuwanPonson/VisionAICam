using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VisionAICam.Views
{
    /// <summary>
    /// Interaction logic for SplashWindow.xaml
    /// </summary>
    public partial class SplashWindow : Window
    {
        public SplashWindow()
        {
            InitializeComponent();
        }

        /// <summary>
        /// Update the status text shown on the splash window.
        /// Safe to call from any thread.
        /// </summary>
        public void ReportStatus(string status)
        {
            if (Dispatcher.CheckAccess())
            {
                StatusText.Text = status;
            }
            else
            {
                Dispatcher.Invoke(() => StatusText.Text = status, DispatcherPriority.Normal);
            }
        }

        /// <summary>
        /// Mark the splash as complete (stop indeterminate progress and set full value).
        /// Safe to call from any thread.
        /// </summary>
        public void ShowComplete()
        {
            if (Dispatcher.CheckAccess())
            {
                ProgressBar.IsIndeterminate = false;
                ProgressBar.Value = 100;
            }
            else
            {
                Dispatcher.Invoke(() =>
                {
                    ProgressBar.IsIndeterminate = false;
                    ProgressBar.Value = 100;
                }, DispatcherPriority.Normal);
            }
        }
    }
}
