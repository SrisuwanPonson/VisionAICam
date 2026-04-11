using System.Windows;

namespace VisionAICam.Dialogs
{
    public enum UpdateRefsDialogResult
    {
        UpdateRef,
        ContinueCal,
        Cancel
    }

    public partial class UpdateRefsDialog : Window
    {
        public UpdateRefsDialogResult Result { get; private set; } = UpdateRefsDialogResult.Cancel;

        public UpdateRefsDialog(string message)
        {
            InitializeComponent();
            MessageText.Text = message ?? string.Empty;
        }

        private void UpdateRefButton_Click(object sender, RoutedEventArgs e)
        {
            Result = UpdateRefsDialogResult.UpdateRef;
            DialogResult = true;
            Close();
        }

        private void ContinueCalButton_Click(object sender, RoutedEventArgs e)
        {
            Result = UpdateRefsDialogResult.ContinueCal;
            DialogResult = true;
            Close();
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            Result = UpdateRefsDialogResult.Cancel;
            DialogResult = false;
            Close();
        }
    }
}