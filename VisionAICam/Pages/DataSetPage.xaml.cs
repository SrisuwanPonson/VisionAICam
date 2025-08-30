using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Ookii.Dialogs.Wpf;
using Python.Runtime;

namespace VisionAICam.Pages
{
    public enum AnnotationType
    {
        Rectangle,
        Polygon,
        FreePen
    }

    public partial class DataSetPage : Page
    {
        private List<string> _imagePaths = new();
        private int _currentImageIndex = -1;
        private int _currentImageWidth;
        private int _currentImageHeight;
        private string? _currentImagePath;

        private bool _isFullScreen = false;

        private enum DrawingMode { FreePen, Rectangle, Polygon }
        private DrawingMode _currentDrawingMode = DrawingMode.Rectangle;

        private List<Point> _currentPolygonPoints = new();
        private Polyline? _currentPolyline = null;
        private bool _isDrawingPolygon = false;

        private Point _rectStartPoint;
        private Rectangle? _currentRectangle = null;
        private bool _isDrawingRectangle = false;

        private Polyline? _currentFreePenLine = null;
        private bool _isDrawingFreePen = false;

        private Stack<List<AnnotationRecord>> _undoStack = new();
        private Stack<List<AnnotationRecord>> _redoStack = new();

        private List<AnnotationRecord> Annotations = new();
        private double _zoom = 1.0;
        private const double ZoomStep = 0.1;
        private const double ZoomMin = 0.1;
        private const double ZoomMax = 10.0;
        private AnnotationProject? _currentProject;

        public DataSetPage()
        {
            InitializeComponent();
            this.Focusable = true;
            this.Loaded += async (s, e) =>
            {
                this.Focus();
                SetStatus("Ready");

                if (ProjectSession.CurrentProject != null)
                {
                    _currentProject = ProjectSession.CurrentProject;
                    Annotations = ProjectSession.Annotations;
                    _imagePaths = ProjectSession.ImagePaths;
                    _currentImageIndex = ProjectSession.CurrentImageIndex;
                    _currentImagePath = ProjectSession.CurrentImagePath;
                    LabelComboBox.Items.Clear();
                    foreach (var label in _currentProject.ClassLabels)
                        LabelComboBox.Items.Add(label);

                    ShowMainContentPanel();
                    if (_currentImageIndex >= 0 && _currentImageIndex < _imagePaths.Count)
                        await LoadImageAtIndex(_currentImageIndex);
                }
                else
                {
                    NoProjectPanel.Visibility = Visibility.Visible;
                    MainContentPanel.Visibility = Visibility.Collapsed;
                }
            };

            BoundingBoxCanvas.IsEnabled = true;
            BoundingBoxCanvas.IsHitTestVisible = true;
            BoundingBoxCanvas.Background = Brushes.Transparent;

            DrawingModeComboBox.SelectionChanged += DrawingModeComboBox_SelectionChanged;
            BoundingBoxCanvas.MouseLeftButtonDown += BoundingBoxCanvas_MouseLeftButtonDown;
            BoundingBoxCanvas.MouseLeftButtonUp += BoundingBoxCanvas_MouseLeftButtonUp;
            BoundingBoxCanvas.MouseMove += BoundingBoxCanvas_MouseMove;
            BoundingBoxCanvas.MouseRightButtonDown += BoundingBoxCanvas_MouseRightButtonDown;
            BoundingBoxCanvas.MouseDown += BoundingBoxCanvas_MouseDown;
            BoundingBoxCanvas.MouseWheel += BoundingBoxCanvas_MouseWheel;
        }

        private void ShowNoProjectPanel()
        {
            NoProjectPanel.Visibility = Visibility.Visible;
            MainContentPanel.Visibility = Visibility.Collapsed;
        }

        private void ShowMainContentPanel()
        {
            NoProjectPanel.Visibility = Visibility.Collapsed;
            MainContentPanel.Visibility = Visibility.Visible;
        }

        private void BoundingBoxCanvas_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta > 0)
                ZoomIn_Click(sender, e);
            else if (e.Delta < 0)
                ZoomOut_Click(sender, e);
            e.Handled = true;
        }

        private void BoundingBoxCanvas_MouseDown(object sender, MouseButtonEventArgs e)
        {
            BoundingBoxCanvas.Focus();
        }

        private void BoundingBoxCanvas_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_isDrawingPolygon && _currentPolygonPoints.Count > 2)
            {
                _isDrawingPolygon = false;
                var imageName = System.IO.Path.GetFileName(_currentImagePath ?? "");
                var annotation = new AnnotationRecord
                {
                    ImageName = imageName,
                    Label = "Polygon",
                    AnnotationType = AnnotationType.Polygon,
                    Points = new List<Point>(_currentPolygonPoints)
                };
                Annotations.Add(annotation);
                ProjectSession.Annotations = Annotations;
                SaveStateForUndo();
                RefreshAnnotations();
                SetStatus("Polygon annotation added.");

                if (_currentPolyline != null)
                {
                    BoundingBoxCanvas.Children.Remove(_currentPolyline);
                    _currentPolyline = null;
                }
                _currentPolygonPoints.Clear();
            }
        }

        private void SaveProject_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null) return;
            _currentProject.ImagePaths = _imagePaths;
            _currentProject.Annotations = Annotations;
            _currentProject.ClassLabels = LabelComboBox.Items.Cast<object>().Select(i => i.ToString() ?? "").ToList();
            _currentProject.SelectedImageIndex = _currentImageIndex;

            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "Annotation Project (*.json)|*.json|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(_currentProject, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(dialog.FileName, json);
                SetStatus("Project saved.");
            }
        }

        private void BoundingBoxCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point pt = e.GetPosition(BoundingBoxCanvas);

            if (_isDrawingRectangle && _currentRectangle != null)
            {
                double x = Math.Min(pt.X, _rectStartPoint.X);
                double y = Math.Min(pt.Y, _rectStartPoint.Y);
                double w = Math.Abs(pt.X - _rectStartPoint.X);
                double h = Math.Abs(pt.Y - _rectStartPoint.Y);

                Canvas.SetLeft(_currentRectangle, x);
                Canvas.SetTop(_currentRectangle, y);
                _currentRectangle.Width = w;
                _currentRectangle.Height = h;
            }
            else if (_isDrawingFreePen && _currentFreePenLine != null)
            {
                _currentFreePenLine.Points.Add(pt);
            }
        }

        private void SetStatus(string message)
        {
            if (StatusTextBlock != null)
                StatusTextBlock.Text = message;
        }

        private void BoundingBoxCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point pt = e.GetPosition(BoundingBoxCanvas);

            if (_isDrawingRectangle && _currentRectangle != null)
            {
                _isDrawingRectangle = false;
                BoundingBoxCanvas.ReleaseMouseCapture();

                var imageName = System.IO.Path.GetFileName(_currentImagePath ?? "");
                var selectedLabel = LabelComboBox.SelectedItem?.ToString() ?? "";
                var annotation = new AnnotationRecord
                {
                    ImageName = imageName,
                    Label = selectedLabel,
                    AnnotationType = AnnotationType.Rectangle,
                    Points = new List<Point> { _rectStartPoint, pt }
                };
                Annotations.Add(annotation);
                SaveStateForUndo();
                RefreshAnnotations();
                SetStatus($"Rectangle annotation added with label '{selectedLabel}'.");
            }
            else if (_isDrawingFreePen && _currentFreePenLine != null)
            {
                _isDrawingFreePen = false;
                BoundingBoxCanvas.ReleaseMouseCapture();

                var imageName = System.IO.Path.GetFileName(_currentImagePath ?? "");
                var selectedLabel = LabelComboBox.SelectedItem?.ToString() ?? "";
                var annotation = new AnnotationRecord
                {
                    ImageName = imageName,
                    Label = selectedLabel,
                    AnnotationType = AnnotationType.FreePen,
                    Points = _currentFreePenLine.Points.ToList()
                };
                Annotations.Add(annotation);
                SaveStateForUndo();
                RefreshAnnotations();
                SetStatus($"Free pen annotation added with label '{selectedLabel}'.");
            }
        }

        private void Page_KeyDown(object sender, KeyEventArgs e)
        {
            if (NewClassTextBox.IsFocused)
                return;

            if (e.Key == Key.F) { NextImage_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.S) { PrevImage_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.A) { AddBox_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.D) { RemoveSelected_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Enter) { SaveAnnotations_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { Undo(); e.Handled = true; }
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { Redo(); e.Handled = true; }
        }

        private async void NextImage_Click(object sender, RoutedEventArgs e)
        {
            if (_imagePaths != null && _currentImageIndex < _imagePaths.Count - 1)
            {
                SaveAnnotations_Click(sender, e);
                _currentImageIndex++;
                ProjectSession.CurrentImageIndex = _currentImageIndex;
                ProjectSession.CurrentImagePath = _currentImagePath;
                await LoadImageAtIndex(_currentImageIndex);
            }
        }

        private void Redo()
        {
            if (_redoStack.Count > 0)
            {
                _undoStack.Push(Annotations.Select(a => new AnnotationRecord
                {
                    ImageName = a.ImageName,
                    Label = a.Label,
                    AnnotationType = a.AnnotationType,
                    Points = a.Points.ToList()
                }).ToList());
                Annotations = _redoStack.Pop();
                RefreshAnnotations();
                SetStatus("Redo.");
            }
        }

        private async void PrevImage_Click(object sender, RoutedEventArgs e)
        {
            if (_currentImageIndex > 0)
            {
                SaveAnnotations_Click(sender, e);
                _currentImageIndex--;
                ProjectSession.CurrentImageIndex = _currentImageIndex;
                ProjectSession.CurrentImagePath = _currentImagePath;
                await LoadImageAtIndex(_currentImageIndex);
            }
        }

        private void Undo()
        {
            if (_undoStack.Count > 0)
            {
                _redoStack.Push(Annotations.Select(a => new AnnotationRecord
                {
                    ImageName = a.ImageName,
                    Label = a.Label,
                    AnnotationType = a.AnnotationType,
                    Points = a.Points.ToList()
                }).ToList());
                Annotations = _undoStack.Pop();
                RefreshAnnotations();
                SetStatus("Undo.");
            }
        }

        private void SaveAnnotations_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject != null)
            {
                _currentProject.Annotations = Annotations;
                _currentProject.SelectedImageIndex = _currentImageIndex;
                ProjectSession.Annotations = Annotations;
                ProjectSession.CurrentImageIndex = _currentImageIndex;
                ProjectSession.CurrentImagePath = _currentImagePath;
                SetStatus("Annotations saved.");
            }
            else
            {
                SetStatus("No project loaded. Cannot save annotations.");
            }
        }

        private void ExportYoloV5_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || _currentProject.ImagePaths.Count == 0)
            {
                SetStatus("No project or images to export.");
                return;
            }

            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select output folder for YOLOv5 export",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == true)
            {
                string outputFolder = System.IO.Path.Combine(
                    dialog.SelectedPath,
                    $"Yolo5Export_{DateTime.Now:yyyyMMdd_HHmmss}"
                );

                YoloExporter.ExportWithSplit(
                    _currentProject,
                    outputFolder,
                    imageName =>
                    {
                        var path = _currentProject.ImagePaths.FirstOrDefault(p => System.IO.Path.GetFileName(p) == imageName);
                        if (path == null) return new Size(0, 0);
                        try
                        {
                            using var img = System.Drawing.Image.FromFile(path);
                            return new Size(img.Width, img.Height);
                        }
                        catch
                        {
                            return new Size(0, 0);
                        }
                    },
                    trainRatio: 0.7, 0.2, 0.1,
                    exportFormat: YoloExportFormat.YoloV5
                );

                SetStatus("YOLOv5 export complete (train/val/test).");
            }
            else
            {
                SetStatus("YOLOv5 export canceled.");
            }
        }

        private void ExportYoloV8_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || _currentProject.ImagePaths.Count == 0)
            {
                SetStatus("No project or images to export.");
                return;
            }

            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select output folder for YOLOv8 export",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == true)
            {
                string outputFolder = System.IO.Path.Combine(
                    dialog.SelectedPath,
                    $"Yolo8Export_{DateTime.Now:yyyyMMdd_HHmmss}"
                );

                YoloExporter.ExportWithSplit(
                    _currentProject,
                    outputFolder,
                    imageName =>
                    {
                        var path = _currentProject.ImagePaths.FirstOrDefault(p => System.IO.Path.GetFileName(p) == imageName);
                        if (path == null) return new Size(0, 0);
                        try
                        {
                            using var img = System.Drawing.Image.FromFile(path);
                            return new Size(img.Width, img.Height);
                        }
                        catch
                        {
                            return new Size(0, 0);
                        }
                    },
                    trainRatio: 0.7, 0.2, 0.1,
                    exportFormat: YoloExportFormat.YoloV8
                );

                SetStatus("YOLOv8 export complete (train/val/test).");
            }
            else
            {
                SetStatus("YOLOv8 export canceled.");
            }
        }

        private void ViewHelp_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "How to use:\n\n" +
                "1. Load a folder of images.\n" +
                "2. Select or add a label/class.\n" +
                "3. Draw annotations using the selected mode.\n" +
                "4. Use the menu to save, export, or finish your project.\n\n" +
                "Keyboard Shortcuts:\n" +
                "F: Next image\n" +
                "S: Previous image\n" +
                "A: Add box\n" +
                "D: Remove selected\n" +
                "E: Export YOLO\n" +
                "Enter: Save annotations\n" +
                "Ctrl+Z: Undo\n" +
                "Ctrl+Y: Redo\n" +
                "F: Toggle full screen\n",
                "Help",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        private void About_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "VisionAICam\n" +
                "Image Annotation Tool\n\n" +
                "Version 1.0\n" +
                "© 2024 Your Company/Team Name",
                "About",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        private void RemoveSelected_Click(object sender, RoutedEventArgs e)
        {
            if (AnnotationListView.SelectedItem is AnnotationRecord selected)
            {
                Annotations.Remove(selected);
                ProjectSession.Annotations = Annotations;
                SaveStateForUndo();
                RefreshAnnotations();
                SetStatus("Box removed.");
            }
        }

        private void SaveStateForUndo()
        {
            _undoStack.Push(Annotations.Select(a => new AnnotationRecord
            {
                ImageName = a.ImageName,
                Label = a.Label,
                AnnotationType = a.AnnotationType,
                Points = a.Points.ToList()
            }).ToList());
            _redoStack.Clear();
        }

        private void AddBox_Click(object sender, RoutedEventArgs e)
        {
            SetStatus("Add Box clicked.");
        }

        private void DrawingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch ((DrawingModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString())
            {
                case "Free Pen": _currentDrawingMode = DrawingMode.FreePen; break;
                case "Rectangle": _currentDrawingMode = DrawingMode.Rectangle; break;
                case "Polygon": _currentDrawingMode = DrawingMode.Polygon; break;
            }
            _isDrawingPolygon = false;
            _currentPolygonPoints.Clear();
            if (_currentPolyline != null)
            {
                BoundingBoxCanvas.Children.Remove(_currentPolyline);
                _currentPolyline = null;
            }
            _isDrawingRectangle = false;
            if (_currentRectangle != null)
            {
                BoundingBoxCanvas.Children.Remove(_currentRectangle);
                _currentRectangle = null;
            }
            _isDrawingFreePen = false;
            if (_currentFreePenLine != null)
            {
                BoundingBoxCanvas.Children.Remove(_currentFreePenLine);
                _currentFreePenLine = null;
            }
            SetStatus($"Drawing mode: {_currentDrawingMode}");
        }

        private void BoundingBoxCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BoundingBoxCanvas.Focus();

            var label = LabelComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(label))
            {
                SetStatus("Please select a label before drawing.");
                return;
            }

            Point pt = e.GetPosition(BoundingBoxCanvas);

            switch (_currentDrawingMode)
            {
                case DrawingMode.Polygon:
                    if (!_isDrawingPolygon)
                    {
                        _currentPolygonPoints.Clear();
                        _currentPolyline = new Polyline
                        {
                            Stroke = Brushes.Red,
                            StrokeThickness = 2
                        };
                        BoundingBoxCanvas.Children.Add(_currentPolyline);
                        _isDrawingPolygon = true;
                    }
                    _currentPolygonPoints.Add(pt);
                    _currentPolyline.Points.Add(pt);
                    SetStatus("Polygon point added.");
                    break;

                case DrawingMode.Rectangle:
                    _rectStartPoint = pt;
                    _currentRectangle = new Rectangle
                    {
                        Stroke = Brushes.Blue,
                        StrokeThickness = 2
                    };
                    Canvas.SetLeft(_currentRectangle, _rectStartPoint.X);
                    Canvas.SetTop(_currentRectangle, _rectStartPoint.Y);
                    BoundingBoxCanvas.Children.Add(_currentRectangle);
                    _isDrawingRectangle = true;
                    if (!BoundingBoxCanvas.IsMouseCaptured)
                    {
                        BoundingBoxCanvas.CaptureMouse();
                        SetStatus($"Started rectangle at ({_rectStartPoint.X:0},{_rectStartPoint.Y:0}), mouse captured.");
                    }
                    else
                    {
                        SetStatus("Warning: Mouse already captured by canvas.");
                    }
                    break;

                case DrawingMode.FreePen:
                    _currentFreePenLine = new Polyline
                    {
                        Stroke = Brushes.Green,
                        StrokeThickness = 2
                    };
                    _currentFreePenLine.Points.Add(pt);
                    BoundingBoxCanvas.Children.Add(_currentFreePenLine);
                    _isDrawingFreePen = true;
                    BoundingBoxCanvas.CaptureMouse();
                    SetStatus("Drawing free pen...");
                    break;
            }
        }

        private async Task LoadImageAtIndex(int index)
        {
            if (_imagePaths == null || index < 0 || index >= _imagePaths.Count)
                return;

            var imagePath = _imagePaths[index];
            _currentImagePath = imagePath;

            BitmapImage bitmap = null;
            await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(imagePath);
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                bitmap = bmp;
            });

            LabelingImage.Source = bitmap;
            _currentImageWidth = bitmap.PixelWidth;
            _currentImageHeight = bitmap.PixelHeight;

            LabelingImage.Width = _currentImageWidth;
            LabelingImage.Height = _currentImageHeight;
            BoundingBoxCanvas.Width = _currentImageWidth;
            BoundingBoxCanvas.Height = _currentImageHeight;

            BoundingBoxCanvas.IsEnabled = true;
            BoundingBoxCanvas.IsHitTestVisible = true;
            BoundingBoxCanvas.Focusable = true;
            BoundingBoxCanvas.Background = Brushes.Transparent;
            BoundingBoxCanvas.Focus();

            RefreshAnnotations();

            ImageProgressText.Text = $"Image {index + 1} of {_imagePaths.Count} ({(int)(((index + 1) * 100.0) / _imagePaths.Count)}%)";

            _isDrawingRectangle = false;
            _isDrawingPolygon = false;
            _isDrawingFreePen = false;
            _currentRectangle = null;
            _currentPolyline = null;
            _currentFreePenLine = null;
            _currentPolygonPoints.Clear();
            BoundingBoxCanvas.ReleaseMouseCapture();

            SetStatus($"Loaded image {index + 1} of {_imagePaths.Count}. Ready to draw.");
        }

        private void RefreshAnnotations()
        {
            var imageName = System.IO.Path.GetFileName(_currentImagePath ?? "");
            var filtered = Annotations.Where(a => a.ImageName == imageName).ToList();
            AnnotationListView.ItemsSource = filtered;

            BoundingBoxCanvas.Children.Clear();
            foreach (var ann in filtered)
            {
                switch (ann.AnnotationType)
                {
                    case AnnotationType.Rectangle:
                        if (ann.Points.Count == 2)
                        {
                            var x = Math.Min(ann.Points[0].X, ann.Points[1].X);
                            var y = Math.Min(ann.Points[0].Y, ann.Points[1].Y);
                            var w = Math.Abs(ann.Points[1].X - ann.Points[0].X);
                            var h = Math.Abs(ann.Points[1].Y - ann.Points[0].Y);

                            var rect = new Rectangle
                            {
                                Stroke = Brushes.Blue,
                                StrokeThickness = 2,
                                Width = w,
                                Height = h
                            };
                            Canvas.SetLeft(rect, x);
                            Canvas.SetTop(rect, y);
                            BoundingBoxCanvas.Children.Add(rect);

                            var label = new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(ann.Label) ? "(No Class)" : ann.Label,
                                Foreground = Brushes.Blue,
                                Background = Brushes.White,
                                FontWeight = FontWeights.Bold,
                                FontSize = 14,
                                Padding = new Thickness(2, 0, 2, 0)
                            };
                            Canvas.SetLeft(label, x + 1);
                            Canvas.SetTop(label, y + 1);
                            BoundingBoxCanvas.Children.Add(label);
                        }
                        break;

                    case AnnotationType.Polygon:
                        if (ann.Points.Count > 2)
                        {
                            var polygon = new Polygon
                            {
                                Stroke = Brushes.Red,
                                StrokeThickness = 2,
                                Fill = Brushes.Transparent,
                                Points = new PointCollection(ann.Points)
                            };
                            BoundingBoxCanvas.Children.Add(polygon);
                        }
                        break;
                    case AnnotationType.FreePen:
                        if (ann.Points.Count > 1)
                        {
                            var polyline = new Polyline
                            {
                                Stroke = Brushes.Green,
                                StrokeThickness = 2,
                                Points = new PointCollection(ann.Points)
                            };
                            BoundingBoxCanvas.Children.Add(polyline);
                        }
                        break;
                }
            }
        }

        private void FullScreenToggleButton_Click(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;

            if (!_isFullScreen)
            {
                window.WindowStyle = WindowStyle.None;
                window.WindowState = WindowState.Maximized;
                window.ResizeMode = ResizeMode.NoResize;
                _isFullScreen = true;
                SetStatus("Entered full screen mode.");
            }
            else
            {
                window.WindowStyle = WindowStyle.SingleBorderWindow;
                window.WindowState = WindowState.Normal;
                window.ResizeMode = ResizeMode.CanResize;
                _isFullScreen = false;
                SetStatus("Exited full screen mode.");
            }
        }

        private void AddClassButton_Click(object sender, RoutedEventArgs e)
        {
            var newClass = NewClassTextBox.Text.Trim();
            if (string.IsNullOrEmpty(newClass))
            {
                SetStatus("Class name cannot be empty.");
                return;
            }

            bool exists = false;
            foreach (var item in LabelComboBox.Items)
            {
                if (item is ComboBoxItem comboItem &&
                    string.Equals(comboItem.Content?.ToString(), newClass, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
                else if (item is string str &&
                    string.Equals(str, newClass, StringComparison.OrdinalIgnoreCase))
                {
                    exists = true;
                    break;
                }
            }

            if (exists)
            {
                SetStatus("Class already exists.");
                return;
            }

            LabelComboBox.Items.Add(newClass);
            LabelComboBox.SelectedItem = newClass;
            NewClassTextBox.Text = string.Empty;
            SetStatus($"Class '{newClass}' added.");
        }

        public async void LoadFolder_Click(object sender, RoutedEventArgs e)
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
                    SetStatus("No images found.");
                    return;
                }

                _currentImageIndex = 0;
                await LoadImageAtIndex(_currentImageIndex);
            }
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Min(_zoom + ZoomStep, ZoomMax);
            ZoomTransform.ScaleX = _zoom;
            ZoomTransform.ScaleY = _zoom;
            SetStatus($"Zoom: {_zoom * 100:0}%");
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Max(_zoom - ZoomStep, ZoomMin);
            ZoomTransform.ScaleX = _zoom;
            ZoomTransform.ScaleY = _zoom;
            SetStatus($"Zoom: {_zoom * 100:0}%");
        }

        private void Undo_Click(object sender, RoutedEventArgs e)
        {
            Undo();
        }

        private void Redo_Click(object sender, RoutedEventArgs e)
        {
            Redo();
        }

        private void CreateProject_Click(object sender, RoutedEventArgs e)
        {
            _imagePaths = new List<string>();
            _currentImageIndex = -1;
            _currentImagePath = null;
            Annotations = new List<AnnotationRecord>();
            LabelComboBox.Items.Clear();
            NewClassTextBox.Text = string.Empty;
            AnnotationListView.ItemsSource = null;
            LabelingImage.Source = null;
            BoundingBoxCanvas.Children.Clear();
            ImageProgressText.Text = "No images loaded";

            _currentProject = new AnnotationProject
            {
                ProjectName = "Untitled Project",
                ImagePaths = _imagePaths,
                ClassLabels = new List<string>(),
                Annotations = Annotations
            };
            ProjectSession.CurrentProject = _currentProject;
            ProjectSession.Annotations = Annotations;
            ProjectSession.ImagePaths = _imagePaths;
            ProjectSession.CurrentImageIndex = _currentImageIndex;
            ProjectSession.CurrentImagePath = _currentImagePath;

            SetStatus("New project created. Please load images and add classes.");
            ShowMainContentPanel();
        }

        private void OpenProject_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Annotation Project (*.json)|*.json|All Files (*.*)|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                var json = File.ReadAllText(dialog.FileName);
                _currentProject = System.Text.Json.JsonSerializer.Deserialize<AnnotationProject>(json);
                if (_currentProject != null)
                {
                    _imagePaths = _currentProject.ImagePaths;
                    Annotations = _currentProject.Annotations;
                    LabelComboBox.Items.Clear();
                    foreach (var label in _currentProject.ClassLabels)
                        LabelComboBox.Items.Add(label);

                    _currentImageIndex = (_currentProject.SelectedImageIndex >= 0 && _currentProject.SelectedImageIndex < _imagePaths.Count)
                        ? _currentProject.SelectedImageIndex
                        : 0;

                    ProjectSession.CurrentProject = _currentProject;
                    ProjectSession.Annotations = Annotations;
                    ProjectSession.ImagePaths = _imagePaths;
                    ProjectSession.CurrentImageIndex = _currentImageIndex;
                    ProjectSession.CurrentImagePath = _imagePaths.Count > 0 ? _imagePaths[_currentImageIndex] : null;

                    _ = LoadImageAtIndex(_currentImageIndex);
                    SetStatus("Project opened.");
                    ShowMainContentPanel();
                }
            }
        }

        private void CloseProject_Click(object sender, RoutedEventArgs e)
        {
            var result = MessageBox.Show(
                "Do you want to save your project before closing?",
                "Close Project",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);

            if (result == MessageBoxResult.Cancel)
                return;

            if (result == MessageBoxResult.Yes)
                SaveProject_Click(sender, e);

            _currentProject = null;
            _imagePaths = new List<string>();
            _currentImageIndex = -1;
            _currentImagePath = null;
            Annotations = new List<AnnotationRecord>();
            LabelComboBox.Items.Clear();
            NewClassTextBox.Text = string.Empty;
            AnnotationListView.ItemsSource = null;
            LabelingImage.Source = null;
            BoundingBoxCanvas.Children.Clear();
            ImageProgressText.Text = "No images loaded";
            ShowNoProjectPanel();
            SetStatus("Project closed.");

            ProjectSession.CurrentProject = null;
            ProjectSession.Annotations = new List<AnnotationRecord>();
            ProjectSession.ImagePaths = new List<string>();
            ProjectSession.CurrentImageIndex = -1;
            ProjectSession.CurrentImagePath = null;
        }

        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            ShowNoProjectPanel();
            SetStatus("No project loaded.");
        }
    }
}
