using System.Windows;
using System.Windows.Controls;
using VisionAICam.Pages;

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
    }
}