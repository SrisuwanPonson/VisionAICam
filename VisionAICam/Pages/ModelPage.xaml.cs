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
using System.Windows.Data;

namespace VisionAICam.Pages
{
    public class ModelOptionTemplateSelector : DataTemplateSelector
    {
        private readonly Dictionary<string, string[]> _optionSources;

        public ModelOptionTemplateSelector(Dictionary<string, string[]> optionSources)
        {
            _optionSources = optionSources;
        }

        public override DataTemplate SelectTemplate(object item, DependencyObject container)
        {
            var option = item as TrainingOption;
            if (option != null && _optionSources.TryGetValue(option.Name, out var choices))
            {
                var comboTemplate = new DataTemplate();
                var factory = new FrameworkElementFactory(typeof(ComboBox));
                factory.SetBinding(ComboBox.SelectedItemProperty, new Binding("Value"));
                factory.SetValue(ComboBox.ItemsSourceProperty, choices);
                comboTemplate.VisualTree = factory;
                return comboTemplate;
            }

            var textTemplate = new DataTemplate();
            var textFactory = new FrameworkElementFactory(typeof(TextBlock));
            textFactory.SetBinding(TextBlock.TextProperty, new Binding("Value"));
            textTemplate.VisualTree = textFactory;
            return textTemplate;
        }
    }
    public class TrainingOption
    {
        public string Name { get; set; }
        public string Value { get; set; }
    }

    public partial class ModelPage : Page
    {
        private UIElement selectedBlock;
        private Point dragStartPoint;
        private List<Line> connectionLines = new();
        private bool isDragging;
        private const double DragThreshold = 4; // pixels

        private Border datasetBlock;
        private Border modelBlock;
        private Border trainBlock;

        public ModelPage()
        {
            InitializeComponent();
            TrainingStatusText.Text = "Please select a dataset to begin.";
        }



        #region Block

        private Border CreateBlock(UIElement content, double left, double top, string tag)
        {
            var block = new Border
            {
                Width = 320,
                Height = 300,
                Background = new SolidColorBrush(Color.FromRgb(58, 58, 61)),
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(8),
                Tag = tag,
                Child = content
            };

            Canvas.SetLeft(block, left);
            Canvas.SetTop(block, top);

            block.MouseLeftButtonDown += Block_MouseLeftButtonDown;
            block.MouseMove += Block_MouseMove;
            block.MouseLeftButtonUp += Block_MouseLeftButtonUp;

            return block;
        }

        private UIElement CreateModelDetailGrid(List<TrainingOption> modelOptions, string blockLabel, Dictionary<string, string[]> optionSources)
        {
            var modelGrid = new DataGrid
            {
                ItemsSource = modelOptions,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Margin = new Thickness(0, 0, 0, 8),
                RowHeight = 28
            };

            // Static column for option name
            modelGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Model Option",
                Binding = new Binding("Name"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });

            // Dynamic column for value (ComboBox or TextBlock)
            modelGrid.Columns.Add(new DataGridTemplateColumn
            {
                Header = "Value",
                Width = new DataGridLength(1, DataGridLengthUnitType.Star),
                CellTemplateSelector = new ModelOptionTemplateSelector(optionSources)
            });

            // Layout panel
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = blockLabel,
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(new TextBlock
            {
                Text = "Model Options",
                Foreground = Brushes.LightGray,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
                FontSize = 14
            });
            panel.Children.Add(modelGrid);

            return panel;
        }


        // DO NOT MODIFY THIS METHOD (per your request)
        private UIElement CreateDatasetDetailsGrid(string datasetPath)
        {
            var datasetOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Path", Value = datasetPath }
    };

            // Try to read data.yaml for format, classes, etc.
            string yamlPath = System.IO.Path.Combine(datasetPath, "data.yaml");
            if (File.Exists(yamlPath))
            {
                datasetOptions.Add(new TrainingOption { Name = "Format", Value = "YOLO" });
                try
                {
                    // Simple YAML parsing for 'names' and 'nc'
                    var lines = File.ReadAllLines(yamlPath);
                    var namesLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("names:"));
                    var ncLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("nc:"));
                    if (ncLine != null)
                    {
                        var nc = ncLine.Split(':')[1].Trim();
                        datasetOptions.Add(new TrainingOption { Name = "Num Classes", Value = nc });
                    }
                    if (namesLine != null)
                    {
                        var names = namesLine.Substring(namesLine.IndexOf('[')).Trim();
                        datasetOptions.Add(new TrainingOption { Name = "Class Names", Value = names });
                    }
                }
                catch
                {
                    datasetOptions.Add(new TrainingOption { Name = "YAML Parse", Value = "Failed" });
                }
            }
            else
            {
                datasetOptions.Add(new TrainingOption { Name = "Format", Value = "Unknown" });
                datasetOptions.Add(new TrainingOption { Name = "Num Classes", Value = "?" });
                datasetOptions.Add(new TrainingOption { Name = "Class Names", Value = "?" });
            }

            // Split statistics
            var splits = new[] { "train", "valid", "test" };
            var imageExts = new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".gif" };
            var datasetDetails = new List<TrainingOption>();
            foreach (var split in splits)
            {
                string imgDir = System.IO.Path.Combine(datasetPath, split, "images");
                string lblDir = System.IO.Path.Combine(datasetPath, split, "labels");

                int imgCount = Directory.Exists(imgDir)
                    ? Directory.GetFiles(imgDir).Count(f => imageExts.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                    : 0;
                int lblCount = Directory.Exists(lblDir)
                    ? Directory.GetFiles(lblDir, "*.txt").Length
                    : 0;

                datasetDetails.Add(new TrainingOption { Name = $"{split} images", Value = imgCount.ToString() });
                datasetDetails.Add(new TrainingOption { Name = $"{split} labels", Value = lblCount.ToString() });
            }

            // Dataset options DataGrid (no style assignment)
            var optionsGrid = new DataGrid
            {
                ItemsSource = datasetOptions,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Margin = new Thickness(0, 0, 0, 8)
            };
            optionsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Option",
                Binding = new System.Windows.Data.Binding("Name"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            optionsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Value",
                Binding = new System.Windows.Data.Binding("Value"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });

            // Split statistics DataGrid (no style assignment)
            var statsGrid = new DataGrid
            {
                ItemsSource = datasetDetails,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Margin = new Thickness(0, 0, 0, 0)
            };
            statsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Split/Type",
                Binding = new System.Windows.Data.Binding("Name"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });
            statsGrid.Columns.Add(new DataGridTextColumn
            {
                Header = "Count",
                Binding = new System.Windows.Data.Binding("Value"),
                IsReadOnly = true,
                Width = new DataGridLength(1, DataGridLengthUnitType.Star)
            });

            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = "📂 Dataset Details",
                Foreground = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 16,
                Margin = new Thickness(0, 0, 0, 8),
                HorizontalAlignment = HorizontalAlignment.Center
            });
            panel.Children.Add(optionsGrid);
            panel.Children.Add(statsGrid);

            return panel;
        }


        private void Block_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            selectedBlock = sender as UIElement;
            dragStartPoint = e.GetPosition(TrainingCanvas);
            isDragging = false;
            selectedBlock.CaptureMouse();
        }

        private void Block_MouseMove(object sender, MouseEventArgs e)
        {
            if (selectedBlock != null && selectedBlock.IsMouseCaptured)
            {
                Point currentPoint = e.GetPosition(TrainingCanvas);
                double offsetX = currentPoint.X - dragStartPoint.X;
                double offsetY = currentPoint.Y - dragStartPoint.Y;

                if (!isDragging && (Math.Abs(offsetX) > DragThreshold || Math.Abs(offsetY) > DragThreshold))
                {
                    isDragging = true;
                }

                if (isDragging)
                {
                    Canvas.SetLeft(selectedBlock, Canvas.GetLeft(selectedBlock) + offsetX);
                    Canvas.SetTop(selectedBlock, Canvas.GetTop(selectedBlock) + offsetY);
                    dragStartPoint = currentPoint;
                }
            }
        }

        private void Block_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (selectedBlock != null)
            {
                selectedBlock.ReleaseMouseCapture();

                if (!isDragging)
                {
                    if (sender is Border block && block.Tag is string tag)
                    {
                        HandleBlockAction(tag);
                    }
                }

                selectedBlock = null;
                isDragging = false;
            }
        }

        private string selectedModelType = "YOLOv8"; // Default

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

                                    if (datasetBlock == null)
                                    {
                                        var detailsGrid = CreateDatasetDetailsGrid(datasetPath);
                                        datasetBlock = CreateBlock(detailsGrid, 50, 50, "Dataset");
                                        TrainingCanvas.Children.Add(datasetBlock);
                                        TrainingStatusText.Text = "Dataset added. Now select a model.";
                                    }
                                });
                            });
                        }
                        break;
                    }

                case "Model":
                    {
                        // Define selectable options for each model parameter
                        var optionSources = new Dictionary<string, string[]>
    {
        { "Architecture", new[] { "YOLOv5", "YOLOv8", "ONNX", "Custom" } },
        { "Input Size", new[] { "320", "416", "512", "640", "768" } },
        { "Backbone", new[] { "CSPDarknet", "ResNet", "MobileNet", "EfficientNet" } },
        { "Pretrained Weights", new[] { "COCO", "ImageNet", "None" } }
    };

                        // Default selections
                        string selectedModel = optionSources["Architecture"][1]; // YOLOv8
                        string blockLabel = $"🧠 {selectedModel}";

                        var modelOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Architecture", Value = selectedModel },
        new TrainingOption { Name = "Input Size", Value = "640" },
        new TrainingOption { Name = "Backbone", Value = "CSPDarknet" },
        new TrainingOption { Name = "Pretrained Weights", Value = "COCO" }
    };

                        // Remove previous model block if present
                        if (modelBlock != null)
                        {
                            TrainingCanvas.Children.Remove(modelBlock);
                            modelBlock = null;
                        }

                        // Positioning logic
                        double datasetBlockX = 50;
                        double datasetBlockWidth = 320;
                        double gap = 30;
                        double modelBlockX = datasetBlockX + datasetBlockWidth + gap;
                        double modelBlockY = 50;

                        // Create and add the model block
                        var panel = CreateModelDetailGrid(modelOptions, blockLabel, optionSources);
                        modelBlock = CreateBlock(panel, modelBlockX, modelBlockY, "Model");
                        TrainingCanvas.Children.Add(modelBlock);

                        TrainingStatusText.Text = "Model added. Edit model options in the block.";
                        break;
                    }


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
            string[] splits = { "train", "valid", "test" };
            string[] imageExtensions = { ".jpg", ".jpeg", ".png", ".bmp", ".webp", ".tiff", ".gif" };

            foreach (string split in splits)
            {
                string imagePath = System.IO.Path.Combine(rootPath, split, "images");
                string labelPath = System.IO.Path.Combine(rootPath, split, "labels");

                if (!Directory.Exists(imagePath) || !Directory.Exists(labelPath))
                    return false;

                try
                {
                    var imageFiles = Directory.GetFiles(imagePath, "*.*", SearchOption.TopDirectoryOnly)
                        .Where(f => imageExtensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                        .ToArray();

                    var labelFiles = Directory.GetFiles(labelPath, "*.txt", SearchOption.TopDirectoryOnly);

                    if (imageFiles.Length == 0 || labelFiles.Length == 0)
                        return false;

                    var imageNames = imageFiles
                        .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                        .ToHashSet();

                    var labelNames = labelFiles
                        .Select(f => System.IO.Path.GetFileNameWithoutExtension(f))
                        .ToHashSet();

                    if (imageNames.Except(labelNames).Any() || labelNames.Except(imageNames).Any())
                        return false;

                    if (imageNames.Count != labelNames.Count)
                        return false;
                }
                catch
                {
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

                            // Remove previous dataset block if it exists
                            if (datasetBlock != null)
                            {
                                TrainingCanvas.Children.Remove(datasetBlock);
                                datasetBlock = null;
                            }

                            var detailsPanel = CreateDatasetDetailsGrid(datasetPath);
                            datasetBlock = CreateBlock(detailsPanel, 50, 50, "Dataset");
                            TrainingCanvas.Children.Add(datasetBlock);
                        }
                    });
                });
            }
        }

        private void AddModel_Click(object sender, RoutedEventArgs e)
        {
            // Define selectable options for each model parameter
            var optionSources = new Dictionary<string, string[]>
    {
        { "Architecture", new[] { "YOLOv5", "YOLOv8", "ONNX", "Custom" } },
        { "Input Size", new[] { "320", "416", "512", "640", "768" } },
        { "Backbone", new[] { "CSPDarknet", "ResNet", "MobileNet", "EfficientNet" } },
        { "Pretrained Weights", new[] { "COCO", "ImageNet", "None" } }
    };

            // Default selections
            string selectedModel = (ModelArchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "YOLOv8";
            string blockLabel = $"🧠 {selectedModel}";

            var modelOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Architecture", Value = selectedModel },
        new TrainingOption { Name = "Input Size", Value = "640" },
        new TrainingOption { Name = "Backbone", Value = "CSPDarknet" },
        new TrainingOption { Name = "Pretrained Weights", Value = "COCO" }
    };

            // Remove previous model block if present
            if (modelBlock != null)
            {
                TrainingCanvas.Children.Remove(modelBlock);
                modelBlock = null;
            }

            // Positioning logic
            double datasetBlockX = 50;
            double datasetBlockWidth = 320;
            double gap = 30;
            double modelBlockX = datasetBlockX + datasetBlockWidth + gap;
            double modelBlockY = 50;

            // Create and add the model block
            var panel = CreateModelDetailGrid(modelOptions, blockLabel, optionSources);
            modelBlock = CreateBlock(panel, modelBlockX, modelBlockY, "Model");
            TrainingCanvas.Children.Add(modelBlock);

            TrainingStatusText.Text = "Model added. Edit model options in the block.";
        }

        private void AddTrainingOption_Click(object sender, RoutedEventArgs e)
        {
            // Define selectable options for each training parameter
            var trainingOptionSources = new Dictionary<string, string[]>
    {
        { "Epochs", new[] { "10", "20", "50", "100", "200" } },
        { "Batch Size", new[] { "8", "16", "32", "64" } },
        { "Learning Rate", new[] { "0.001", "0.005", "0.01", "0.05" } },
        { "Optimizer", new[] { "Adam", "SGD", "RMSprop" } },
        { "Scheduler", new[] { "None", "StepLR", "CosineAnnealing" } }
    };

            // Initialize with default values (first value in each array)
            var trainingOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Epochs", Value = trainingOptionSources["Epochs"][0] },
        new TrainingOption { Name = "Batch Size", Value = trainingOptionSources["Batch Size"][0] },
        new TrainingOption { Name = "Learning Rate", Value = trainingOptionSources["Learning Rate"][0] },
        new TrainingOption { Name = "Optimizer", Value = trainingOptionSources["Optimizer"][0] },
        new TrainingOption { Name = "Scheduler", Value = trainingOptionSources["Scheduler"][0] }
    };

            // Remove previous training block if present
            if (trainBlock != null)
            {
                TrainingCanvas.Children.Remove(trainBlock);
                trainBlock = null;
            }

            // Positioning logic (below model block)
            double trainBlockX = 50;
            double trainBlockY = 400;

            var panel = CreateModelDetailGrid(trainingOptions, "🚀 Training Options", trainingOptionSources);
            trainBlock = CreateBlock(panel, trainBlockX, trainBlockY, "Train");
            TrainingCanvas.Children.Add(trainBlock);

            TrainingStatusText.Text = "Training options added. Edit training options in the block.";
        }


    }
}
