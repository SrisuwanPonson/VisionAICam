using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Navigation;
using VisionAICam.Pages;

namespace VisionAICam
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        private Production? _productionPage;

        public MainWindow()
        {
            InitializeComponent();
            _productionPage = new Production();
            MainContent.Navigate(_productionPage);
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
                        PauseButton.Content = "\uE768"; // Play icon (resume)
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

        private void UserButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new UserPage());
        }

        private void DataButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new DataPage());
        }

        private void DiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new DiagnosticsPage());
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new SettingPage());
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new CameraPage());
        }

        private void ModelButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new ModelPage());
        }

        private void ProductionButton_Click(object sender, RoutedEventArgs e)
        {
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
    }
}
