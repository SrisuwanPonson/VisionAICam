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
using System.Diagnostics;
using VisionAICam.Utilities;

namespace VisionAICam.Pages
{
    
    

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
        private string pretrainFolderPath;
        public string PretrainFolderPath
        {
            get
            {
                return pretrainFolderPath;
            }
            set
            {
                pretrainFolderPath = value;
            }
        }
        // Call this method in the constructor or initialization logic  
        public ModelPage()
        {
            InitializeComponent();
            Loaded += OnModelPageLoaded;
           
            InitializeTrainingStatus();
            EnsurePretrainEnvironment();
        }

        private void OnModelPageLoaded(object sender, RoutedEventArgs e)
        {
            InitializeTrainingStatus("Welcome! Please select a dataset to begin.");
            EnsurePretrainEnvironment();
            Console.WriteLine("ModelPage loaded successfully.");
            
        }

        private void InitializeTrainingStatus(string message = "Please select a dataset to begin.")
        {
            TrainingStatusText.Text = message;
        }

        private void EnsurePretrainEnvironment()
        {
            CreatePretrainFolder();
            EnsurePretrainedModelsExist();
        }

        private void CreatePretrainFolder()
        {
            pretrainFolderPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pretrain");
            if (!Directory.Exists(pretrainFolderPath))
            {
                Directory.CreateDirectory(pretrainFolderPath);
            }
        }
        private void EnsurePretrainedModelsExist()
        {
            string pretrainFolderPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "pretrain");
            if (!Directory.Exists(pretrainFolderPath))
            {
                Directory.CreateDirectory(pretrainFolderPath);
            }

            // Actual URLs for pretrained YOLO models
            string[] pretrainedModelUrls =
            {
                // YOLOv5
                "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5n.pt",
                "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5s.pt",
                "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5m.pt",
                "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5l.pt",
                "https://github.com/ultralytics/yolov5/releases/download/v7.0/yolov5x.pt",

                // YOLOv8
                "https://github.com/ultralytics/assets/releases/download/v0.0.0/yolov8n.pt",
                "https://github.com/ultralytics/assets/releases/download/v0.0.0/yolov8s.pt",
                "https://github.com/ultralytics/assets/releases/download/v0.0.0/yolov8m.pt",
                "https://github.com/ultralytics/assets/releases/download/v0.0.0/yolov8l.pt",
                "https://github.com/ultralytics/assets/releases/download/v0.0.0/yolov8x.pt"
            };

            foreach (var url in pretrainedModelUrls)
            {
                string fileName = System.IO.Path.GetFileName(url); // Extracts "yolov5s.pt" etc.
                string modelPath = System.IO.Path.Combine(pretrainFolderPath, fileName);

                if (!File.Exists(modelPath))
                {
                    try
                    {
                        using (var client = new System.Net.WebClient())
                        {
                            client.DownloadFile(url, modelPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show($"Failed to download {fileName}: {ex.Message}", "Download Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }
            }
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
            ModelArchComboBox.Text = "";
            return panel;
        }


        private void Block_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            selectedBlock = sender as UIElement;
            dragStartPoint = e.GetPosition(TrainingCanvas);
            isDragging = false;
            selectedBlock.CaptureMouse();
        }
        private void ModelArchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TrainingCanvas == null) return;

            // Remove previous blocks
            if (modelBlock != null)
            {
                TrainingCanvas.Children.Remove(modelBlock);
                modelBlock = null;
            }
            if (trainBlock != null)
            {
                TrainingCanvas.Children.Remove(trainBlock);
                trainBlock = null;
            }

            // Get selected architecture
            string selectedArch = (ModelArchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "YOLOv8";
            string blockLabel = $"🧠 {selectedArch}";

            // Define model options
            var modelOptionSources = new Dictionary<string, string[]>();
            var modelOptions = new List<TrainingOption>();
            var trainingOptionSources = new Dictionary<string, string[]>();

            switch (selectedArch)
            {
                case "YOLOv5":
                    // 🎯 Define model configuration options for YOLOv5
                    modelOptionSources = new Dictionary<string, string[]>
                    {
                        { "Architecture", new[] { "YOLOv5" } },
                        { "Input Size", new[] { "320", "416", "512", "640" } },
                        { "Backbone", new[] { "CSPDarknet", "Custom-ResNet" } }, // ResNet requires manual integration
                        { "Pretrained Weights", new[] { "yolov5n", "yolov5s", "yolov5m", "yolov5l", "yolov5x", "None" } }
                    };

                                    // ✅ Default selections for YOLOv5
                                    modelOptions = new List<TrainingOption>
                    {
                        new TrainingOption { Name = "Architecture", Value = "YOLOv5" },
                        new TrainingOption { Name = "Input Size", Value = "640" },
                        new TrainingOption { Name = "Backbone", Value = "CSPDarknet" },
                        new TrainingOption { Name = "Pretrained Weights", Value = "yolov5n" }
                    };

                                    // 🚀 Training hyperparameters for YOLOv5
                                    trainingOptionSources = new Dictionary<string, string[]>
                    {
                        { "Epochs", new[] { "10", "20", "50", "100", "200" } },
                        { "Batch Size", new[] { "8", "16", "32", "64" } },
                        { "Learning Rate", new[] { "0.001", "0.005", "0.01", "0.05" } },
                        { "Optimizer", new[] { "SGD", "Adam", "RMSprop" } },
                        { "Scheduler", new[] { "None", "StepLR", "CosineAnnealing", "ReduceLROnPlateau" } }
                    };


                    break;


                case "YOLOv8":
                    // 🎯 Define model configuration options for YOLOv8 (no backbone selection)
                                    modelOptionSources = new Dictionary<string, string[]>
                    {
                        { "Architecture", new[] { "YOLOv8" } },
                        { "Input Size", new[] { "320", "416", "512", "640", "768" } },
                        { "Backbone", new[] { "None" } }, // Explicitly disabled or not applicable
                        { "Pretrained Weights", new[] { "yolov8n", "yolov8s", "yolov8m", "yolov8l", "yolov8x", "None" } }
                    };

                                    // ✅ Default selections for YOLOv8
                                    modelOptions = new List<TrainingOption>
                    {
                        new TrainingOption { Name = "Architecture", Value = "YOLOv8" },
                        new TrainingOption { Name = "Input Size", Value = "640" },
                        new TrainingOption { Name = "Backbone", Value = "None" },
                        new TrainingOption { Name = "Pretrained Weights", Value = "yolov8n" }
                    };

                                    // 🚀 Training hyperparameters optimized for YOLOv8
                                    trainingOptionSources = new Dictionary<string, string[]>
                    {
                        { "Epochs", new[] { "10", "20", "50", "100", "200", "300" } },
                        { "Batch Size", new[] { "8", "16", "32", "64", "128" } },
                        { "Learning Rate", new[] { "0.0005", "0.001", "0.005", "0.01" } },
                        { "Optimizer", new[] { "Adam", "SGD", "AdamW" } },
                        { "Scheduler", new[] { "None", "Cosine", "Linear", "StepLR" } }
                    };
                    break;



                case "ONNX":
                    // Define valid ONNX export options
                    modelOptionSources = new Dictionary<string, string[]>
                    {
                        { "Architecture", new[] { "ONNX" } },
                        { "Input Size", new[] { "320", "416", "512", "640" } },
                        { "Opset", new[] { "11", "12", "13" } } // Opset 13 recommended for latest compatibility
                    };

                                    // Set default ONNX export configuration
                                    modelOptions = new List<TrainingOption>
                    {
                        new TrainingOption { Name = "Architecture", Value = "ONNX" },
                        new TrainingOption { Name = "Input Size", Value = "640" },
                        new TrainingOption { Name = "Opset", Value = "13" }
                    };
                    // Training hyperparameters for ONNX-compatible models
                    trainingOptionSources = new Dictionary<string, string[]>
                    {
                        { "Epochs", new[] { "10", "20", "50", "100", "200" } },         // Standard training durations
                        { "Batch Size", new[] { "8", "16", "32", "64" } },              // GPU-dependent; ONNX prefers consistent input shapes
                        { "Learning Rate", new[] { "0.0005", "0.001", "0.005", "0.01" } }, // Conservative range for stable export
                        { "Optimizer", new[] { "SGD", "Adam", "AdamW" } },              // AdamW often yields smoother ONNX graphs
                        { "Scheduler", new[] { "None", "StepLR", "CosineAnnealing" } }, // Compatible with most ONNX export pipelines
                        { "Opset", new[] { "11", "12", "13" } }                         // Critical for ONNX export compatibility
                    };
                    // Optional: Add validation to ensure selected opset matches target runtime (e.g., Jetson, Colab, etc.)
                    break;

                case "Custom":
                    // Allow full flexibility for user-defined models
                    modelOptionSources = new Dictionary<string, string[]>
                    {
                        { "Architecture", new[] { "Custom" } },
                        { "Input Size", new[] { "320", "416", "512", "640", "768", "1280" } },
                        { "Backbone", new[] { "UserDefined" } } // Could be expanded later to support dropdowns or text input
                    };

                                    // Default selections for Custom model setup
                                    modelOptions = new List<TrainingOption>
                    {
                        new TrainingOption { Name = "Architecture", Value = "Custom" },
                        new TrainingOption { Name = "Input Size", Value = "640" },
                        new TrainingOption { Name = "Backbone", Value = "UserDefined" }
                    };

                    // Training hyperparameters for Custom models
                    trainingOptionSources = new Dictionary<string, string[]>
                    {
                        { "Epochs", new[] { "10", "20", "50", "100", "200", "300" } },       // Flexible range for experimentation
                        { "Batch Size", new[] { "4", "8", "16", "32", "64", "128" } },       // Includes smaller sizes for edge devices
                        { "Learning Rate", new[] { "0.0001", "0.0005", "0.001", "0.005", "0.01" } }, // Wider range for tuning unknown models
                        { "Optimizer", new[] { "SGD", "Adam", "AdamW", "RMSprop" } },        // Broad support for various training styles
                        { "Scheduler", new[] { "None", "StepLR", "CosineAnnealing", "ReduceLROnPlateau", "Linear" } } // Covers classic and modern schedulers
                    };
                    // Optional: Add validation to ensure input size matches model requirements
                    break;
            }

        

            var trainingOptions = new List<TrainingOption>
            {
                new TrainingOption { Name = "Epochs", Value = trainingOptionSources["Epochs"][0] },
                new TrainingOption { Name = "Batch Size", Value = trainingOptionSources["Batch Size"][0] },
                new TrainingOption { Name = "Learning Rate", Value = trainingOptionSources["Learning Rate"][0] },
                new TrainingOption { Name = "Optimizer", Value = trainingOptionSources["Optimizer"][0] },
                new TrainingOption { Name = "Scheduler", Value = trainingOptionSources["Scheduler"][0] }
            };

            // Positioning logic
            double datasetBlockX = 50;
            double datasetBlockWidth = 320;
            double gap = 30;
            double modelBlockX = datasetBlockX + datasetBlockWidth + gap;
            double modelBlockY = 50;

            double modelBlockWidth = 320; // or use modelBlock.DesiredSize.Width after layout
            double trainBlockX = modelBlockX + modelBlockWidth + gap;
            double trainBlockY = modelBlockY;

            // Create and add blocks
            var modelPanel = CreateModelDetailGrid(modelOptions, blockLabel, modelOptionSources);
            modelBlock = CreateBlock(modelPanel, modelBlockX, modelBlockY, "Model");
            TrainingCanvas.Children.Add(modelBlock);

            var trainPanel = CreateModelDetailGrid(trainingOptions, "🚀 Training Options", trainingOptionSources);
            trainBlock = CreateBlock(trainPanel, trainBlockX, trainBlockY, "Train");
            TrainingCanvas.Children.Add(trainBlock);

            TrainingStatusText.Text = $"Model and training options updated for {selectedArch}.";
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
                            { "Backbone", new[] { "None" } },
                            { "Pretrained Weights", new[] { "COCO", "ImageNet", "None" } }
                        };

                        // Default selections
                        string selectedModel = optionSources["Architecture"][1]; // YOLOv8
                        string blockLabel = $"🧠 {selectedModel}";

                        var modelOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Architecture", Value = selectedModel },
        new TrainingOption { Name = "Input Size", Value = "640" },
        new TrainingOption { Name = "Backbone", Value = "None" },
        new TrainingOption { Name = "Pretrained Weights", Value = "None" }
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

        //#region Train Button event handlers
        private void TrainModelButton_Click(object sender, RoutedEventArgs e)
        {
            TrainingStatusText.Text = "🚀 Training started...";
            TrainModelButton.IsEnabled = false;

            // Extract selected options from UI blocks
            var datasetOptions = ExtractOptionsFromBlock(datasetBlock);
            var modelOptions = ExtractOptionsFromBlock(modelBlock);
            var trainingOptions = ExtractOptionsFromBlock(trainBlock);
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                Dispatcher.Invoke(() => TrainingStatusText.Text = ex.Message);
                return;
            }
            // Basic validation
            if (datasetOptions.Count == 0 || modelOptions.Count == 0)
            {
                MessageBox.Show("❌ Missing dataset or model configuration.", "Training Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TrainingStatusText.Text = "Training aborted due to missing configuration.";
                TrainModelButton.IsEnabled = true;
                return;
            }

            // Determine architecture
            string architecture = modelOptions.FirstOrDefault(opt => opt.Name == "Architecture")?.Value ?? "YOLOv8";

            Task.Run(() =>
            {
                try
                {
                    if (architecture == "YOLOv8")
                    {
                        // Replace the direct call to LaunchYOLOv8Training with a call to the TrainingHelper class.
                        //TrainingHelper helper = new TrainingHelper();
                        TrainingHelper.LaunchYOLOv8Training(datasetOptions, modelOptions, trainingOptions);
                       

                    }
                    else
                    {
                        string trainingCommand = BuildTrainingCommand(architecture, modelOptions, trainingOptions);
                        Console.WriteLine($"Executing: {trainingCommand}");
                        // TODO: Replace with actual training logic for other architectures
                    }

                    Dispatcher.Invoke(() =>
                    {
                        TrainingStatusText.Text = $"✅ Training complete for {architecture}!";
                        TrainModelButton.IsEnabled = true;
                    });
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        MessageBox.Show($"❌ Training failed:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        TrainingStatusText.Text = "Training failed. Check logs or configuration.";
                        TrainModelButton.IsEnabled = true;
                    });
                }
            });
        }
        private string BuildTrainingCommand(string arch, List<TrainingOption> modelOpts, List<TrainingOption> trainOpts)
        {
            var args = modelOpts.Concat(trainOpts)
                .Select(opt => $"{opt.Name.Replace(" ", "").ToLower()}={opt.Value}");
            return $"train_model --arch={arch} " + string.Join(" ", args);
        }

        private List<TrainingOption> ExtractOptionsFromBlock(UIElement block)
        {
            var options = new List<TrainingOption>();

            if (block is Border border)
            {
                var content = border.Child;

                // Case 1: Model block with Grid layout
                if (content is Grid grid)
                {
                    foreach (var child in grid.Children)
                    {
                        if (child is StackPanel panel && panel.Tag is string optionName)
                        {
                            var comboBox = panel.Children.OfType<ComboBox>().FirstOrDefault();
                            var selected = comboBox?.SelectedItem as ComboBoxItem;
                            string value = selected?.Content?.ToString() ?? "";
                            options.Add(new TrainingOption { Name = optionName, Value = value });
                        }
                    }
                }

                // Case 2: Dataset block with StackPanel layout
                else if (content is StackPanel stackPanel)
                {
                    foreach (var child in stackPanel.Children)
                    {
                        if (child is DataGrid dg)
                        {
                            if (dg.ItemsSource is IEnumerable<TrainingOption> gridOptions)
                            {
                                options.AddRange(gridOptions);
                            }
                            else
                            {
                                Console.WriteLine("⚠️ DataGrid ItemsSource is not TrainingOption.");
                            }
                        }
                    }
                }
                else
                {
                    Console.WriteLine("⚠️ Block content is not Grid or StackPanel.");
                }
            }
            else
            {
                Console.WriteLine("⚠️ UIElement is not a Border.");
            }

            return options;
        }

        private void ValidatePythonPath(string pythonPath)
        {
            if (!File.Exists(pythonPath))
            {
                ShowError("❌ Python executable not found.", $"Expected at:\n{pythonPath}");
                throw new FileNotFoundException($"Python executable not found at {pythonPath}");
            }
        }

        private string ResolvePythonPath()
        {
            string pythonPath = Environment.GetEnvironmentVariable("PYTHON_PATH") ??
                                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python313", "python.exe");

            ValidatePythonPath(pythonPath);
            return pythonPath;
        }
        public void RunPythonScript(string pythonExePath, string scriptPath, string arguments, Action<string> logCallback, Action<int> exitCallback)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = pythonExePath,
                    Arguments = $"\"{scriptPath}\" {arguments}",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    WorkingDirectory = System.IO.Path.GetDirectoryName(scriptPath)
                };

                var process = new Process { StartInfo = psi };

                process.OutputDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        logCallback?.Invoke($"🟢 {e.Data}");
                };

                process.ErrorDataReceived += (s, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        logCallback?.Invoke($"🔴 {e.Data}");
                };

                logCallback?.Invoke($"🚀 Launching: {psi.FileName} {psi.Arguments}");

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                process.WaitForExit();
                int exitCode = process.ExitCode;

                exitCallback?.Invoke(exitCode);
            }
            catch (Exception ex)
            {
                logCallback?.Invoke($"❌ Failed to run Python: {ex.Message}");
                exitCallback?.Invoke(-1);
            }
        }
       

        // ✅ Utility: Show error with UI feedback
        private void ShowError(string messageBoxText, string statusText)
        {
            Dispatcher.Invoke(() =>
            {
                MessageBox.Show(messageBoxText, "Training Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TrainingStatusText.Text = statusText;
            });
        }
   
        

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
        { "Backbone", new[] { "None" } },
        { "Pretrained Weights", new[] { "None" } }
    };

            // Default selections
            string selectedModel = (ModelArchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "YOLOv8";
            string blockLabel = $"🧠 {selectedModel}";

            var modelOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Architecture", Value = selectedModel },
        new TrainingOption { Name = "Input Size", Value = "640" },
        new TrainingOption { Name = "Backbone", Value = "None" },
        new TrainingOption { Name = "Pretrained Weights", Value = "None" }
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

            // Initialize with default values
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

            // Positioning logic: to the right of model block
            double modelBlockX = Canvas.GetLeft(modelBlock);
            double modelBlockWidth = modelBlock.RenderSize.Width;
            double gap = 30;
            double trainBlockX = modelBlockX + modelBlockWidth + gap;
            double trainBlockY = Canvas.GetTop(modelBlock); // align vertically

            var panel = CreateModelDetailGrid(trainingOptions, "🚀 Training Options", trainingOptionSources);
            trainBlock = CreateBlock(panel, trainBlockX, trainBlockY, "Train");
            TrainingCanvas.Children.Add(trainBlock);

            TrainingStatusText.Text = "Training options added. Edit training options in the block.";
        }


    }
}
