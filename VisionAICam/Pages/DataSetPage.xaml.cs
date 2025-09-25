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

namespace VisionAICam.Pages
{
    public class ShapeMetadata
    {
        public string Label { get; set; }
        public AnnotationType Type { get; set; }
    }

    public enum AnnotationType
    {
        Rectangle,
        Polygon,
        FreePen
    }

    public class ShapeInfo
    {
        public Shape Shape { get; set; }
        public TextBlock LabelBlock { get; set; }
        public AnnotationRecord Record { get; set; }
        public ShapeMetadata Metadata { get; set; }
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
        private ShapeInfo? _currentDrawingShapeInfo = null;
        private bool _isDrawing = false;

        private ShapeInfo? _activeShapeInfo = null;
        private Point _dragStartPoint;
        private bool _isDraggingShape = false;

        private enum HitType { None, Body, TopLeft, TopRight, BottomLeft, BottomRight }
        private HitType _currentHit = HitType.None;
        private Ellipse? _activeHandle = null;
        private ShapeInfo? _reshapeShapeInfo = null;
        private readonly double _handleSize = 10.0;
        private readonly List<Ellipse> _handles = new();

        private Stack<List<AnnotationRecord>> _undoStack = new();
        private Stack<List<AnnotationRecord>> _redoStack = new();

        private List<AnnotationRecord> Annotations = new();
        private double _zoom = 1.0;
        private const double ZoomStep = 0.1;
        private const double ZoomMin = 0.1;
        private const double ZoomMax = 10.0;
        private AnnotationProject? _currentProject;

        private readonly List<ShapeInfo> _shapeInfos = new();

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
                    ShowNoProjectPanel();
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
        private void DrawingModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            switch ((DrawingModeComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString())
            {
                case "Free Pen": _currentDrawingMode = DrawingMode.FreePen; break;
                case "Rectangle": _currentDrawingMode = DrawingMode.Rectangle; break;
                case "Polygon": _currentDrawingMode = DrawingMode.Polygon; break;
            }
            _isDrawing = false;
            _currentPolygonPoints.Clear();
            if (_currentDrawingShapeInfo != null)
            {
                BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                _currentDrawingShapeInfo = null;
            }
            RemoveResizeHandles();
            SetStatus($"Drawing mode: {_currentDrawingMode}");
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
            if (_isDrawing && _currentDrawingShapeInfo?.Shape is Polyline && _currentPolygonPoints.Count > 2)
            {
                FinalizePolygon();
            }
        }
        private void FinalizePolygon()
        {
            if (_currentDrawingShapeInfo != null && _currentDrawingShapeInfo.Shape is Polyline)
            {
                // Convert to Polygon for final display
                var polygon = new Polygon
                {
                    Stroke = Brushes.Red,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    Points = new PointCollection(_currentPolygonPoints)
                };
                BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                BoundingBoxCanvas.Children.Add(polygon);

                // Move label to first point
                var first = _currentPolygonPoints.First();
                Canvas.SetLeft(_currentDrawingShapeInfo.LabelBlock, first.X + 1);
                Canvas.SetTop(_currentDrawingShapeInfo.LabelBlock, first.Y + 1);

                _currentDrawingShapeInfo.Record.Points = new List<Point>(_currentPolygonPoints);
                Annotations.Add(_currentDrawingShapeInfo.Record);
                SaveStateForUndo();
                RefreshAnnotations();
                SetStatus($"Polygon annotation added with label '{_currentDrawingShapeInfo.Metadata.Label}'.");
                _currentDrawingShapeInfo = null;
                _currentPolygonPoints.Clear();
                _isDrawing = false;
            }
        }

        private void BoundingBoxCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BoundingBoxCanvas.Focus();
            Point pt = e.GetPosition(BoundingBoxCanvas);

            if (!IsPointInImageBounds(pt))
            {
                SetStatus("Click inside the image to annotate.");
                return;
            }

            foreach (var handle in _handles)
            {
                if (IsPointOverHandle(pt, handle))
                {
                    _activeHandle = handle;
                    _currentHit = (HitType)handle.Tag;
                    _reshapeShapeInfo = _activeShapeInfo;
                    BoundingBoxCanvas.CaptureMouse();
                    SetStatus("Reshape started.");
                    e.Handled = true;
                    return;
                }
            }

            if (!_isDrawing)
            {
                _activeShapeInfo = _shapeInfos.LastOrDefault(info =>
                    info.Shape.IsMouseOver || info.LabelBlock.IsMouseOver);

                if (_activeShapeInfo != null)
                {
                    if (!IsShapeFullyInImage(_activeShapeInfo))
                    {
                        SetStatus("Shape must remain inside the image.");
                        return;
                    }
                    _dragStartPoint = pt;
                    _isDraggingShape = true;
                    HighlightShape(_activeShapeInfo, true);
                    AddResizeHandles(_activeShapeInfo);
                    BoundingBoxCanvas.CaptureMouse();
                    SetStatus("Shape selected for dragging.");
                    return;
                }
                else
                {
                    RemoveResizeHandles();
                }
            }

            var label = LabelComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(label))
            {
                SetStatus("Please select a label before drawing.");
                return;
            }

            _isDrawing = true;
            _dragStartPoint = pt;
            StartRectangle(pt, label);
        }

        private void BoundingBoxCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            Point pt = e.GetPosition(BoundingBoxCanvas);

            if (_activeHandle != null && _reshapeShapeInfo != null && e.LeftButton == MouseButtonState.Pressed)
            {
                Point clampedPt = ClampPointToImage(pt);
                ResizeRectangle(_reshapeShapeInfo, _currentHit, clampedPt);
                UpdateAnnotationRecordFromShape(_reshapeShapeInfo);
                RefreshHandles(_reshapeShapeInfo);
                return;
            }

            if (_isDraggingShape && _activeShapeInfo != null && e.LeftButton == MouseButtonState.Pressed)
            {
                Vector delta = pt - _dragStartPoint;
                delta = ClampDragDeltaToImage(_activeShapeInfo, delta);
                MoveShapeAndLabel(_activeShapeInfo, delta);
                _dragStartPoint += delta;
                RefreshHandles(_activeShapeInfo);
                return;
            }

            if (_isDrawing && _currentDrawingShapeInfo != null)
            {
                Point clampedPt = ClampPointToImage(pt);
                UpdateRectangle(clampedPt);
            }
        }

        private void BoundingBoxCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point pt = e.GetPosition(BoundingBoxCanvas);

            if (_activeHandle != null && _reshapeShapeInfo != null)
            {
                BoundingBoxCanvas.ReleaseMouseCapture();
                if (IsShapeFullyInImage(_reshapeShapeInfo))
                {
                    UpdateAnnotationRecordFromShape(_reshapeShapeInfo);
                    RefreshAnnotations();
                    AddResizeHandles(_reshapeShapeInfo);
                }
                else
                {
                    SetStatus("Shape must remain inside the image.");
                }
                _activeHandle = null;
                _reshapeShapeInfo = null;
                _currentHit = HitType.None;
                return;
            }

            if (_isDraggingShape && _activeShapeInfo != null)
            {
                _isDraggingShape = false;
                BoundingBoxCanvas.ReleaseMouseCapture();
                if (IsShapeFullyInImage(_activeShapeInfo))
                {
                    UpdateAnnotationRecordFromShape(_activeShapeInfo);
                    RefreshAnnotations();
                    HighlightShape(_activeShapeInfo, false);
                    AddResizeHandles(_activeShapeInfo);
                    SetStatus("Shape moved.");
                }
                else
                {
                    SetStatus("Shape must remain inside the image.");
                }
                _activeShapeInfo = null;
                return;
            }

            if (_isDrawing && _currentDrawingShapeInfo != null)
            {
                Point clampedPt = ClampPointToImage(pt);
                UpdateRectangle(clampedPt);
                if (IsShapeFullyInImage(_currentDrawingShapeInfo))
                {
                    FinalizeRectangle(clampedPt);
                }
                else
                {
                    BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                    BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                    _shapeInfos.Remove(_currentDrawingShapeInfo);
                    SetStatus("Rectangle must be fully inside the image.");
                }
                _isDrawing = false;
            }
        }

        // --- Helper Methods for Bounds and Clamping ---

        private bool IsPointInImageBounds(Point pt)
        {
            return pt.X >= 0 && pt.Y >= 0 && pt.X <= _currentImageWidth && pt.Y <= _currentImageHeight;
        }

        private Point ClampPointToImage(Point pt)
        {
            return new Point(
                Math.Max(0, Math.Min(_currentImageWidth, pt.X)),
                Math.Max(0, Math.Min(_currentImageHeight, pt.Y))
            );
        }

        private bool IsShapeFullyInImage(ShapeInfo info)
        {
            if (info.Record.Points.Count != 2) return false;
            var p0 = info.Record.Points[0];
            var p1 = info.Record.Points[1];
            return IsPointInImageBounds(p0) && IsPointInImageBounds(p1);
        }

        private Vector ClampDragDeltaToImage(ShapeInfo info, Vector delta)
        {
            if (info.Shape is Rectangle rect)
            {
                double left = Canvas.GetLeft(rect) + delta.X;
                double top = Canvas.GetTop(rect) + delta.Y;
                double right = left + rect.Width;
                double bottom = top + rect.Height;

                double dx = delta.X, dy = delta.Y;
                if (left < 0) dx -= left;
                if (top < 0) dy -= top;
                if (right > _currentImageWidth) dx -= (right - _currentImageWidth);
                if (bottom > _currentImageHeight) dy -= (bottom - _currentImageHeight);

                return new Vector(dx, dy);
            }
            return delta;
        }

        private bool IsPointOverHandle(Point pt, Ellipse handle)
        {
            double x = Canvas.GetLeft(handle) + _handleSize / 2;
            double y = Canvas.GetTop(handle) + _handleSize / 2;
            double dx = pt.X - x;
            double dy = pt.Y - y;
            return dx * dx + dy * dy <= (_handleSize * _handleSize) / 4;
        }

        private void SetStatus(string message)
        {
            if (StatusTextBlock != null)
                StatusTextBlock.Text = message;
        }

        // --- Drawing/Shape Helper Methods ---

        private void StartRectangle(Point start, string label)
        {
            var rect = new Rectangle
            {
                Stroke = Brushes.Blue,
                StrokeThickness = 2
            };
            Canvas.SetLeft(rect, start.X);
            Canvas.SetTop(rect, start.Y);

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = Brushes.Blue,
                Background = Brushes.White,
                FontWeight = FontWeights.Bold,
                FontSize = 14,
                Padding = new Thickness(2, 0, 2, 0)
            };
            Canvas.SetLeft(labelBlock, start.X + 1);
            Canvas.SetTop(labelBlock, start.Y + 1);

            var record = new AnnotationRecord
            {
                ImageName = System.IO.Path.GetFileName(_currentImagePath ?? ""),
                Label = label,
                AnnotationType = AnnotationType.Rectangle,
                Points = new List<Point> { start, start }
            };

            var info = new ShapeInfo
            {
                Shape = rect,
                LabelBlock = labelBlock,
                Record = record,
                Metadata = new ShapeMetadata { Label = label, Type = AnnotationType.Rectangle }
            };

            _currentDrawingShapeInfo = info;
            _shapeInfos.Add(info);

            BoundingBoxCanvas.Children.Add(rect);
            BoundingBoxCanvas.Children.Add(labelBlock);
        }

        private void UpdateRectangle(Point current)
        {
            if (_currentDrawingShapeInfo?.Shape is Rectangle rect)
            {
                var start = _currentDrawingShapeInfo.Record.Points[0];
                Point clamped = ClampPointToImage(current);
                double x = Math.Min(start.X, clamped.X);
                double y = Math.Min(start.Y, clamped.Y);
                double w = Math.Abs(clamped.X - start.X);
                double h = Math.Abs(clamped.Y - start.Y);

                Canvas.SetLeft(rect, x);
                Canvas.SetTop(rect, y);
                rect.Width = w;
                rect.Height = h;

                Canvas.SetLeft(_currentDrawingShapeInfo.LabelBlock, x + 1);
                Canvas.SetTop(_currentDrawingShapeInfo.LabelBlock, y + 1);

                _currentDrawingShapeInfo.Record.Points = new List<Point> { start, clamped };
            }
        }

        private void FinalizeRectangle(Point end)
        {
            if (_currentDrawingShapeInfo != null)
            {
                UpdateRectangle(end);
                if (IsShapeFullyInImage(_currentDrawingShapeInfo))
                {
                    Annotations.Add(_currentDrawingShapeInfo.Record);
                    SaveStateForUndo();
                    RefreshAnnotations();
                    SetStatus($"Rectangle annotation added with label '{_currentDrawingShapeInfo.Metadata.Label}'.");
                }
                else
                {
                    BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                    BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                    _shapeInfos.Remove(_currentDrawingShapeInfo);
                    SetStatus("Rectangle must be fully inside the image.");
                }
                _currentDrawingShapeInfo = null;
            }
        }

        private void MoveShapeAndLabel(ShapeInfo info, Vector delta)
        {
            if (info.Shape is Rectangle rect)
            {
                Vector clampedDelta = ClampDragDeltaToImage(info, delta);
                Canvas.SetLeft(rect, Canvas.GetLeft(rect) + clampedDelta.X);
                Canvas.SetTop(rect, Canvas.GetTop(rect) + clampedDelta.Y);
                Canvas.SetLeft(info.LabelBlock, Canvas.GetLeft(info.LabelBlock) + clampedDelta.X);
                Canvas.SetTop(info.LabelBlock, Canvas.GetTop(info.LabelBlock) + clampedDelta.Y);

                var p0 = info.Record.Points[0];
                var p1 = info.Record.Points[1];
                info.Record.Points[0] = new Point(p0.X + clampedDelta.X, p0.Y + clampedDelta.Y);
                info.Record.Points[1] = new Point(p1.X + clampedDelta.X, p1.Y + clampedDelta.Y);
            }
        }

        private void ResizeRectangle(ShapeInfo info, HitType hit, Point pt)
        {
            if (info.Shape is not Rectangle rect) return;
            var points = info.Record.Points.ToList();

            var p0 = points[0];
            var p1 = points[1];

            pt = ClampPointToImage(pt);

            switch (hit)
            {
                case HitType.TopLeft:
                    p0 = pt;
                    break;
                case HitType.TopRight:
                    p0 = new Point(p0.X, pt.Y);
                    p1 = new Point(pt.X, p1.Y);
                    break;
                case HitType.BottomLeft:
                    p0 = new Point(pt.X, p0.Y);
                    p1 = new Point(p1.X, pt.Y);
                    break;
                case HitType.BottomRight:
                    p1 = pt;
                    break;
            }

            info.Record.Points[0] = p0;
            info.Record.Points[1] = p1;
            double x = Math.Min(p0.X, p1.X);
            double y = Math.Min(p0.Y, p1.Y);
            double w = Math.Abs(p1.X - p0.X);
            double h = Math.Abs(p1.Y - p0.Y);

            Canvas.SetLeft(rect, x);
            Canvas.SetTop(rect, y);
            rect.Width = w;
            rect.Height = h;

            Canvas.SetLeft(info.LabelBlock, x + 1);
            Canvas.SetTop(info.LabelBlock, y + 1);
        }

        private void UpdateAnnotationRecordFromShape(ShapeInfo info)
        {
            if (info.Shape is Rectangle rect)
            {
                double x = Canvas.GetLeft(rect);
                double y = Canvas.GetTop(rect);
                double w = rect.Width;
                double h = rect.Height;
                info.Record.Points[0] = new Point(x, y);
                info.Record.Points[1] = new Point(x + w, y + h);
            }
        }

        private void HighlightShape(ShapeInfo info, bool highlight)
        {
            if (highlight)
            {
                info.Shape.Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Yellow,
                    BlurRadius = 10,
                    ShadowDepth = 0,
                    Opacity = 0.8
                };
            }
            else
            {
                info.Shape.Effect = null;
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

        private void AddResizeHandles(ShapeInfo info)
        {
            RemoveResizeHandles();

            if (info.Shape is not Rectangle rect) return;

            var points = info.Record.Points;
            if (points.Count != 2) return;

            var corners = new[]
            {
                new Point(points[0].X, points[0].Y), // TopLeft
                new Point(points[1].X, points[0].Y), // TopRight
                new Point(points[0].X, points[1].Y), // BottomLeft
                new Point(points[1].X, points[1].Y), // BottomRight
            };

            for (int i = 0; i < 4; i++)
            {
                var handle = new Ellipse
                {
                    Width = _handleSize,
                    Height = _handleSize,
                    Fill = Brushes.Yellow,
                    Stroke = Brushes.Orange,
                    StrokeThickness = 2,
                    Cursor = Cursors.SizeAll,
                    Tag = (HitType)(i + 1)
                };
                Canvas.SetLeft(handle, corners[i].X - _handleSize / 2);
                Canvas.SetTop(handle, corners[i].Y - _handleSize / 2);
                _handles.Add(handle);
                BoundingBoxCanvas.Children.Add(handle);
            }
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

        private void RemoveResizeHandles()
        {
            foreach (var h in _handles)
                BoundingBoxCanvas.Children.Remove(h);
            _handles.Clear();
        }

        private void RefreshHandles(ShapeInfo? info)
        {
            if (info != null)
            {
                AddResizeHandles(info);
            }
            else
            {
                RemoveResizeHandles();
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

        private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
        private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

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
        private void RefreshAnnotations()
        {
            var imageName = System.IO.Path.GetFileName(_currentImagePath ?? "");
            var filtered = Annotations.Where(a => a.ImageName == imageName).ToList();
            AnnotationListView.ItemsSource = filtered;

            BoundingBoxCanvas.Children.Clear();
            _shapeInfos.Clear();
            RemoveResizeHandles();

            foreach (var ann in filtered)
            {
                Shape shape = null;
                TextBlock label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(ann.Label) ? "(No Class)" : ann.Label,
                    FontWeight = FontWeights.Bold,
                    FontSize = 14,
                    Padding = new Thickness(2, 0, 2, 0),
                    Background = Brushes.White
                };

                switch (ann.AnnotationType)
                {
                    case AnnotationType.Rectangle:
                        if (ann.Points.Count == 2)
                        {
                            var x = Math.Min(ann.Points[0].X, ann.Points[1].X);
                            var y = Math.Min(ann.Points[0].Y, ann.Points[1].Y);
                            var w = Math.Abs(ann.Points[1].X - ann.Points[0].X);
                            var h = Math.Abs(ann.Points[1].Y - ann.Points[0].Y);

                            shape = new Rectangle
                            {
                                Stroke = Brushes.Blue,
                                StrokeThickness = 2,
                                Width = w,
                                Height = h
                            };
                            Canvas.SetLeft(shape, x);
                            Canvas.SetTop(shape, y);
                            label.Foreground = Brushes.Blue;
                            Canvas.SetLeft(label, x + 1);
                            Canvas.SetTop(label, y + 1);
                        }
                        break;
                    case AnnotationType.Polygon:
                        if (ann.Points.Count > 2)
                        {
                            shape = new Polygon
                            {
                                Stroke = Brushes.Red,
                                StrokeThickness = 2,
                                Fill = Brushes.Transparent,
                                Points = new PointCollection(ann.Points)
                            };
                            var first = ann.Points.First();
                            label.Foreground = Brushes.Red;
                            Canvas.SetLeft(label, first.X + 1);
                            Canvas.SetTop(label, first.Y + 1);
                        }
                        break;
                    case AnnotationType.FreePen:
                        if (ann.Points.Count > 1)
                        {
                            shape = new Polyline
                            {
                                Stroke = Brushes.Green,
                                StrokeThickness = 2,
                                Points = new PointCollection(ann.Points)
                            };
                            var first = ann.Points.First();
                            label.Foreground = Brushes.Green;
                            Canvas.SetLeft(label, first.X + 1);
                            Canvas.SetTop(label, first.Y + 1);
                        }
                        break;
                }

                if (shape != null)
                {
                    BoundingBoxCanvas.Children.Add(shape);
                    BoundingBoxCanvas.Children.Add(label);
                    _shapeInfos.Add(new ShapeInfo
                    {
                        Shape = shape,
                        LabelBlock = label,
                        Record = ann,
                        Metadata = new ShapeMetadata { Label = ann.Label, Type = ann.AnnotationType }
                    });
                }
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
        private void Exit_Click(object sender, RoutedEventArgs e)
        {
            ShowNoProjectPanel();
            SetStatus("No project loaded.");
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

            _isDrawing = false;
            _currentDrawingShapeInfo = null;
            _currentPolygonPoints.Clear();
            BoundingBoxCanvas.ReleaseMouseCapture();

            SetStatus($"Loaded image {index + 1} of {_imagePaths.Count}. Ready to draw.");
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

        private void Page_KeyDown(object sender, KeyEventArgs e)
        {
            if (NewClassTextBox.IsFocused)
                return;

            if (e.Key == Key.F) { NextImage_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.S) { PrevImage_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.A) { /* AddBox_Click(sender, e); */ e.Handled = true; }
            else if (e.Key == Key.D) { RemoveSelected_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Enter) { SaveAnnotations_Click(sender, e); e.Handled = true; }
            else if (e.Key == Key.Z && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { Undo(); e.Handled = true; }
            else if (e.Key == Key.Y && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) { Redo(); e.Handled = true; }
            else if (e.Key == Key.Delete)
            {
                // Remove the selected annotation from the list and update the view
                if (AnnotationListView.SelectedItem is AnnotationRecord selected)
                {
                    Annotations.Remove(selected);
                    ProjectSession.Annotations = Annotations;
                    SaveStateForUndo();
                    RefreshAnnotations();
                    SetStatus("Box removed.");
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                // Cancel drawing
                if (_isDrawing)
                {
                    _isDrawing = false;
                    _currentPolygonPoints.Clear();
                    if (_currentDrawingShapeInfo != null)
                    {
                        BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                        BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                        _currentDrawingShapeInfo = null;
                    }
                    SetStatus("Drawing canceled.");
                }

                // Cancel dragging
                if (_isDraggingShape)
                {
                    _isDraggingShape = false;
                    BoundingBoxCanvas.ReleaseMouseCapture();
                    if (_activeShapeInfo != null)
                    {
                        HighlightShape(_activeShapeInfo, false);
                        _activeShapeInfo = null;
                    }
                    RemoveResizeHandles();
                    SetStatus("Drag canceled.");
                }

                // Cancel reshaping
                if (_activeHandle != null)
                {
                    _activeHandle = null;
                    _reshapeShapeInfo = null;
                    _currentHit = HitType.None;
                    BoundingBoxCanvas.ReleaseMouseCapture();
                    RemoveResizeHandles();
                    SetStatus("Reshape canceled.");
                }

                e.Handled = true;
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

        // --- The rest of your project management, export, and UI methods remain unchanged ---
    }
}
