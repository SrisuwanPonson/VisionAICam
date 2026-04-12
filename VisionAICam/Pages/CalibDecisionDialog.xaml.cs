using System.Windows;

namespace VisionAICam.Pages
{
    public partial class CalibDecisionDialog : Window
    {
        public MessageBoxResult SelectedResult { get; private set; } = MessageBoxResult.Cancel;

        public CalibDecisionDialog()
        {
            InitializeComponent();
        }

        private void BtnSetReference_Click(object sender, RoutedEventArgs e)
        {
            SelectedResult = MessageBoxResult.Yes; // Set Reference
            DialogResult = true;
        }

        private void BtnContinue_Click(object sender, RoutedEventArgs e)
        {
            SelectedResult = MessageBoxResult.No; // Continue (auto-tune)
            DialogResult = true;
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            SelectedResult = MessageBoxResult.Cancel;
            DialogResult = false;
        }
    }
}

