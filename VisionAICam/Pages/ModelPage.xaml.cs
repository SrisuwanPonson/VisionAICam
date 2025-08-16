using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VisionAICam; // For AppSettings and SettingsManager
using Python.Runtime; // Requires pythonnet NuGet package

namespace VisionAICam.Pages
{
    public partial class ModelPage : Page
    {
        private readonly string ModelsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Models");
        private readonly string DatasetsFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Datasets");

        public ModelPage()
        {
            InitializeComponent();
        }

        private async void LoadModelButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(ModelsFolder))
                Directory.CreateDirectory(ModelsFolder);

            string ptPath = null;
            var result = MessageBox.Show(
                "Do you want to download a YOLOv8 pre-trained .pt model from a URL?\nClick 'No' to select a local file.",
                "Load Pre-trained Model", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                var url = PromptForUrl();
                if (string.IsNullOrWhiteSpace(url)) return;

                var saveDialog = new SaveFileDialog
                {
                    Filter = "YOLOv8 Pre-trained Model (*.pt)|*.pt",
                    FileName = "yolov8n.pt",
                    InitialDirectory = ModelsFolder
                };
                if (saveDialog.ShowDialog() != true) return;

                var fileName = Path.GetFileName(saveDialog.FileName);
                ptPath = Path.Combine(ModelsFolder, fileName);

                ProgressMessage.Text = "Downloading pre-trained model, please wait...";
                ProgressOverlay.Visibility = Visibility.Visible;
                try
                {
                    await DownloadFileAsync(url, ptPath);
                    CurrentModelPathText.Text = ptPath;
                    ModelDetailsText.Text = "Downloaded YOLOv8 pre-trained .pt model.";
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Download failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }
                finally
                {
                    ProgressOverlay.Visibility = Visibility.Collapsed;
                }
            }
            else if (result == MessageBoxResult.No)
            {
                var openDialog = new OpenFileDialog
                {
                    Filter = "YOLOv8 Pre-trained Model (*.pt)|*.pt",
                    InitialDirectory = ModelsFolder
                };
                if (openDialog.ShowDialog() != true) return;

                var selectedFile = openDialog.FileName;
                var fileName = Path.GetFileName(selectedFile);
                ptPath = Path.Combine(ModelsFolder, fileName);

                if (!string.Equals(selectedFile, ptPath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(selectedFile, ptPath, true);
                }

                CurrentModelPathText.Text = ptPath;
                ModelDetailsText.Text = "Selected YOLOv8 pre-trained .pt model.";
            }
            else
            {
                return;
            }
        }

        private void SelectModelForRunningButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(ModelsFolder))
                Directory.CreateDirectory(ModelsFolder);

            var openDialog = new OpenFileDialog
            {
                Filter = "YOLOv8 Model (*.pt)|*.pt",
                InitialDirectory = ModelsFolder,
                Title = "Select Model for Running"
            };
            if (openDialog.ShowDialog() != true) return;

            var selectedFile = openDialog.FileName;
            var fileName = Path.GetFileName(selectedFile);
            var modelPath = Path.Combine(ModelsFolder, fileName);

            if (!string.Equals(selectedFile, modelPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(selectedFile, modelPath, true);
            }

            CurrentModelPathText.Text = modelPath;
            ModelDetailsText.Text = "Model selected for running:\n" + modelPath;
        }

        private void SetAsDefaultModelButton_Click(object sender, RoutedEventArgs e)
        {
            var modelPath = CurrentModelPathText.Text;
            if (string.IsNullOrWhiteSpace(modelPath) || modelPath == "(none loaded)" || modelPath == "(none selected)")
            {
                MessageBox.Show("No model selected to set as default.", "Set Default Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var settings = SettingsManager.Load();
            settings.DefaultModelPath = modelPath;
            SettingsManager.Save(settings);

            MessageBox.Show("Default model updated.", "Set Default Model", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        private async void DownloadDatasetButton_Click(object sender, RoutedEventArgs e)
        {
            if (!Directory.Exists(DatasetsFolder))
                Directory.CreateDirectory(DatasetsFolder);

            var url = PromptForDatasetUrl();
            if (string.IsNullOrWhiteSpace(url)) return;

            var saveDialog = new SaveFileDialog
            {
                Filter = "ZIP Archive (*.zip)|*.zip|All Files|*.*",
                FileName = "dataset.zip",
                InitialDirectory = DatasetsFolder
            };
            if (saveDialog.ShowDialog() != true) return;

            var fileName = Path.GetFileName(saveDialog.FileName);
            var datasetPath = Path.Combine(DatasetsFolder, fileName);

            ProgressMessage.Text = "Downloading dataset, please wait...";
            ProgressOverlay.Visibility = Visibility.Visible;
            try
            {
                await DownloadFileAsync(url, datasetPath);
                TrainingStatusText.Text = $"Downloaded dataset: {datasetPath}";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Dataset download failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                ProgressOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async void TrainModelButton_Click(object sender, RoutedEventArgs e)
        {
            int epochs = int.TryParse(EpochsTextBox.Text, out var eVal) ? eVal : 50;
            int batchSize = int.TryParse(BatchSizeTextBox.Text, out var bVal) ? bVal : 16;
            double learningRate = double.TryParse(LearningRateTextBox.Text, out var lVal) ? lVal : 0.01;
            int imageSize = int.TryParse(ImageSizeTextBox.Text, out var iVal) ? iVal : 640;
            string pretrainedWeights = PretrainedWeightsTextBox.Text;

            ProgressMessage.Text = "Training model, please wait...";
            ProgressOverlay.Visibility = Visibility.Visible;
            TrainingStatusText.Text = "Training in progress...";

            try
            {
                // Placeholder for actual training
                await Task.Delay(2000);
                TrainingStatusText.Text = "Training complete!";
            }
            catch (Exception ex)
            {
                TrainingStatusText.Text = $"Training failed: {ex.Message}";
            }
            finally
            {
                ProgressOverlay.Visibility = Visibility.Collapsed;
            }
        }

        private async Task DownloadFileAsync(string url, string savePath)
        {
            using var client = new HttpClient();
            var data = await client.GetByteArrayAsync(url);
            await File.WriteAllBytesAsync(savePath, data);
        }

        private string PromptForUrl()
        {
            var inputDialog = new Window
            {
                Title = "Enter YOLOv8 Pre-trained .pt Model URL",
                Width = 400,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow
            };
            var stack = new StackPanel { Margin = new Thickness(10) };
            var textBox = new TextBox
            {
                Width = 360,
                Text = "https://github.com/ultralytics/assets/releases/download/v0.0.0/yolov8n.pt"
            };
            var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 10, 0, 0), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
            stack.Children.Add(new TextBlock { Text = "Paste the direct download URL for the YOLOv8 pre-trained .pt model:" });
            stack.Children.Add(textBox);
            stack.Children.Add(okButton);
            inputDialog.Content = stack;

            string url = null;
            okButton.Click += (s, e) => { url = textBox.Text; inputDialog.DialogResult = true; inputDialog.Close(); };
            inputDialog.ShowDialog();
            return url;
        }

        private string PromptForDatasetUrl()
        {
            var inputDialog = new Window
            {
                Title = "Enter Dataset URL",
                Width = 400,
                Height = 120,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                Owner = Application.Current.MainWindow
            };
            var stack = new StackPanel { Margin = new Thickness(10) };
            var textBox = new TextBox
            {
                Width = 360,
                Text = "https://ultralytics.com/assets/coco128.zip"
            };
            var okButton = new Button { Content = "OK", Width = 80, Margin = new Thickness(0, 10, 0, 0), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right };
            stack.Children.Add(new TextBlock { Text = "Paste the direct download URL for the dataset:" });
            stack.Children.Add(textBox);
            stack.Children.Add(okButton);
            inputDialog.Content = stack;

            string url = null;
            okButton.Click += (s, e) => { url = textBox.Text; inputDialog.DialogResult = true; inputDialog.Close(); };
            inputDialog.ShowDialog();
            return url;
        }

        // --- Python.NET Model Test Example ---

        private void TestModelWithPythonNetButton_Click(object sender, RoutedEventArgs e)
        {
            var modelPath = CurrentModelPathText.Text;
            if (string.IsNullOrWhiteSpace(modelPath) || !File.Exists(modelPath))
            {
                MessageBox.Show("No valid model selected.", "Test Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = TestModelWithPythonNet(modelPath);
                MessageBox.Show(result, "Python.NET Test Result", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Python.NET test failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string TestModelWithPythonNet(string modelPath)
        {
            string pythonScriptDir = AppDomain.CurrentDomain.BaseDirectory; // Adjust if needed

            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                bool pathExists = false;
                foreach (dynamic p in sys.path)
                {
                    if (pythonScriptDir.Equals((string)p.ToString(), StringComparison.OrdinalIgnoreCase))
                    {
                        pathExists = true;
                        break;
                    }
                }
                if (!pathExists)
                    sys.path.append(pythonScriptDir);

                dynamic inference = Py.Import("inference");
                // Use a test image (ensure test.jpg exists in your base directory)
                byte[] dummyImage = File.ReadAllBytes("test.jpg");
                dynamic results = inference.detect(dummyImage);

                string output = "";
                foreach (dynamic det in results)
                {
                    output += $"{det["class"]} ({det["confidence"]}): {string.Join(",", det["box"])}\n";
                }
                return output;
            }
        }
    }
}
