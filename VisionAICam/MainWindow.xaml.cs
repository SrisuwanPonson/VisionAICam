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
        public MainWindow()
        {
            InitializeComponent();
            MainContent.Navigate(new Production());
        }

        private bool isStarted = false;

        private void StartStopButton_Click(object sender, RoutedEventArgs e)
        {
            if (!isStarted)
            {
                // Start logic here
                StartStopButton.Content = "\uE71A"; // Stop icon
                isStarted = true;
            }
            else
            {
                // Stop logic here
                StartStopButton.Content = "\uE768"; // Play icon
                isStarted = false;
            }
        }

        private void PauseButton_Click(object sender, RoutedEventArgs e)
        {

        }

        private void UserButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new UserPage()); // Navigate to UserPage
        }

        private void DataButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new DataPage()); // Navigate to DataPage
        }

        private void DiagnosticButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new DiagnosticsPage()); // Navigate to DiagnosticPage
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new SettingPage());
        }

        private void CameraButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new CameraPage()); // Navigate to CameraPage
        }

        private void ModelButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new ModelPage()); // Navigate to ModelPage
        }

        private void ProductionButton_Click(object sender, RoutedEventArgs e)
        {
            MainContent.Navigate(new Production());
        }
    }
}
