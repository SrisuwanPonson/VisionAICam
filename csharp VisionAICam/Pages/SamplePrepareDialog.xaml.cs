using System.Windows;

namespace VisionAICam.Pages
{
    public partial class SamplePrepareDialog : Window
    {
        public SamplePrepareDialog()
        {
            InitializeComponent();
        }

        public SamplePrepareDialog(string message) : this()
        {
            try { TxtMessage.Text = message ?? TxtMessage.Text; } catch { }
        }

        // Helper to show result (true == OK, false/null == Cancel)
        private void BtnOk_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }

        private void BtnCancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }

  
}