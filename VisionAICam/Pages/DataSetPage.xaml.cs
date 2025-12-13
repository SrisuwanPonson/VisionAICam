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
    public class PolygonVertexHit
    {
        public int VertexIndex { get; }
        public PolygonVertexHit(int index) => VertexIndex = index;
    }
    public enum AnnotationType
    {
        Rectangle,
        Polygon,
        FreePen,
        RotatedBox
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

        // NEW: pending selection when user presses left button but drag should start only after moving into shape
        private ShapeInfo? _pendingShapeInfo = null;
        private Point _mouseDownPoint;
        private bool _mouseLeftDown = false;

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
                Logger.Instance.LogInfo("DataSetPage loaded.");
                
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

        #region Mouse handle

        private void AddPolygonHandles(ShapeInfo shapeInfo)
        {
            RemoveResizeHandles(); // Clear old handles

            if (shapeInfo.Shape is Polyline polyline)
            {
                for (int i = 0; i < polyline.Points.Count; i++)
                {
                    var point = polyline.Points[i];
                    var handle = CreateHandle(point);
                    handle.Tag = new PolygonVertexHit(i);
                    _handles.Add(handle);
                    BoundingBoxCanvas.Children.Add(handle);
                }
            }
        }
        private Ellipse CreateHandle(Point point)
        {
            var handle = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = Brushes.Blue,
                Stroke = Brushes.White,
                StrokeThickness = 1,
                Cursor = Cursors.SizeAll
            };

            Canvas.SetLeft(handle, point.X - handle.Width / 2);
            Canvas.SetTop(handle, point.Y - handle.Height / 2);

            return handle;
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
            // If drawing and current shape is a Polyline handle both polygon-close and free-pen finalize
            if (_isDrawing && _currentDrawingShapeInfo?.Shape is Polyline)
            {
                if (_currentDrawingMode == DrawingMode.Polygon && _currentPolygonPoints.Count > 2)
                {
                    FinalizePolygon();
                    return;
                }

                if (_currentDrawingMode == DrawingMode.FreePen)
                {
                    // finalize free-pen using the current mouse position (clamped)
                    var clamped = ClampPointToImage(e.GetPosition(BoundingBoxCanvas));
                    FinalizeFreePen(clamped);
                    return;
                }
            }
        }
        private void FinalizeFreePen(Point end)
        {
            if (_currentDrawingShapeInfo == null || _currentDrawingShapeInfo.Shape is not Polyline poly)
                return;

            // Release any mouse capture
            BoundingBoxCanvas.ReleaseMouseCapture();

            // Clamp final point and add if different enough
            var clamped = ClampPointToImage(end);
            var last = poly.Points.Count > 0 ? poly.Points[poly.Points.Count - 1] : new Point(double.NaN, double.NaN);
            if (double.IsNaN(last.X) ||
                Math.Abs(last.X - clamped.X) > 0.5 ||
                Math.Abs(last.Y - clamped.Y) > 0.5)
            {
                poly.Points.Add(clamped);
                _currentDrawingShapeInfo.Record.Points.Add(clamped);
            }

            // Decide whether to keep the stroke (require at least 2 points)
            if (poly.Points.Count > 1)
            {
                // Commit record
                _currentDrawingShapeInfo.Record.Points = new List<Point>(poly.Points);
                Annotations.Add(_currentDrawingShapeInfo.Record);
                SaveStateForUndo();

                // Keep session/project in sync so exports and saves include this annotation
                ProjectSession.Annotations = Annotations;
                if (_currentProject != null)
                    _currentProject.Annotations = Annotations;

                // Refresh UI from annotations (will recreate visuals consistently)
                RefreshAnnotations();
                SetStatus($"Free-pen annotation added with label '{_currentDrawingShapeInfo.Metadata.Label}'.");
            }
            else
            {
                // Too short — remove temporary visuals and shape info
                BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                _shapeInfos.Remove(_currentDrawingShapeInfo);
                SetStatus("Free-pen stroke too short, ignored.");
            }

            _currentDrawingShapeInfo = null;
            _isDrawing = false;
        }

        private void BoundingBoxCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BoundingBoxCanvas.Focus();
            Point pt = ClampPointToImage(e.GetPosition(BoundingBoxCanvas));
            _mouseLeftDown = true;
            _mouseDownPoint = pt;

            if (!IsPointInImageBounds(pt))
            {
                SetStatus("Click inside the image to annotate.");
                _mouseLeftDown = false;
                return;
            }

            // Check if clicking on a handle (start reshape immediately)
            foreach (var handle in _handles)
            {
                if (IsPointOverHandle(pt, handle))
                {
                    _activeHandle = handle;
                    // attempt to recover hit info (some handles use HitType or PolygonVertexHit as Tag)
                    if (handle.Tag is HitType ht) _currentHit = ht;
                    else if (handle.Tag is PolygonVertexHit pvh) _currentHit = HitType.Body; // placeholder for polygon vertex
                    _reshapeShapeInfo = _activeShapeInfo;
                    BoundingBoxCanvas.CaptureMouse();
                    SetStatus("Reshape started.");
                    e.Handled = true;
                    return;
                }
            }

            // DELAYED DRAG: remember candidate shape under cursor, but don't start dragging yet.
            if (!_isDrawing)
            {
                _pendingShapeInfo = _shapeInfos.LastOrDefault(info =>
                    info.Shape.IsMouseOver || info.LabelBlock.IsMouseOver);

                if (_pendingShapeInfo != null)
                {
                    // don't set _isDraggingShape here; start drag when mouse moves while holding down and cursor is over shape
                    SetStatus("Hold and move to start dragging the shape.");
                    BoundingBoxCanvas.CaptureMouse();
                    e.Handled = true;
                    return;
                }
                else
                {
                    RemoveResizeHandles();
                }
            }

            // If drawing a polygon, add a new point
            var label = LabelComboBox.SelectedItem?.ToString();
            if (string.IsNullOrWhiteSpace(label))
            {
                SetStatus("Please select a label before drawing.");
                _mouseLeftDown = false;
                return;
            }

            if (_currentDrawingMode == DrawingMode.Polygon)
            {
                _isDrawing = true;
                StartPolygon(pt, label); // Adds a new point
                _mouseLeftDown = false;
                return;
            }

            // If drawing a rectangle
            if (_currentDrawingMode == DrawingMode.Rectangle)
            {
                _isDrawing = true;
                _dragStartPoint = pt;
                StartRectangle(pt, label);
                _mouseLeftDown = false;
            }
            else if(_currentDrawingMode==DrawingMode.FreePen)
            {
                _isDrawing = true;
                StartFreePen(pt,label); _mouseLeftDown = false;
            }

        }

        private void StartFreePen(Point pt, string label)
        {
            // Create visual polyline stroke and label (similar to StartRectangle)
            Color color = GetColorForClass(label);
            var brush = new SolidColorBrush(color);

            var polyline = new Polyline
            {
                Stroke = brush,
                StrokeThickness = 2,
                Points = new PointCollection { pt }
            };

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = brush,
                FontSize = 10,
                FontWeight = FontWeights.Normal,
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0)
            };

            Canvas.SetLeft(labelBlock, pt.X + 1);
            Canvas.SetTop(labelBlock, pt.Y + 1);

            var record = new AnnotationRecord
            {
                ImageName = System.IO.Path.GetFileName(_currentImagePath ?? ""),
                Label = label,
                AnnotationType = AnnotationType.FreePen,
                Points = new List<Point> { pt }
            };

            var info = new ShapeInfo
            {
                Shape = polyline,
                LabelBlock = labelBlock,
                Record = record,
                Metadata = new ShapeMetadata { Label = label, Type = AnnotationType.FreePen }
            };

            _currentDrawingShapeInfo = info;
            _shapeInfos.Add(info);

            BoundingBoxCanvas.Children.Add(polyline);
            BoundingBoxCanvas.Children.Add(labelBlock);
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

            // If user pressed mouse down previously, but drag hasn't started yet, start drag when moving into shape area while holding.
            if (_mouseLeftDown && !_isDraggingShape && _pendingShapeInfo != null && e.LeftButton == MouseButtonState.Pressed)
            {
                // Start dragging only when cursor is over the pending shape (or moved a small threshold)
                bool cursorOverShape = _pendingShapeInfo.Shape.IsMouseOver || _pendingShapeInfo.LabelBlock.IsMouseOver;
                double dx = pt.X - _mouseDownPoint.X;
                double dy = pt.Y - _mouseDownPoint.Y;
                double moved = Math.Sqrt(dx * dx + dy * dy);

                // Use either entering the shape or moving beyond a small movement threshold to start dragging
                if (cursorOverShape || moved > 3.0)
                {
                    _activeShapeInfo = _pendingShapeInfo;
                    _pendingShapeInfo = null;
                    _isDraggingShape = true;
                    _dragStartPoint = pt;
                    HighlightShape(_activeShapeInfo, true);

                    if (_activeShapeInfo.Shape is Polyline)
                        AddPolygonHandles(_activeShapeInfo);
                    else
                        AddResizeHandles(_activeShapeInfo);

                    BoundingBoxCanvas.CaptureMouse();
                    SetStatus("Shape selected for dragging.");
                    // continue to perform an immediate drag step below
                }
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

                if (_currentDrawingMode == DrawingMode.Rectangle)
                {
                    UpdateRectangle(clampedPt);
                }
                else if (_currentDrawingMode == DrawingMode.FreePen && _currentDrawingShapeInfo.Shape is Polyline poly)
                {
                    // Add point with simple thinning to reduce excessive points
                    var last = poly.Points.Count > 0 ? poly.Points[poly.Points.Count - 1] : new Point(double.NaN, double.NaN);
                    if (double.IsNaN(last.X) ||
                        Math.Abs(clampedPt.X - last.X) > 1.5 ||
                        Math.Abs(clampedPt.Y - last.Y) > 1.5)
                    {
                        poly.Points.Add(clampedPt);
                        // keep the annotation record in sync
                        _currentDrawingShapeInfo.Record.Points.Add(clampedPt);
                    }
                }
            }

            if (_activeHandle != null && _reshapeShapeInfo?.Shape is Polyline polyline &&
                _activeHandle.Tag is PolygonVertexHit vertexHit && e.LeftButton == MouseButtonState.Pressed)
            {
                Point clampedPt = ClampPointToImage(pt);
                polyline.Points[vertexHit.VertexIndex] = clampedPt;
                UpdateAnnotationRecordFromShape(_reshapeShapeInfo);
                RefreshHandles(_reshapeShapeInfo);
                return;
            }
        }

        private void BoundingBoxCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point pt = e.GetPosition(BoundingBoxCanvas);
            Point clampedPt = ClampPointToImage(pt);

            // reset pending state since mouse button released
            _mouseLeftDown = false;
            if (_pendingShapeInfo != null)
            {
                // user pressed but didn't move enough to start drag; just clear
                _pendingShapeInfo = null;
                BoundingBoxCanvas.ReleaseMouseCapture();
            }

            // Handle polygon vertex dragging
            if (_activeHandle != null && _reshapeShapeInfo != null)
            {
                BoundingBoxCanvas.ReleaseMouseCapture();
                if (IsShapeFullyInImage(_reshapeShapeInfo))
                {
                    UpdateAnnotationRecordFromShape(_reshapeShapeInfo);
                    RefreshAnnotations();

                    if (_reshapeShapeInfo.Shape is Polyline)
                        AddPolygonHandles(_reshapeShapeInfo);
                    else
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

            // Handle dragging of entire shape
            if (_isDraggingShape && _activeShapeInfo != null)
            {
                _isDraggingShape = false;
                BoundingBoxCanvas.ReleaseMouseCapture();
                if (IsShapeFullyInImage(_activeShapeInfo))
                {
                    UpdateAnnotationRecordFromShape(_activeShapeInfo);
                    RefreshAnnotations();
                    HighlightShape(_activeShapeInfo, false);

                    if (_activeShapeInfo.Shape is Polyline)
                        AddPolygonHandles(_activeShapeInfo);
                    else
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

            // Handle polygon drawing
            if (_isDrawing && _currentDrawingShapeInfo != null && _currentDrawingMode == DrawingMode.Polygon)
            {
                updatePolygon(clampedPt);
                return;
            }

            // Handle free-pen drawing
            
            if (_isDrawing && _currentDrawingShapeInfo != null && _currentDrawingMode == DrawingMode.FreePen)
            {
                FinalizeFreePen(clampedPt);
                return;
            }

            // Handle rectangle drawing
            if (_isDrawing && _currentDrawingShapeInfo != null && _currentDrawingMode == DrawingMode.Rectangle)
            {
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
                _currentDrawingShapeInfo = null;
            }
        }
        #endregion

        #region Drawing/Shape Helper Methods 

        private void StartRectangle(Point start, string label)
        {
            // Generate a random color based on the class name (label)
            Color color = GetColorForClass(label);

            var rect = new Rectangle
            {
                Stroke = new SolidColorBrush(color),
                StrokeThickness = 2
            };
            Canvas.SetLeft(rect, start.X);
            Canvas.SetTop(rect, start.Y);

            var labelBlock = new TextBlock
            {
                Text = label,
                Foreground = new SolidColorBrush(color),
                FontSize = 10,
                FontWeight = FontWeights.Normal,
                Background = Brushes.Transparent,
                Padding = new Thickness(0),
                Margin = new Thickness(0)
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

        // Utility: Generate a deterministic color for each class name
        private Color GetColorForClass(string className)
        {
            // Use a hash of the class name to pick a hue, but also vary saturation and lightness for more variety
            int hash = Math.Abs(className.GetHashCode());
            
            // Vary hue, saturation, and lightness for more distinct colors
            double hue = (hash % 360);
            double saturation = 0.7 + ((hash / 360) % 30) / 100.0; // 0.7 - 1.0
            double lightness = 0.5 + ((hash / 10800) % 20) / 100.0; // 0.5 - 0.7

            // Convert HSL to RGB
            double c = (1 - Math.Abs(2 * lightness - 1)) * saturation;
            double x = c * (1 - Math.Abs((hue / 60) % 2 - 1));
            double m = lightness - c / 2;
            double r1 = 0, g1 = 0, b1 = 0;

            if (hue < 60) { r1 = c; g1 = x; b1 = 0; }
            else if (hue < 120) { r1 = x; g1 = c; b1 = 0; }
            else if (hue < 180) { r1 = 0; g1 = c; b1 = x; }
            else if (hue < 240) { r1 = 0; g1 = x; b1 = c; }
            else if (hue < 300) { r1 = x; g1 = 0; b1 = c; }
            else { r1 = c; g1 = 0; b1 = x; }

            byte r = (byte)Math.Round((r1 + m) * 255);
            byte g = (byte)Math.Round((g1 + m) * 255);
            byte b = (byte)Math.Round((b1 + m) * 255);

            return Color.FromRgb(r, g, b);
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

        private void StartPolygon(Point start, string label)
        {
            if (_currentDrawingShapeInfo == null)
            {
                // Use class-based color for polygon and label
                Color color = GetColorForClass(label);
                var brush = new SolidColorBrush(color);

                var polyline = new Polyline
                {
                    Stroke = brush,
                    StrokeThickness = 2
                };

                var labelBlock = new TextBlock
                {
                    Text = label,
                    Foreground = brush,
                    FontSize = 10,
                    FontWeight = FontWeights.Normal,
                    Background = Brushes.Transparent,
                    Padding = new Thickness(0),
                    Margin = new Thickness(0)
                };

                Canvas.SetLeft(labelBlock, start.X + 1);
                Canvas.SetTop(labelBlock, start.Y + 1);

                var record = new AnnotationRecord
                {
                    ImageName = System.IO.Path.GetFileName(_currentImagePath ?? ""),
                    Label = label,
                    AnnotationType = AnnotationType.Polygon,
                    Points = new List<Point> { start }
                };

                var info = new ShapeInfo
                {
                    Shape = polyline,
                    LabelBlock = labelBlock,
                    Record = record,
                    Metadata = new ShapeMetadata { Label = label, Type = AnnotationType.Polygon }
                };

                _currentDrawingShapeInfo = info;
                _shapeInfos.Add(info);

                BoundingBoxCanvas.Children.Add(polyline);
                BoundingBoxCanvas.Children.Add(labelBlock);

                _currentPolygonPoints = new List<Point>();
            }

            if (!_currentPolygonPoints.Contains(start))
                _currentPolygonPoints.Add(start);

            if (_currentDrawingShapeInfo.Shape is Polyline currentPolyline)
            {
                currentPolyline.Points = new PointCollection(_currentPolygonPoints);
            }
        }
        private void updatePolygon(Point clampedPt)
        {
            if (_currentDrawingShapeInfo?.Shape is Polyline polyline)
            {
                // Replace last point with clamped position
                if (_currentPolygonPoints.Count > 0)
                {
                    _currentPolygonPoints[_currentPolygonPoints.Count - 1] = clampedPt;
                }
                else
                {
                    _currentPolygonPoints.Add(clampedPt);
                }

                polyline.Points = new PointCollection(_currentPolygonPoints);
            }
        }

        private void FinalizePolygon()
        {
            if (_currentDrawingShapeInfo != null && _currentDrawingShapeInfo.Shape is Polyline)
            {
                // Use class-based color for finalized polygon
                var color = GetColorForClass(_currentDrawingShapeInfo.Metadata.Label);
                var brush = new SolidColorBrush(color);

                var polygon = new Polygon
                {
                    Stroke = brush,
                    StrokeThickness = 2,
                    Fill = Brushes.Transparent,
                    Points = new PointCollection(_currentPolygonPoints)
                };
                BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.Shape);
                BoundingBoxCanvas.Children.Add(polygon);

                var first = _currentPolygonPoints.First();
                _currentDrawingShapeInfo.LabelBlock.Foreground = brush;
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
        #endregion

        #region Helper Methods for Bounds and Clamping

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
        #endregion

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

        #region Project management
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

            if (dialog.ShowDialog() != true) return;

            try
            {
                var json = File.ReadAllText(dialog.FileName);
                _currentProject = System.Text.Json.JsonSerializer.Deserialize<AnnotationProject>(json);

                if (_currentProject == null)
                {
                    SetStatus("Failed to load project.");
                    return;
                }

                // Load saved data
                var savedPaths = _currentProject.ImagePaths ?? new List<string>();
                var savedAnnotations = _currentProject.Annotations ?? new List<AnnotationRecord>();
                var savedLabels = _currentProject.ClassLabels ?? new List<string>();

                // Scan folder for new images
                var folderPath = System.IO.Path.GetDirectoryName(savedPaths.FirstOrDefault() ?? "");
                var allImages = Directory.Exists(folderPath)
                    ? Directory.GetFiles(folderPath)
                        .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                    f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                        .ToList()
                    : new List<string>();

                // Merge: keep existing annotations, add new images
                var knownImages = new HashSet<string>(savedPaths);
                var newImages = allImages.Where(img => !knownImages.Contains(img)).ToList();
                var mergedPaths = savedPaths.Concat(newImages).ToList();

                //// Add empty annotations using object initializer
                //savedAnnotations.AddRange(
                //    newImages.Select(img => new AnnotationRecord
                //    {
                //        ImagePath = img,
                //        Labels = new List<string>()
                //    })
                //);

                // Update project
                _currentProject.ImagePaths = mergedPaths;
                _currentProject.Annotations = savedAnnotations;
                _currentProject.ClassLabels = savedLabels;
                _currentProject.SelectedImageIndex = (_currentProject.SelectedImageIndex >= 0 &&
                                                      _currentProject.SelectedImageIndex < mergedPaths.Count)
                    ? _currentProject.SelectedImageIndex
                    : 0;

                // Sync local state
                _imagePaths = mergedPaths;
                Annotations = savedAnnotations;
                _currentImageIndex = _currentProject.SelectedImageIndex;

                LabelComboBox.Items.Clear();
                foreach (var label in savedLabels)
                    LabelComboBox.Items.Add(label);

                ProjectSession.CurrentProject = _currentProject;
                ProjectSession.Annotations = Annotations;
                ProjectSession.ImagePaths = _imagePaths;
                ProjectSession.CurrentImageIndex = _currentImageIndex;
                ProjectSession.CurrentImagePath = _imagePaths.Count > 0 ? _imagePaths[_currentImageIndex] : null;

                _ = LoadImageAtIndex(_currentImageIndex);
                SetStatus($"Project opened. {savedLabels.Count} classes, {savedAnnotations.Count} images.");
                ShowMainContentPanel();
            }
            catch (Exception ex)
            {
                SetStatus($"Error opening project: {ex.Message}");
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
        private void EditAnnotation_Click(object sender, RoutedEventArgs e)
        {
            // TODO: Implement the logic to edit the selected annotation.
            // For now, you can show a message box as a placeholder.
            MessageBox.Show("Edit annotation clicked.");
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
                // Use the same color logic as Start/Finalize: only class name
                Color color = GetColorForClass(ann.Label);
                var brush = new SolidColorBrush(color);

                TextBlock label = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(ann.Label) ? "(No Class)" : ann.Label,
                    FontWeight = FontWeights.Normal,
                    FontSize = 10,
                    Padding = new Thickness(2, 0, 2, 0),
                    Background = Brushes.Transparent,
                    Foreground = brush
                };

                if (ann.AnnotationType == AnnotationType.Rectangle && ann.Points.Count == 2)
                {
                    var x = Math.Min(ann.Points[0].X, ann.Points[1].X);
                    var y = Math.Min(ann.Points[0].Y, ann.Points[1].Y);
                    var w = Math.Abs(ann.Points[1].X - ann.Points[0].X);
                    var h = Math.Abs(ann.Points[1].Y - ann.Points[0].Y);

                    shape = new Rectangle
                    {
                        Stroke = brush,
                        StrokeThickness = 2,
                        Width = w,
                        Height = h
                    };
                    Canvas.SetLeft(shape, x);
                    Canvas.SetTop(shape, y);
                    Canvas.SetLeft(label, x + 1);
                    Canvas.SetTop(label, y + 1);
                }
                else if (ann.AnnotationType == AnnotationType.Polygon && ann.Points.Count > 2)
                {
                    shape = new Polygon
                    {
                        Stroke = brush,
                        StrokeThickness = 2,
                        Fill = Brushes.Transparent,
                        Points = new PointCollection(ann.Points)
                    };
                    var first = ann.Points.First();
                    Canvas.SetLeft(label, first.X + 1);
                    Canvas.SetTop(label, first.Y + 1);
                }
                else if (ann.AnnotationType == AnnotationType.FreePen && ann.Points.Count > 1)
                {
                    shape = new Polyline
                    {
                        Stroke = brush,
                        StrokeThickness = 2,
                        Points = new PointCollection(ann.Points)
                    };
                    var first = ann.Points.First();
                    Canvas.SetLeft(label, first.X + 1);
                    Canvas.SetTop(label, first.Y + 1);
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

        // Utility: Generate a deterministic color for each class name
        //private Color GetColorForClass(string className)
        //{
        //    int hash = className.GetHashCode();
        //    byte r = (byte)(128 + (hash & 0x7F));
        //    byte g = (byte)(128 + ((hash >> 8) & 0x7F));
        //    byte b = (byte)(128 + ((hash >> 16) & 0x7F));
        //    return Color.FromRgb(r, g, b);
        //}

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
                    $"Yolo5Export_{DateTime.Now:yyyyMMdd}"
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
                    $"Yolo8Export_{DateTime.Now:yyyyMMdd}"
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
        private void ExportYoloV5_OBB_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || _currentProject.ImagePaths.Count == 0)
            {
                SetStatus("No project or images to export.");
                return;
            }

            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select output folder for YOLOv5_OBB export",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() == true)
            {
                string outputFolder = System.IO.Path.Combine(
                    dialog.SelectedPath,
                    $"Yolo5OBBExport_{DateTime.Now:yyyyMMdd_HHmmss}"
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
                    exportFormat: YoloExportFormat.YoloV5_OBB
                );

                SetStatus("YOLOv5_OBB export complete (train/val/test).");
            }
            else
            {
                SetStatus("YOLOv5_OBB export canceled.");
            }
        }
        private void ExportYoloV8_OBB_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || _currentProject.ImagePaths.Count == 0)
            {
                SetStatus("No project or images to export.");
                return;
            }

            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select output folder for YOLOv8_OBB export",
                UseDescriptionForTitle = true

            };

            if (dialog.ShowDialog() == true)
            {
                string outputFolder = System.IO.Path.Combine(
                    dialog.SelectedPath,
                    $"Yolo8OBBExport_{DateTime.Now:yyyyMMdd_HHmmss}"
                );
                //I want to know annotation type of current project before convert and data structure befor convert
                
                _currentProject =Convert2RotatedBox(_currentProject);// this fuction convert all box annotation to rotated box annotation(convert rectangle or polygon to rotated box)

                Logger.Instance.LogInfo("Example OBB label structure: x1,y1,x2,y2,x3,y3,x4,y4");

                for (int i = 0; i < Math.Min(5, _currentProject.Annotations.Count); i++)
                {
                    var a = _currentProject.Annotations[i];
                    if (a.AnnotationType == AnnotationType.RotatedBox && a.RawValues != null && a.RawValues.Count == 8)
                    {
                        Logger.Instance.LogInfo(
                            $"Label: {a.Label}, OBB 8-Data: {string.Join(",", a.RawValues.Select(v => v.ToString("F2")))}"
                        );
                    }

                }
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
                    exportFormat: YoloExportFormat.YoloV8_OBB
                );

                SetStatus("YOLOv8_OBB export complete (train/val/test).");
            }
            else
            {
                SetStatus("YOLOv8_OBB export canceled.");
            }
        }
        private void ExportYoloV8_SEG_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || _currentProject.ImagePaths.Count == 0)
            {
                SetStatus("No project or images to export.");
                return;
            }
            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select output folder for YOLOv8_SEG export",
                UseDescriptionForTitle = true
            };
            if (dialog.ShowDialog() == true)
            {
                string outputFolder = System.IO.Path.Combine(
                    dialog.SelectedPath,
                    $"Yolo8SEGExport_{DateTime.Now:yyyyMMdd_HHmmss}"
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
                    exportFormat: YoloExportFormat.YoloV8_SEG
                );
                SetStatus("YOLOv8_SEG export complete (train/val/test).");
            }
            else
            {
                SetStatus("YOLOv8_SEG export canceled.");
            }
        }
        // Converts all rectangle or polygon annotations in the project to rotated box annotations.
        private AnnotationProject Convert2RotatedBox(AnnotationProject project)
        {
            if (project == null) return null;

            var rotatedAnnotations = new List<AnnotationRecord>();

            foreach (var ann in project.Annotations)
            {
                List<double> values = null;

                if (ann.AnnotationType == AnnotationType.Rectangle && ann.Points.Count == 2)
                {
                    var p0 = ann.Points[0];
                    var p1 = ann.Points[1];

                    double x1 = Math.Min(p0.X, p1.X);
                    double y1 = Math.Min(p0.Y, p1.Y);
                    double x2 = Math.Max(p0.X, p1.X);
                    double y2 = Math.Max(p0.Y, p1.Y);

                    double cx = (x1 + x2) / 2.0;
                    double cy = (y1 + y2) / 2.0;
                    double w = Math.Abs(x2 - x1);
                    double h = Math.Abs(y2 - y1);
                    double angle = 0.0;

                    values = GetRotatedBoxAs8Values(cx, cy, w, h, angle);
                }
                else if (ann.AnnotationType == AnnotationType.Polygon && ann.Points.Count > 2)
                {
                    var rect = GetMinAreaRect(ann.Points);
                    values = GetRotatedBoxAs8Values(rect.Center.X, rect.Center.Y, rect.Size.Width, rect.Size.Height, rect.Angle);
                }

                if (values != null && values.Count == 8)
                {
                    var points = new List<Point>
            {
                new Point((int)values[0], (int)values[1]),
                new Point((int)values[2], (int)values[3]),
                new Point((int)values[4], (int)values[5]),
                new Point((int)values[6], (int)values[7])
            };

                    rotatedAnnotations.Add(new AnnotationRecord
                    {
                        ImageName = ann.ImageName,
                        Label = ann.Label,
                        AnnotationType = AnnotationType.RotatedBox,
                        RawValues = values,
                        Points = points
                    });
                }
                else
                {
                    rotatedAnnotations.Add(ann);
                }
            }

            return new AnnotationProject
            {
                ProjectName = project.ProjectName,
                ImagePaths = project.ImagePaths,
                ClassLabels = project.ClassLabels,
                Annotations = rotatedAnnotations,
                SelectedImageIndex = project.SelectedImageIndex
            };
        }
        private MinAreaRect GetMinAreaRect(List<Point> points)
        {
            if (points == null || points.Count < 3)
                throw new ArgumentException("At least 3 points are required for minimum area rectangle.");

            // Step 1: Compute centroid
            double cx = points.Average(p => p.X);
            double cy = points.Average(p => p.Y);

            // Step 2: Compute covariance matrix
            double sumXX = 0, sumXY = 0, sumYY = 0;
            foreach (var p in points)
            {
                double dx = p.X - cx;
                double dy = p.Y - cy;
                sumXX += dx * dx;
                sumXY += dx * dy;
                sumYY += dy * dy;
            }

            // Step 3: Compute orientation using PCA (eigenvector of largest eigenvalue)
            double covXX = sumXX / points.Count;
            double covXY = sumXY / points.Count;
            double covYY = sumYY / points.Count;

            double theta = 0.5 * Math.Atan2(2 * covXY, covXX - covYY); // angle in radians
            double cosT = Math.Cos(theta);
            double sinT = Math.Sin(theta);

            // Step 4: Rotate all points to align with principal axis
            var rotated = points.Select(p =>
            {
                double dx = p.X - cx;
                double dy = p.Y - cy;
                return new Point(
                    dx * cosT + dy * sinT,
                    -dx * sinT + dy * cosT
                );
            }).ToList();

            // Step 5: Get bounding box in rotated space
            double minX = rotated.Min(p => p.X);
            double maxX = rotated.Max(p => p.X);
            double minY = rotated.Min(p => p.Y);
            double maxY = rotated.Max(p => p.Y);

            double width = maxX - minX;
            double height = maxY - minY;

            return new MinAreaRect
            {
                Center = new Point(cx, cy),
                Size = new Size(width, height),
                Angle = theta * 180.0 / Math.PI // convert to degrees
            };
        }
        private struct MinAreaRect
{
    public Point Center;
    public Size Size;
    public double Angle; // In degrees
}
        private List<double> GetRotatedBoxAs8Values(double cx, double cy, double w, double h, double angleDegrees)
        {
            double angle = angleDegrees * Math.PI / 180.0;
            double cosA = Math.Cos(angle);
            double sinA = Math.Sin(angle);

            double w2 = w / 2.0;
            double h2 = h / 2.0;

            var corners = new List<Point>
    {
        new Point(cx - w2 * cosA + h2 * sinA, cy - w2 * sinA - h2 * cosA), // top-left
        new Point(cx + w2 * cosA + h2 * sinA, cy + w2 * sinA - h2 * cosA), // top-right
        new Point(cx + w2 * cosA - h2 * sinA, cy + w2 * sinA + h2 * cosA), // bottom-right
        new Point(cx - w2 * cosA - h2 * sinA, cy - w2 * sinA + h2 * cosA)  // bottom-left
    };

            return corners.SelectMany(p => new List<double> { p.X, p.Y }).ToList();
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
        #endregion

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

        #region Image selection
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
            ImageProgressText.Text = $"Image {index + 1} of {_imagePaths.Count} ({(int)(((index + 1) * 100.0) / _imagePaths.Count)}%) - {System.IO.Path.GetFileName(imagePath)}";

            _isDrawing = false;
            _currentDrawingShapeInfo = null;
            _currentPolygonPoints.Clear();
            BoundingBoxCanvas.ReleaseMouseCapture();

            SetStatus($"Loaded image {index + 1} of {_imagePaths.Count} ({System.IO.Path.GetFileName(imagePath)}). Ready to draw.");
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
        #endregion

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
    }
}
