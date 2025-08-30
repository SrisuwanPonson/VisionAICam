using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Shapes;
using System.Windows.Media;
using Microsoft.Win32;
using System.IO;

namespace VisionAICam.Pages
{
   
    public partial class ModelPage : Page
    {
        private UIElement selectedBlock;
        private Point dragStartPoint;
        private List<Line> connectionLines = new();

        // --- NEW: Track block references ---
        private Border datasetBlock;
        private Border modelBlock;
        private Border trainBlock;

        public ModelPage()
        {
            InitializeComponent();
            // Do NOT call InitializeTrainingBlocks here!
            // Blocks will be added step-by-step as user progresses.
            TrainingStatusText.Text = "Please select a dataset to begin.";
        }

        #region Block
        // --- MODIFIED: No blocks at startup ---

        private Border CreateBlock(string label, double left, double top, string tag)
        {
            var block = new Border
            {
                Width = 150,
                Height = 80,
                Background = new SolidColorBrush(Color.FromRgb(58, 58, 61)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Tag = tag,
                Child = new TextBlock
                {
                    Text = label,
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.Bold,
                    FontSize = 16,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, top);

            block.MouseLeftButtonDown += Block_MouseLeftButtonDown;
            block.MouseMove += Block_MouseMove;
            block.MouseLeftButtonUp += Block_MouseLeftButtonUp;
            block.MouseLeftButtonUp += Block_Click;

            return block;
        }

        private void Block_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            selectedBlock = sender as UIElement;
            dragStartPoint = e.GetPosition(TrainingCanvas);
            selectedBlock.CaptureMouse();
        }

        private void Block_MouseMove(object sender, MouseEventArgs e)
        {
            if (selectedBlock != null && selectedBlock.IsMouseCaptured)
            {
                Point currentPoint = e.GetPosition(TrainingCanvas);
                double offsetX = currentPoint.X - dragStartPoint.X;
                double offsetY = currentPoint.Y - dragStartPoint.Y;

                Canvas.SetLeft(selectedBlock, Canvas.GetLeft(selectedBlock) + offsetX);
                Canvas.SetTop(selectedBlock, Canvas.GetTop(selectedBlock) + offsetY);

                dragStartPoint = currentPoint;
            }
        }

        private void Block_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (selectedBlock != null)
            {
                selectedBlock.ReleaseMouseCapture();
                selectedBlock = null;
            }
        }

        private void HandleBlockAction(string tag)
        {
            switch (tag)
            {
                case "Dataset":
                    {
                        var folderDialog = new System.Windows.Forms.FolderBrowserDialog
                        {
                            Description = "📂 Select your YOLOv8 dataset folder",
                            UseDescriptionForTitle = true,
                            ShowNewFolderButton = false
                        };

                        if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                        {
                            string datasetPath = folderDialog.SelectedPath;

                            Task.Run(() =>
                            {
                                bool isValid = ValidateYoloDatasetStructure(datasetPath);
                                var imageToLabelMap = isValid ? SyncYoloAnnotations(datasetPath) : null;

                                Dispatcher.Invoke(() =>
                                {
                                    if (!isValid)
                                    {
                                        MessageBox.Show(
                                            "⚠️ Invalid dataset structure.\nExpected folders: train/valid/test with images/labels subfolders and a data.yaml file.",
                                            "Validation Failed",
                                            MessageBoxButton.OK,
                                            MessageBoxImage.Warning
                                        );
                                        TrainingStatusText.Text = "Dataset validation failed. Please select a valid dataset.";
                                        return;
                                    }

                                    MessageBox.Show(
                                        $"✅ Dataset loaded successfully:\n{datasetPath}",
                                        "Dataset Validated",
                                        MessageBoxButton.OK,
                                        MessageBoxImage.Information
                                    );

                                    AddOrSelectDataset(datasetPath);

                                    // --- NEW: Draw dataset block if not already present ---
                                    if (datasetBlock == null)
                                    {
                                        datasetBlock = CreateBlock("📁 Dataset", 50, 50, "Dataset");
                                        TrainingCanvas.Children.Add(datasetBlock);
                                        TrainingStatusText.Text = "Dataset added. Now select a model.";
                                    }

                                    // Optionally: Remove model/train blocks if user re-selects dataset
                                    // RemoveModelAndTrainBlocks();
                                });
                            });
                        }
                        break;
                    }

                case "Model":
                    var modelWindow = new Window
                    {
                        Title = "Select Model Type",
                        Width = 300,
                        Height = 150,
                        WindowStartupLocation = WindowStartupLocation.CenterScreen,
                        ResizeMode = ResizeMode.NoResize
                    };

                    var panel = new StackPanel { Margin = new Thickness(20) };
                    var comboBox = new ComboBox
                    {
                        ItemsSource = new List<string> { "YOLOv8", "ONNX", "Custom" },
                        SelectedIndex = 0,
                        Margin = new Thickness(0, 0, 0, 10)
                    };
                    var confirmButton = new Button
                    {
                        Content = "Confirm",
                        Width = 100,
                        HorizontalAlignment = HorizontalAlignment.Center
                    };
                    confirmButton.Click += (s, e) =>
                    {
                        MessageBox.Show($"🧠 Model selected: {comboBox.SelectedItem}");
                        modelWindow.Close();

                        // --- NEW: Draw model block if not already present ---
                        if (modelBlock == null)
                        {
                            modelBlock = CreateBlock("🧠 Model", 300, 50, "Model");
                            TrainingCanvas.Children.Add(modelBlock);
                            TrainingStatusText.Text = "Model added. Ready to train.";
                        }

                        // --- NEW: Draw train block if not already present ---
                        if (trainBlock == null)
                        {
                            trainBlock = CreateBlock("🚀 Train", 550, 50, "Train");
                            TrainingCanvas.Children.Add(trainBlock);
                        }
                    };

                    panel.Children.Add(comboBox);
                    panel.Children.Add(confirmButton);
                    modelWindow.Content = panel;
                    modelWindow.ShowDialog();
                    break;

                case "Train":
                    var result = MessageBox.Show("🚀 Start training now?", "Training", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (result == MessageBoxResult.Yes)
                    {
                        TrainingStatusText.Text = "Training started...";
                        TrainModelButton.IsEnabled = false;

                        Task.Run(() =>
                        {
                            System.Threading.Thread.Sleep(2000);
                            Dispatcher.Invoke(() =>
                            {
                                TrainingStatusText.Text = "Training complete!";
                                TrainModelButton.IsEnabled = true;
                            });
                        });
                    }
                    break;

                default:
                    MessageBox.Show($"❓ Unknown block type: {tag}");
                    break;
            }
        }

        private bool ValidateYoloDatasetStructure(string rootPath)
        {
            var result = new YoloValidationResult();
            string[] splits = { "train", "valid", "test" };
            string[] imageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".gif" };

            foreach (string split in splits)
            {
                string imagePath = System.IO.Path.Combine(rootPath, split, "images");
                string labelPath = System.IO.Path.Combine(rootPath, split, "labels");

                if (!Directory.Exists(imagePath))
                {
                    Console.WriteLine($"❌ Missing image folder for '{split}': {imagePath}");
                    result.Issues.Add($"Missing image folder for '{split}': {imagePath}");
                    return false;
                }

                if (!Directory.Exists(labelPath))
                {
                    Console.WriteLine($"❌ Missing label folder for '{split}': {labelPath}");
                    result.Issues.Add($"Missing label folder for '{split}': {labelPath}");
                    return false;
                }

                try
                {
                    var imageFiles = Directory.GetFiles(imagePath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => imageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                        .ToArray();

                    var labelFiles = Directory.GetFiles(labelPath, "*.txt", SearchOption.TopDirectoryOnly);

                    if (imageFiles.Length == 0)
                    {
                        Console.WriteLine($"⚠️ No image files found for '{split}' in: {imagePath}");
                        result.Issues.Add($"No image files found for '{split}' in: {imagePath}");
                        return false;
                    }

                    if (labelFiles.Length == 0)
                    {
                        Console.WriteLine($"⚠️ No label files found for '{split}' in: {labelPath}");
                        result.Issues.Add($"No label files found for '{split}' in: {labelPath}");
                        return false;
                    }

                    var imageNames = imageFiles
                        .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                        .ToHashSet();

                    var labelNames = labelFiles
                        .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                        .ToHashSet();

                    var missingLabels = imageNames.Except(labelNames).ToList();
                    var extraLabels = labelNames.Except(imageNames).ToList();

                    if (missingLabels.Any())
                    {
                        Console.WriteLine($"❌ Missing label files for {missingLabels.Count} image(s) in '{split}':");
                        result.Issues.AddRange(missingLabels.Select(name => $"Missing label for image: {name}"));
                        missingLabels.ForEach(name => Console.WriteLine($"   - {name}"));
                        return false;
                    }

                    if (extraLabels.Any())
                    {
                        Console.WriteLine($"❌ Extra label files without matching images in '{split}':");
                        result.Issues.AddRange(extraLabels.Select(name => $"Extra label without image: {name}"));
                        extraLabels.ForEach(name => Console.WriteLine($"   - {name}"));
                        return false;
                    }

                    if (imageNames.Count != labelNames.Count)
                    {
                        Console.WriteLine($"❌ Mismatch in file count for '{split}': {imageNames.Count} images vs {labelNames.Count} labels");
                        result.Issues.Add($"Mismatch in file count for '{split}': {imageNames.Count} images vs {labelNames.Count} labels");
                        return false;
                    }

                    Console.WriteLine($"✅ '{split}' split passed: {imageNames.Count} matched image-label pairs");
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Error while validating '{split}' split:\n{ex.Message}", "Validation Error");
                    result.Issues.Add($"Error while validating '{split}' split: {ex.Message}");
                    return false;
                }
            }

            return true;
        }

        private List<(string ImagePath, string LabelPath, bool LabelExists)> SyncYoloAnnotations(string rootPath)
        {
            string imageDir = System.IO.Path.Combine(rootPath, "train", "images");
            string labelDir = System.IO.Path.Combine(rootPath, "train", "labels");

            var imageFiles = System.IO.Directory.GetFiles(imageDir, "*.jpg");
            var result = new List<(string, string, bool)>();

            foreach (var img in imageFiles)
            {
                string labelPath = System.IO.Path.Combine(labelDir, System.IO.Path.GetFileNameWithoutExtension(img) + ".txt");
                result.Add((img, labelPath, System.IO.File.Exists(labelPath)));
            }

            return result;
        }
        private void Block_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border block && block.Tag is string tag)
            {
                HandleBlockAction(tag);
            }
        }

        #endregion

        #region Menu event handlers
        // Menu event handlers
        private void NewModel_Click(object sender, RoutedEventArgs e) => MessageBox.Show("New Model action triggered.");
        private void OpenModel_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Open Model action triggered.");
        private void SaveModel_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Save Model action triggered.");
        private void ExportModel_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Export Model action triggered.");
        private void CloseModel_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Close Model action triggered.");
        private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
        private void Undo_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Undo action triggered.");
        private void Redo_Click(object sender, RoutedEventArgs e) => MessageBox.Show("Redo action triggered.");
        private void FullScreenToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window != null)
            {
                if (window.WindowState == WindowState.Maximized && window.WindowStyle == WindowStyle.None)
                {
                    window.WindowStyle = WindowStyle.SingleBorderWindow;
                    window.WindowState = WindowState.Normal;
                }
                else
                {
                    window.WindowStyle = WindowStyle.None;
                    window.WindowState = WindowState.Maximized;
                }
            }
        }
        private void ViewHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("Help content goes here.", "Help", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show("VisionAICam\nModel Training Module\n© 2025", "About", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        #endregion

        #region Button event handlers
        private void TrainModelButton_Click(object sender, RoutedEventArgs e)
        {
            TrainingStatusText.Text = "Training started...";
            TrainModelButton.IsEnabled = false;

            Task.Run(() =>
            {
                System.Threading.Thread.Sleep(2000);
                Dispatcher.Invoke(() =>
                {
                    TrainingStatusText.Text = "Training complete!";
                    TrainModelButton.IsEnabled = true;
                });
            });
        }

        #endregion

        // Add or select dataset in ComboBox
        private void AddOrSelectDataset(string datasetPath)
        {
            if (DatasetComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == datasetPath) is ComboBoxItem existing)
            {
                DatasetComboBox.SelectedItem = existing;
            }
            else
            {
                var item = new ComboBoxItem { Content = datasetPath };
                DatasetComboBox.Items.Add(item);
                DatasetComboBox.SelectedItem = item;
            }
        }

        private void BrowseDataset_Click(object sender, RoutedEventArgs e)
        {
            var folderDialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "Select your YOLOv8 dataset folder",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = false
            };

            if (folderDialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                string datasetPath = folderDialog.SelectedPath;
                AddOrSelectDataset(datasetPath);

                // Optionally, validate the dataset structure here
                Task.Run(() =>
                {
                    bool isValid = ValidateYoloDatasetStructure(datasetPath);
                    Dispatcher.Invoke(() =>
                    {
                        if (!isValid)
                        {
                            MessageBox.Show(
                                "⚠️ Invalid dataset structure.\nExpected folders: train/valid/test with images/labels subfolders and a data.yaml file.",
                                "Validation Failed",
                                MessageBoxButton.OK,
                                MessageBoxImage.Warning
                            );
                            TrainingStatusText.Text = "Dataset validation failed. Please select a valid dataset.";
                        }
                        else
                        {
                            MessageBox.Show(
                                $"✅ Dataset loaded successfully:\n{datasetPath}",
                                "Dataset Validated",
                                MessageBoxButton.OK,
                                MessageBoxImage.Information
                            );
                            // Draw dataset block if not already present
                            if (datasetBlock == null)
                            {
                                datasetBlock = CreateBlock("📁 Dataset", 50, 50, "Dataset");
                                TrainingCanvas.Children.Add(datasetBlock);
                                TrainingStatusText.Text = "Dataset added. Now select a model.";
                            }
                        }
                    });
                });
            }
        }
    }
}
