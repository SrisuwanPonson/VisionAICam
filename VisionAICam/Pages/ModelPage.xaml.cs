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
using System.Windows.Media.Animation;

namespace VisionAICam.Pages
{

    public class BlockState
    {
        public string BlockType { get; set; } = ""; // e.g., "Dataset", "Model", "Train"
        public double X { get; set; }
        public double Y { get; set; }
        public List<TrainingOption> Options { get; set; } = new();
    }
    public static class ModelProjectSession
    {
        public class Project
        {
            // Add properties as needed  
            public string Name { get; set; }
            public string Description { get; set; }
            public DateTime CreatedDate { get; set; }
            public DateTime ModifiedDate { get; set; }
            public string DatasetPath { get; set; }
            public string ModelArchitecture { get; set; }
            public string TrainingMode { get; set; }
            public Dictionary<string, string> AdditionalOptions { get; set; }
            public List<BlockState> Blocks { get; set; } = new();
           
            public Project()
            {
                Name = string.Empty;
                Description = string.Empty;
                CreatedDate = DateTime.Now;
                ModifiedDate = DateTime.Now;
                DatasetPath = string.Empty;
                ModelArchitecture = string.Empty;
                TrainingMode = string.Empty;
                AdditionalOptions = new Dictionary<string, string>();
            }
        }
        public static Project? CurrentProject { get; set; }
    }
    public partial class ModelPage : Page
    {
        private UIElement? selectedBlock;
        private Point dragStartPoint;
        private List<Line> connectionLines = new();
        private bool isDragging;
        private const double DragThreshold = 4; // pixels

        private Border datasetBlock;
        // Update the declarations of `modelBlock` and `trainBlock` to make them nullable.  
        private Border? modelBlock;
        private Border? trainBlock;
        private string pretrainFolderPath;
        private int modelBlockAddCount = 0;

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

        private string[] architectures = new[] { "YOLOv5n", "YOLOv5s", "YOLOv5m", "YOLOv5l", "YOLOv5x" ,
                                                       "YOLOv8n", "YOLOv8s", "YOLOv8m", "YOLOv8l", "YOLOv8x",
                                                       "Faster R-CNN", "SSD", "RetinaNet", "EfficientDet", "CenterNet",
                                                       "YOLOv4", "YOLOv3", "YOLOv7", "DETR", "Cascade R-CNN"};

        public string[] Architectures
        {
            get { return architectures; }
            set { architectures = value; }
        }

        private void SaveBlockStates()
        {
            
         
            var blocks = new List<BlockState>();
            if (TrainingCanvas == null)
                return;
            foreach (UIElement child in TrainingCanvas.Children)
            {
                if (child is Border border && border.Tag is string tag)
                {
                    var options = ExtractOptionsFromBlock(border);
                    blocks.Add(new BlockState
                    {
                        BlockType = tag,
                        X = Canvas.GetLeft(border),
                        Y = Canvas.GetTop(border),
                        Options = options
                    });
                }
            }
            if (ModelProjectSession.CurrentProject != null)
                ModelProjectSession.CurrentProject.Blocks = blocks;
        }
        private void RestoreBlockStates()
        {
            if (ProjectSession.CurrentProject == null)
                return;
            var project = ModelProjectSession.CurrentProject;
            if (project?.Blocks == null)
                return;

            // Clear current blocks from the canvas
            TrainingCanvas.Children.Clear();
            modelBlock = null;
            trainBlock = null;
            datasetBlock = null;

            foreach (var blockState in project.Blocks)
            {
                UIElement? panel = null;

                if (blockState.BlockType == "Dataset")
                {
                    var path = blockState.Options?.FirstOrDefault(opt => opt.Name == "Path")?.Value ?? string.Empty;
                    panel = CreateDatasetDetailsGrid(path);
                }
                else if (blockState.BlockType == "Model")
                {
                    var arch = blockState.Options?.FirstOrDefault(opt => opt.Name == "Architecture")?.Value ?? "YOLOv8";
                    panel = CreateModelDetailGrid(
                        blockState.Options ?? new List<TrainingOption>(),
                        "🧠 " + arch,
                        BuildModelOptions(arch).sources
                    );
                }
                else if (blockState.BlockType == "Train")
                {
                    panel = CreateTrainDetailGrid(
                        blockState.Options ?? new List<TrainingOption>(),
                        "🚀 Training Options",
                        BuildTrainingSources()
                    );
                }

                if (panel == null)
                    continue;

                var border = CreateBlock(panel, blockState.X, blockState.Y, blockState.BlockType);

                switch (blockState.BlockType)
                {
                    case "Dataset":
                        datasetBlock = border;
                        break;
                    case "Model":
                        modelBlock = border;
                        break;
                    case "Train":
                        trainBlock = border;
                        break;
                }

                TrainingCanvas.Children.Add(border);
            }
        }


        public ModelPage()
        {
            InitializeComponent();
            Loaded += OnModelPageLoaded;

            InitializeTrainingStatus();
            EnsurePretrainEnvironment();

            // Initialize non-nullable fields to default values to satisfy the compiler  
            datasetBlock = new Border();
            pretrainFolderPath = string.Empty;
        }

        private void OnModelPageLoaded(object sender, RoutedEventArgs e)
        {
            InitializeTrainingStatus("Welcome! Please select a dataset to begin.");
            EnsurePretrainEnvironment();
            Console.WriteLine("ModelPage loaded successfully.");

            GlobalSignals.TrainingModeChanged.Subscribe(OnTrainingModeChanged);

            // Hide config panel if no project is loaded
            ConfigPanelGrid.Visibility = ProjectSession.CurrentProject == null
                ? Visibility.Collapsed
                : Visibility.Visible;
            // Restore block states on load
         
            RestoreBlockStates();

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
                Width = 230,
                Height = 380,
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
        private UIElement CreateTrainDetailGrid(List<TrainingOption> modelOptions, string blockLabel, Dictionary<string, string[]> optionSources)
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
                Header = "Train Option",
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
                Text = "Train Options",
                Foreground = Brushes.LightGray,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2),
                FontSize = 14
            });
            panel.Children.Add(modelGrid);

            return panel;
        }
        #region New function
        private (List<TrainingOption> options, Dictionary<string, string[]> sources) BuildModelOptions(string arch)
        {
            string baseArch = arch;
            string variant = null;

            // Normalize architecture and extract variant
            if (arch.StartsWith("YOLOv5"))
            {
                baseArch = "YOLOv5";
                variant = arch["YOLOv5".Length..].ToLower();
            }
            else if (arch.StartsWith("YOLOv8"))
            {
                baseArch = "YOLOv8";
                variant = arch["YOLOv8".Length..].ToLower();
            }

            // Define reusable defaults
            string defaultVariant = variant ?? "n";
            string defaultInput = "640";
            string defaultMode = "scratch";

            var sources = new Dictionary<string, string[]>();
            var options = new List<TrainingOption>();

            switch (baseArch)
            {
                case "YOLOv5":
                case "YOLOv8":
                    sources = new()
            {
                { "Architecture", new[] { baseArch } },
                { "Variant", new[] { "n", "s", "m", "l", "x" } },
                { "Input Size", baseArch == "YOLOv5" ? new[] { "320", "416", "512", "640" } : new[] { "320", "416", "512", "640", "768" } },
                { "Backbone", baseArch == "YOLOv5" ? new[] { "CSPDarknet", "Custom-ResNet" } : new[] { "None" } },
                { "Pretrained Weights", new[] {
                    $"{baseArch.ToLower()}n", $"{baseArch.ToLower()}s", $"{baseArch.ToLower()}m",
                    $"{baseArch.ToLower()}l", $"{baseArch.ToLower()}x", "None"
                }},
                { "Training mode", new[] { "scratch", "topup", "benchmark" } }
            };

                    options = new()
            {
                new TrainingOption { Name = "Architecture", Value = baseArch },
                new TrainingOption { Name = "Variant", Value = defaultVariant },
                new TrainingOption { Name = "Input Size", Value = defaultInput },
                new TrainingOption { Name = "Backbone", Value = sources["Backbone"][0] },
                new TrainingOption { Name = "Pretrained Weights", Value = $"{baseArch.ToLower()}{defaultVariant}" },
                new TrainingOption { Name = "Training mode", Value = defaultMode }
            };
                    break;

                case "YOLOv3":
                case "YOLOv4":
                case "YOLOv7":
                    sources = new()
            {
                { "Architecture", new[] { baseArch } },
                { "Input Size", new[] { "416", "512", "640" } },
                { "Backbone", new[] { "Darknet", "Custom-CSP" } },
                { "Pretrained Weights", new[] { "Default", "None" } },
                { "Training mode", new[] { "scratch", "topup", "benchmark" } }
            };

                    options = new()
            {
                new TrainingOption { Name = "Architecture", Value = baseArch },
                new TrainingOption { Name = "Input Size", Value = defaultInput },
                new TrainingOption { Name = "Backbone", Value = "Darknet" },
                new TrainingOption { Name = "Pretrained Weights", Value = "Default" },
                new TrainingOption { Name = "Training mode", Value = defaultMode }
            };
                    break;

                case "Faster R-CNN":
                case "Cascade R-CNN":
                    sources = new()
            {
                { "Architecture", new[] { baseArch } },
                { "Input Size", new[] { "512", "640", "768" } },
                { "Backbone", new[] { "ResNet50", "ResNet101" } },
                { "Pretrained Weights", new[] { "COCO", "None" } },
                { "Training mode", new[] { "scratch", "topup", "benchmark" } }
            };

                    options = new()
            {
                new TrainingOption { Name = "Architecture", Value = baseArch },
                new TrainingOption { Name = "Input Size", Value = defaultInput },
                new TrainingOption { Name = "Backbone", Value = "ResNet50" },
                new TrainingOption { Name = "Pretrained Weights", Value = "COCO" },
                new TrainingOption { Name = "Training mode", Value = defaultMode }
            };
                    break;

                case "SSD":
                case "RetinaNet":
                case "EfficientDet":
                case "CenterNet":
                case "DETR":
                    sources = new()
            {
                { "Architecture", new[] { baseArch } },
                { "Input Size", new[] { "512", "640", "768", "1024" } },
                { "Backbone", new[] { "ResNet", "EfficientNet", "Custom" } },
                { "Pretrained Weights", new[] { "Default", "None" } },
                { "Training mode", new[] { "scratch", "topup", "benchmark" } }
            };

                    options = new()
            {
                new TrainingOption { Name = "Architecture", Value = baseArch },
                new TrainingOption { Name = "Input Size", Value = defaultInput },
                new TrainingOption { Name = "Backbone", Value = "ResNet" },
                new TrainingOption { Name = "Pretrained Weights", Value = "Default" },
                new TrainingOption { Name = "Training mode", Value = defaultMode }
            };
                    break;

                case "ONNX":
                    sources = new()
            {
                { "Architecture", new[] { "ONNX" } },
                { "Input Size", new[] { "320", "416", "512", "640" } },
                { "Opset", new[] { "11", "12", "13" } },
                { "Training mode", new[] { "scratch", "topup", "benchmark" } }
            };

                    options = new()
            {
                new TrainingOption { Name = "Architecture", Value = "ONNX" },
                new TrainingOption { Name = "Input Size", Value = defaultInput },
                new TrainingOption { Name = "Opset", Value = "13" },
                new TrainingOption { Name = "Training mode", Value = defaultMode }
            };
                    break;

                case "Custom":
                    sources = new()
            {
                { "Architecture", new[] { "Custom" } },
                { "Input Size", new[] { "320", "416", "512", "640", "768", "1280" } },
                { "Backbone", new[] { "UserDefined" } },
                { "Training mode", new[] { "scratch", "topup", "benchmark" } }
            };

                    options = new()
            {
                new TrainingOption { Name = "Architecture", Value = "Custom" },
                new TrainingOption { Name = "Input Size", Value = defaultInput },
                new TrainingOption { Name = "Backbone", Value = "UserDefined" },
                new TrainingOption { Name = "Training mode", Value = defaultMode }
            };
                    break;
            }

            return (options, sources);
        }

        private void ModelArchComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)

        {
            if (TrainingCanvas == null) return;

            TrainingCanvas.Children.Remove(modelBlock);
            TrainingCanvas.Children.Remove(trainBlock);
            modelBlock = null;
            trainBlock = null;
            
            string selectedArch = (ModelArchComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "YOLOv8";
            var (modelOptions, modelSources) = 
                BuildModelOptions(selectedArch);
            var trainingSources = BuildTrainingSources();

            var trainingOptions = trainingSources
                .Select(kvp => new TrainingOption { Name = kvp.Key, Value = kvp.Value[0] })
                .ToList();

            double datasetBlockX = 10;
            double datasetBlockWidth = 230;
            double gap = 10;
            double modelBlockX = datasetBlockX + datasetBlockWidth + gap;
            double modelBlockY = 10;
            double trainBlockX = modelBlockX + 230 + gap;
            double trainBlockY = modelBlockY;

            var modelPanel = CreateModelDetailGrid(modelOptions, $"🧠 {selectedArch}", modelSources);
            modelBlock = CreateBlock(modelPanel, modelBlockX, modelBlockY, "Model");
            TrainingCanvas.Children.Add(modelBlock);

            var trainPanel = CreateTrainDetailGrid(trainingOptions, "🚀 Training Options", trainingSources);
            trainBlock = CreateBlock(trainPanel, trainBlockX, trainBlockY, "Train");
            TrainingCanvas.Children.Add(trainBlock);

            //var trainingMode = modelOptions.FirstOrDefault(opt => opt.Name == "Training mode")?.Value;
            //if (!string.IsNullOrEmpty(trainingMode))
            //    GlobalSignals.TrainingModeChanged.Fire(trainingMode);

            //TrainingStatusText.Text = $"Model and training options updated for {selectedArch}.";
            SaveBlockStates();
        }

        private void OnTrainingModeChanged(string mode)
        {
            TrainingStatusText.Text = $"🧠 Mode: {mode}";
            UpdateModelBlockPerTrainingMode(mode);
            SaveBlockStates();
      
        }
        private string GetDefaultWeight(string mode, string variant)
        {
            variant = variant?.ToLower() ?? "n";
            string type = selectedModelType?.ToLower();

            if (type.StartsWith("yolov5")) return $"yolov5{variant}";
            if (type.StartsWith("yolov8")) return $"yolov8{variant}";

            return type switch
            {
                "faster r-cnn" => "faster_rcnn_coco",
                "ssd" => "ssd_coco",
                "retinanet" => "retinanet_coco",
                "efficientdet" => "efficientdet_d0_coco",
                "centernet" => "centernet_coco",
                "yolov4" => "yolov4_coco",
                "yolov3" => "yolov3_coco",
                "yolov7" => "yolov7_coco",
                "detr" => "detr_coco",
                "cascade r-cnn" => "cascade_rcnn_coco",
                _ => "None"
            };
        }


        private string GetDefaultBackbone(string arch, string mode)
        {
            if (mode == "benchmark")
                return "None";

            arch = arch?.Trim() ?? string.Empty;

            // Explicit mappings by architecture
            if (arch.StartsWith("YOLOv3", StringComparison.OrdinalIgnoreCase) ||
                arch.StartsWith("YOLOv4", StringComparison.OrdinalIgnoreCase))
                return "Darknet";

            if (arch.StartsWith("YOLOv5", StringComparison.OrdinalIgnoreCase))
                return "CSPDarknet";

            if (arch.StartsWith("YOLOv7", StringComparison.OrdinalIgnoreCase))
                return "Custom-CSP";

            if (arch.StartsWith("YOLOv8", StringComparison.OrdinalIgnoreCase))
                return "C2f-Darknet"; // Adjust if needed

            if (arch.StartsWith("TopUp", StringComparison.OrdinalIgnoreCase))
                return "Custom-TopUp"; // Indicates fine-tuning logic

            if (arch.StartsWith("Scratch", StringComparison.OrdinalIgnoreCase))
                return "None"; // No pretrained backbone

            if (arch.Contains("R-CNN", StringComparison.OrdinalIgnoreCase))
                return "ResNet50";

            if (arch.StartsWith("SSD", StringComparison.OrdinalIgnoreCase) ||
                arch.StartsWith("RetinaNet", StringComparison.OrdinalIgnoreCase) ||
                arch.StartsWith("CenterNet", StringComparison.OrdinalIgnoreCase))
                return "ResNet";

            if (arch.StartsWith("EfficientDet", StringComparison.OrdinalIgnoreCase))
                return "EfficientNet";

            if (arch.StartsWith("DETR", StringComparison.OrdinalIgnoreCase))
                return "Transformer";

            // Fallback for unknown or custom architectures
            return "Custom-ResNet";
        }
        private void UpdateModelBlockPerTrainingMode(string selectedMode)
        {
            // Extract options BEFORE removing the block
            List<TrainingOption> existingOptions = new List<TrainingOption>();
            if (modelBlock != null)
            {
                var extracted = ExtractOptionsFromBlock(modelBlock);
                if (extracted != null)
                {
                    existingOptions = extracted;
                }

                TrainingCanvas.Children.Remove(modelBlock);
                modelBlock = null;
            }

            // Use selectedMode directly
            string trainingMode = selectedMode;

            // You may need to define or pass selectedModelType properly
            string architecture = GetOption("Architecture", "YOLOv8");
            string variant = GetOption("Variant", "n");

            string GetOption(string name, string fallback)
            {
                foreach (var opt in existingOptions)
                {
                    if (opt.Name == name)
                    {
                        return opt.Value;
                    }
                }
                return fallback;
            }



           


            // Inject missing options
            //EnsureOption("Pretrained Weights", GetDefaultWeight(trainingMode, variant));
            //EnsureOption("Backbone", GetDefaultBackbone(architecture, trainingMode));
            string weight = GetDefaultWeight(trainingMode, variant);
            string backbone = GetDefaultBackbone(architecture, trainingMode);

TrainingStatusText.Text += $"[Inject] Pretrained Weights = {weight}\n";
TrainingStatusText.Text += $"[Inject] Backbone = {backbone}\n";

//EnsureOption("Pretrained Weights", weight ?? "N/A");
//EnsureOption("Backbone", backbone ?? "N/A");

            // Determine block position
            double modelBlockX = 50;
            double modelBlockY = 50;

            if (datasetBlock != null)
            {
                modelBlockX = Canvas.GetLeft(datasetBlock) + datasetBlock.RenderSize.Width + 30;
                modelBlockY = Canvas.GetTop(datasetBlock);
            }

            // Rebuild model block
            var modelOptions = BuildModelOptions(architecture);
            var modelSources = modelOptions.sources;
            var modelPanel = CreateModelDetailGrid(existingOptions, "🧠 Updated Model", modelSources);
            TrainingStatusText.Text += "[Debug] existingOptions count: " + existingOptions.Count + "\n";
            foreach (var opt in existingOptions)
            {
                TrainingStatusText.Text += $"[Option] {opt.Name} = {opt.Value}\n";
            }

           
            modelBlock = CreateBlock(modelPanel, modelBlockX, modelBlockY, "Model");
            TrainingStatusText.Text += "[Debug] modelPanel is " + (modelPanel != null ? "valid" : "null") + "\n";
            TrainingStatusText.Text += "[Debug] Attempting to add modelBlock to canvas...\n";
            TrainingStatusText.Text += "[Debug] Canvas children before add: " + TrainingCanvas.Children.Count + "\n";

            TrainingCanvas.Children.Add(modelBlock);

            TrainingStatusText.Text += "[Debug] Canvas children after add: " + TrainingCanvas.Children.Count + "\n";
            TrainingStatusText.Text += "[Debug] modelBlock added successfully.\n";

        }


        #endregion

        private Dictionary<string, string[]> BuildTrainingSources()
        {
            return new()
    {
        { "Epochs", new[] { "1","10", "20", "50", "100", "200", "300" } },
        { "Batch Size", new[] { "8", "16", "32", "64", "128" } },
        { "Learning Rate", new[] { "0.0005", "0.001", "0.005", "0.01" } },
        { "Optimizer", new[] { "Adam", "SGD", "AdamW" } },
        { "Scheduler", new[] { "None", "Cosine", "Linear", "StepLR" } }
    };
        }
        // DO NOT MODIFY THIS METHOD (per your request)
        private UIElement CreateDatasetDetailsGrid(string datasetPath)
        {
            var datasetOptions = new List<TrainingOption>
    {
        new TrainingOption { Name = "Path", Value = datasetPath }
    };

            // Attempt to parse data.yaml for YOLO format metadata
            string yamlPath = System.IO.Path.Combine(datasetPath, "data.yaml");
            if (File.Exists(yamlPath))
            {
                datasetOptions.Add(new TrainingOption { Name = "Format", Value = "YOLO" });
                try
                {
                    var lines = File.ReadAllLines(yamlPath);
                    var ncLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("nc:"));
                    var namesLine = lines.FirstOrDefault(l => l.TrimStart().StartsWith("names:"));

                    if (ncLine != null)
                    {
                        var nc = ncLine.Split(':')[1].Trim();
                        datasetOptions.Add(new TrainingOption { Name = "Num Classes", Value = nc });
                    }

                    if (namesLine != null && namesLine.Contains("["))
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

            // Collect split statistics
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

            // Create labeled DataGrid for dataset options
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

            // Create labeled DataGrid for split statistics
            var statsGrid = new DataGrid
            {
                ItemsSource = datasetDetails,
                AutoGenerateColumns = false,
                CanUserAddRows = false,
                CanUserDeleteRows = false,
                HeadersVisibility = DataGridHeadersVisibility.Column,
                Margin = new Thickness(0)
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

            // Compose UI panel with headers and grids
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

            panel.Children.Add(new TextBlock
            {
                Text = "🧾 Dataset Options",
                Foreground = Brushes.LightGray,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 0, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            });
            panel.Children.Add(optionsGrid);

            panel.Children.Add(new TextBlock
            {
                Text = "📊 Split Statistics",
                Foreground = Brushes.LightGray,
                FontWeight = FontWeights.SemiBold,
                FontSize = 14,
                Margin = new Thickness(0, 12, 0, 4),
                HorizontalAlignment = HorizontalAlignment.Left
            });
            panel.Children.Add(statsGrid);

            ModelArchComboBox.Text = ""; // Reset model selection if applicable
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

                                    //MessageBox.Show(
                                    //    $"✅ Dataset loaded successfully:\n{datasetPath}",
                                    //    "Dataset Validated",
                                    //    MessageBoxButton.OK,
                                    //    MessageBoxImage.Information
                                    //);

                                    AddOrSelectDataset(datasetPath);

                                    if (datasetBlock == null)
                                    {
                                        var detailsGrid = CreateDatasetDetailsGrid(datasetPath);
                                        datasetBlock = CreateBlock(detailsGrid, 10, 10, "Dataset");
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
                        double datasetBlockX = 10;
                        double datasetBlockWidth = 230;
                        double gap = 10;
                        double modelBlockX = datasetBlockX + datasetBlockWidth + gap;
                        double modelBlockY = 10;

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
        private void NewModel_Click(object sender, RoutedEventArgs e)
        {
            ModelProjectSession.CurrentProject = new ModelProjectSession.Project();
            ConfigPanelGrid.Visibility = Visibility.Visible;
            TrainingStatusText.Text = "New project created. Configure your model and dataset.";
            // Optionally reset blocks and ComboBoxes here
            if (TrainingCanvas!=null)
            {
                TrainingCanvas.Children.Clear(); 
            }
            modelBlock = null;
            trainBlock = null;
            datasetBlock = new Border();
            DatasetComboBox.Items.Clear();
            ModelArchComboBox.SelectedIndex = -1;
        }
       
        private void OpenModel_Click(object sender, RoutedEventArgs e)
        {
            // Implement your open project logic here
            ModelProjectSession.CurrentProject = new ModelProjectSession.Project();
            ConfigPanelGrid.Visibility = Visibility.Visible;
            TrainingStatusText.Text = "Project opened. Configure your model and dataset.";
        }
        private void SaveModel_Click(object sender, RoutedEventArgs e)
        {
            // Implement your save project logic here
            MessageBox.Show("Save Project action triggered.");
        }
        private void ExportModel_Click(object sender, RoutedEventArgs e)
        {
            // Implement your export project logic here
            MessageBox.Show("Export Project action triggered.");
        }
        private void CloseModel_Click(object sender, RoutedEventArgs e)

        {
            ProjectSession.CurrentProject = null;
            ConfigPanelGrid.Visibility = Visibility.Collapsed;

            TrainingCanvas.Children.Clear();
            TrainingCanvas = null;
            modelBlock = null;
            trainBlock = null;
            datasetBlock = null;
            DatasetComboBox.Items.Clear();
            ModelArchComboBox.SelectedIndex = -1;
            TrainingStatusText.Text = "Project closed.";
            SaveBlockStates();
        }
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

            // ✅ Resolve training mode directly from modelOptions grid
            string selectedMode = modelOptions.FirstOrDefault(opt => opt.Name == "Training mode")?.Value?.ToLower() ?? "scratch";
            
            // ✅ Inject training mode into trainingOptions
            trainingOptions.RemoveAll(opt => opt.Name == "Training Mode");
            trainingOptions.Add(new TrainingOption { Name = "Training Mode", Value = selectedMode });
            

            // ✅ Resolve Python path
            string pythonPath;
            try
            {
                pythonPath = ResolvePythonPath();
            }
            catch (FileNotFoundException ex)
            {
                Dispatcher.Invoke(() => TrainingStatusText.Text = ex.Message);
                TrainModelButton.IsEnabled = true;
                return;
            }

            // ✅ Basic validation
            if (datasetOptions.Count == 0 || modelOptions.Count == 0)
            {
                MessageBox.Show("❌ Missing dataset or model configuration.", "Training Error", MessageBoxButton.OK, MessageBoxImage.Error);
                TrainingStatusText.Text = "Training aborted due to missing configuration.";
                TrainModelButton.IsEnabled = true;
                return;
            }

            // ✅ Determine architecture
            string architecture = modelOptions.FirstOrDefault(opt => opt.Name == "Architecture")?.Value ?? "YOLOv8";

            Task.Run(() =>
            {
                try
                {
                    if (architecture == "YOLOv8")
                    {
                        TrainingHelper.LaunchYOLOv8Training(datasetOptions, modelOptions, trainingOptions);
                    }
                    else if (architecture == "YOLOv5")
                    {
                        TrainingHelper.LaunchYOLOv5Training(datasetOptions, modelOptions, trainingOptions);
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

        private List<TrainingOption> ExtractOptionsFromBlock(UIElement? block)
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
                            // Remove previous dataset block if it exists
                            if (datasetBlock != null)
                            {
                                TrainingCanvas.Children.Remove(datasetBlock);
                                datasetBlock = null;
                            }

                            var detailsPanel = CreateDatasetDetailsGrid(datasetPath);
                            datasetBlock = CreateBlock(detailsPanel, 10, 10, "Dataset");
                            TrainingCanvas.Children.Add(datasetBlock);

                            // Save state only after the block is added
                            SaveBlockStates();
                        }
                    });
                });
            }
        }


    

        private void ProjectTypeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {

        }
    }
}
