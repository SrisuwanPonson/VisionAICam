using System;
using System.Windows;
using System.Windows.Controls;
using WinForms = System.Windows.Forms;

namespace VisionAICam.Model
{
    /// <summary>
    /// Interaction logic for DatasetPreparationPage.xaml
    /// </summary>
    public partial class DatasetPreparationPage : Page
    {
        public DatasetPreparationPage()
        {
            InitializeComponent();
        }

        private void CreateDatasetButton_Click(object sender, RoutedEventArgs e)
        {
            CreatePanel.Visibility = Visibility.Visible;
            LoadPanel.Visibility = Visibility.Collapsed;
        }

        private void LoadDatasetButton_Click(object sender, RoutedEventArgs e)
        {
            CreatePanel.Visibility = Visibility.Collapsed;
            LoadPanel.Visibility = Visibility.Visible;
        }

        private void CreateButton_Click(object sender, RoutedEventArgs e)
        {
            string datasetName = DatasetNameTextBox.Text.Trim();
            string[] classes = ClassesTextBox.Text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

            if (string.IsNullOrWhiteSpace(datasetName))
            {
                System.Windows.MessageBox.Show("Please enter a dataset name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (classes.Length == 0)
            {
                System.Windows.MessageBox.Show("Please enter at least one class name.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // TODO: Implement actual dataset creation logic here
            System.Windows.MessageBox.Show(
                $"Dataset '{datasetName}' with {classes.Length} classes created (placeholder).",
                "Create Dataset", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            //using var dialog = new WinForms.FolderBrowserDialog();
            //if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            //{
            //    LoadedDatasetInfo.Text = $"Loaded dataset from: {dialog.SelectedPath}";
            //    // TODO: Implement actual dataset loading logic here
            //}
        }
    }
}
