using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Ookii.Dialogs.Wpf;
using Python.Runtime;

namespace VisionAICam.Pages
{
    public partial class ModelPage : Page
    {
        // Store image list and navigation state
        private List<string> _imagePaths = new();
        private int _currentImageIndex = -1;
        private int _currentImageWidth;
        private int _currentImageHeight;
        private string? _currentImagePath;

        // Full-screen state
        private bool _isFullScreen = false;

        public ModelPage()
        {
            InitializeComponent();
            this.Focusable = true;
            this.Loaded += (s, e) => this.Focus();
        }

        // Keyboard navigation: W for next, S for previous (matches XAML KeyDown="Page_KeyDown")
        private void Page_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F)
            {
                NextImage_Click(sender, e);
                e.Handled = true;
            }
            else if (e.Key == Key.S)
            {
                PrevImage_Click(sender, e);
                e.Handled = true;
            }
        }

        // Show a step-by-step help dialog
        private void ShowHelp()
        {
            string helpText =
@"Step 1: Load a Folder
- Click 'Load Folder' to select a folder of images for annotation.

Step 2: Manage Labels
- Select or add a label from the dropdown before drawing boxes.

Step 3: Annotate
- Drag on the image to draw a bounding box.
- Click 'Add Box' to save the box with the selected label.

Step 4: Edit or Remove
- Select a box in the list to remove it if needed.

Step 5: Save
- Click 'Save Annotations' to save your work for the current image.

Step 6: Export
- When finished, click 'Export YOLO' to export all annotations in Roboflow/YOLO format.

Tip: Use 'Previous' and 'Next' to navigate images. Progress is shown above the image.";

            MessageBox.Show(helpText, "How to Prepare and Annotate Data", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        // Load a folder of images using Ookii.Dialogs.Wpf
        private void LoadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select a folder of images",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == true)
            {
                string folderPath = dialog.SelectedPath;
                _imagePaths = Directory.GetFiles(folderPath, "*.*")
                    .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => f)
                    .ToList();

                if (_imagePaths.Count == 0)
                {
                    MessageBox.Show("No images found in the selected folder.", "No Images", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                _currentImageIndex = 0;
                LoadImageAtIndex(_currentImageIndex);
            }
        }

        // Full-screen toggle for image area
        private void FullScreenToggleButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isFullScreen)
            {
                // Hide all except image area
                HelpExpander.Visibility = Visibility.Collapsed;
                TitlePanel.Visibility = Visibility.Collapsed;
                ModelDetailsPanel.Visibility = Visibility.Collapsed;
                AnnotationPanel.Visibility = Visibility.Collapsed;

                // Stretch image area to fill
                Grid.SetRow(ImageAreaContainer, 0);
                Grid.SetRowSpan(ImageAreaContainer, 4);
                _isFullScreen = true;
            }
            else
            {
                // Restore all UI
                HelpExpander.Visibility = Visibility.Visible;
                TitlePanel.Visibility = Visibility.Visible;
                ModelDetailsPanel.Visibility = Visibility.Visible;
                AnnotationPanel.Visibility = Visibility.Visible;

                // Restore image area position
                Grid.SetRow(ImageAreaContainer, 3);
                Grid.SetRowSpan(ImageAreaContainer, 1);
                _isFullScreen = false;
            }
        }

        // Load image by index and update UI
        private void LoadImageAtIndex(int index)
        {
            if (_imagePaths == null || index < 0 || index >= _imagePaths.Count)
                return;

            var imagePath = _imagePaths[index];
            _currentImagePath = imagePath;

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(imagePath);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();

            LabelingImage.Source = bitmap;
            _currentImageWidth = bitmap.PixelWidth;
            _currentImageHeight = bitmap.PixelHeight;

            // Clear previous annotations
            AnnotationListView.ItemsSource = null;
            BoundingBoxCanvas.Children.Clear();

            // Update progress text
            ImageProgressText.Text = $"Image {index + 1} of {_imagePaths.Count} ({(int)(((index + 1) * 100.0) / _imagePaths.Count)}%)";

            // Optionally update model details or status
            ModelDetailsText.Text = $"Loaded: {Path.GetFileName(imagePath)} ({_currentImageWidth}x{_currentImageHeight})";
        }

        private void PrevImage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImageIndex > 0)
            {
                SaveAnnotations_Click(sender, e); // Optionally save before navigating
                _currentImageIndex--;
                LoadImageAtIndex(_currentImageIndex);
            }
        }

        private void NextImage_Click(object sender, RoutedEventArgs e)
        {
            if (_imagePaths != null && _currentImageIndex < _imagePaths.Count - 1)
            {
                SaveAnnotations_Click(sender, e); // Optionally save before navigating
                _currentImageIndex++;
                LoadImageAtIndex(_currentImageIndex);
            }
        }

        private void AddBox_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement add bounding box logic
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement remove selected annotation logic
        }

        private void SaveAnnotations_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement save annotations logic
        }

        private void ExportYolo_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement YOLO export logic
        }

        // Python.NET model test (optional, for advanced users)
        private void TestModelWithPythonNetButton_Click(object sender, RoutedEventArgs e)
        {
            var modelPath = ModelDetailsText.Text?.Trim();
            if (string.IsNullOrEmpty(modelPath) || !File.Exists(modelPath))
            {
                MessageBox.Show("No valid model selected.", "Test Model", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var result = RunPythonNetInference(modelPath);
                MessageBox.Show(result, "Python.NET Test Result", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Python.NET test failed: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private string RunPythonNetInference(string modelPath)
        {
            string pythonScriptDir = AppDomain.CurrentDomain.BaseDirectory;

            using (Py.GIL())
            {
                dynamic sys = Py.Import("sys");
                if (!sys.path.__contains__(pythonScriptDir))
                    sys.path.append(pythonScriptDir);

                dynamic inference = Py.Import("inference");
                var testImagePath = Path.Combine(pythonScriptDir, "test.jpg");
                if (!File.Exists(testImagePath))
                    throw new FileNotFoundException("Test image (test.jpg) not found.", testImagePath);

                byte[] dummyImage = File.ReadAllBytes(testImagePath);
                dynamic results = inference.detect(dummyImage);

                var output = "";
                foreach (dynamic det in results)
                {
                    output += $"{det["class"]} ({det["confidence"]}): {string.Join(",", det["box"])}\n";
                }
                return output;
            }
        }
    }
}
