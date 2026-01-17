using ClearEngine.Logging;
using Microsoft.VisualBasic.Logging;
using Ookii.Dialogs.Wpf;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static System.Windows.Forms.Design.AxImporter;

// Explicitly disambiguate WPF Point to avoid conflicts with other Point types.
using SWPoint = System.Windows.Point;
using SWPath = System.IO.Path;

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

        private List<SWPoint> _currentPolygonPoints = new();
        private ShapeInfo? _currentDrawingShapeInfo = null;
        private bool _isDrawing = false;

        private ShapeInfo? _activeShapeInfo = null;
        private SWPoint _dragStartPoint;
        private bool _isDraggingShape = false;

        // NEW: pending selection when user presses left button but drag should start only after moving into shape
        private ShapeInfo? _pendingShapeInfo = null;
        private SWPoint _mouseDownPoint;
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
        private List<ShapeInfo> _shape_infos => _shapeInfos;

        // Small processing mode state for the floating menu
        private enum ImageProcessMode { None, Grayscale, Edges, Contours, ContourRects }
        private ImageProcessMode _selectedProcessingMode = ImageProcessMode.None;

        // Holds the last processed preview (assigned when applying processing).
        private BitmapSource? _lastProcessedImage;
        private bool _isDraggingProcessingMenu = false;
        private SWPoint _processingMenuMouseDownPoint;
        private double _processingMenuStartX = 0;
        private double _processingMenuStartY = 0;
        // --- Added fields for draggable Class Stats panel ---
        private bool _isDraggingClassStats = false;
        private SWPoint _classStatsMouseDownPoint;
        private double _classStatsStartX = 0;
        private double _classStatsStartY = 0;
        // --- Added fields for right-side collapse state ---
        private bool _rightPanelCollapsed = false;
        private GridLength _savedRightColumnWidth = new GridLength(400); // default width when expanded
        private bool _classStatsCollapsed = false; // Add this member
                                                   // add near other private fields
        private bool _classStatsOverlayShown = false;
        public DataSetPage()
        {
            InitializeComponent();
            this.Focusable = true;

            // Ensure processing panel is hidden by default and menu unchecked.
            if (FloatingProcessingMenu != null)
                FloatingProcessingMenu.Visibility = Visibility.Collapsed;
            if (ToggleProcessingPanelMenu != null)
                ToggleProcessingPanelMenu.IsChecked = false;

            this.Loaded += async (s, e) =>
            {
                this.Focus();
                SetStatus("Ready");
                Logger.Instance.LogInfo("DataSetPage loaded.");

                // Defensive: ensure processing panel remains hidden on load (in case XAML or styles changed it)
                if (FloatingProcessingMenu != null)
                    FloatingProcessingMenu.Visibility = Visibility.Collapsed;
                if (ToggleProcessingPanelMenu != null)
                    ToggleProcessingPanelMenu.IsChecked = false;
                
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

                    // ensure stats reflect the loaded project
                    UpdateClassStats();
                    CheckAutoLabelEnable();
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
            BoundingBoxCanvas.MouseDown += BoundingBoxCanvas_MouseDown;
            BoundingBoxCanvas.MouseWheel += BoundingBoxCanvas_MouseWheel;
            
            if (ApplyProcessingButton != null) ApplyProcessingButton.Click += ApplyProcessingButton_Click;

            BoundingBoxCanvas.ContextMenuOpening += BoundingBoxCanvas_ContextMenuOpening;

            // After InitializeComponent();
            if (FloatingProcessingMenu != null)
            {
                FloatingProcessingMenu.PreviewMouseLeftButtonDown += FloatingProcessingMenu_MouseLeftButtonDown;
                FloatingProcessingMenu.PreviewMouseMove += FloatingProcessingMenu_MouseMove;
                FloatingProcessingMenu.PreviewMouseLeftButtonUp += FloatingProcessingMenu_MouseLeftButtonUp;
            }
            // --- Wire up handlers in the constructor (add inside the DataSetPage() constructor) ---
            // After existing FloatingProcessingMenu wiring add:
            if (ClassStatsPanel != null)
            {
                ClassStatsPanel.PreviewMouseLeftButtonDown += ClassStatsPanel_MouseLeftButtonDown;
                ClassStatsPanel.PreviewMouseMove += ClassStatsPanel_MouseMove;
                ClassStatsPanel.PreviewMouseLeftButtonUp += ClassStatsPanel_MouseLeftButtonUp;
            }

            // wire label combo selection and ensure badge updates on load
            LabelComboBox.SelectionChanged += LabelComboBox_SelectionChanged;

            // inside the DataSetPage() constructor (after InitializeComponent();)
            this.PreviewKeyDown += DataSetPage_PreviewKeyDown;
        }
        // Add these members/methods inside the DataSetPage class

        // Convert current mouse position into the canvas/image logical coordinates (undo ZoomTransform).
        private SWPoint GetMousePointUnscaled()
        {
            if (BoundingBoxCanvas == null)
                return new SWPoint(0, 0);

            var p = Mouse.GetPosition(BoundingBoxCanvas);

            // If you have a ScaleTransform named ZoomTransform in XAML, invert it here.
            // If ZoomTransform is null or other transforms exist, return raw position.
            if (ZoomTransform != null)
            {
                double sx = ZoomTransform.ScaleX;
                double sy = ZoomTransform.ScaleY;
                double cx = ZoomTransform.CenterX;
                double cy = ZoomTransform.CenterY;

                if (Math.Abs(sx) > 1e-6 && Math.Abs(sy) > 1e-6)
                {
                    double x = (p.X - cx) / sx + cx;
                    double y = (p.Y - cy) / sy + cy;
                    return new SWPoint(x, y);
                }
            }

            return p;
        }

        // Tunnelled key handler: runs before focused controls receive the key.
        // Consumes Space while drawing so controls/layout won't react and move the image.
        private void DataSetPage_PreviewKeyDown(object? sender, KeyEventArgs e)
        {
            try
            {
                if (e.Key == Key.Space && _isDrawing && _currentDrawingShapeInfo != null)
                {
                    // Use unscaled pointer so zoom/transform doesn't cause coordinate jumps.
                    var rawPos = GetMousePointUnscaled();
                    var clamped = ClampPointToImage(rawPos);

                    if (_currentDrawingMode == DrawingMode.Polygon)
                    {
                        if (_currentPolygonPoints.Count > 0)
                            _currentPolygonPoints[_currentPolygonPoints.Count - 1] = clamped;
                        else
                            _currentPolygonPoints.Add(clamped);

                        if (_currentDrawingShapeInfo.Shape is Polyline poly)
                            poly.Points = new PointCollection(_currentPolygonPoints);
                    }
                    else if (_currentDrawingMode == DrawingMode.FreePen)
                    {
                        if (_currentDrawingShapeInfo.Shape is Polyline poly)
                        {
                            if (poly.Points.Count > 0)
                                poly.Points[poly.Points.Count - 1] = clamped;
                            else
                                poly.Points.Add(clamped);
                            _currentDrawingShapeInfo.Record.Points = poly.Points.ToList();
                        }
                    }
                    else if (_currentDrawingMode == DrawingMode.Rectangle)
                    {
                        UpdateRectangle(clamped);
                    }

                    // Finish and prevent other controls from acting on Space (collapse/activate/etc).
                    FinishDrawing_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"DataSetPage_PreviewKeyDown error: {ex}");
            }
        }
        private void LabelComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSelectedClassBadge();
        }
        // New: shows/hides/updates the small badge at top-left of the image
        private void UpdateSelectedClassBadge()
        {
            Dispatcher.Invoke(() =>
            {
                if (SelectedClassBadge == null || LabelComboBox == null || LabelingImage == null)
                    return;

                var selected = LabelComboBox.SelectedItem?.ToString();
                bool hasImage = LabelingImage.Source != null;

                if (!hasImage || string.IsNullOrWhiteSpace(selected))
                {
                    SelectedClassBadge.Visibility = Visibility.Collapsed;
                    return;
                }

                string modeName = _currentDrawingMode switch
                {
                    DrawingMode.FreePen => "Free Pen",
                    DrawingMode.Rectangle => "Rectangle",
                    DrawingMode.Polygon => "Polygon",
                    _ => _currentDrawingMode.ToString()
                };

                SelectedClassBadge.Text = $"{selected} : {modeName}";
                SelectedClassBadge.Visibility = Visibility.Visible;
            });
        }
        // --- Toggle handler for right-side annotation panel (placed inside DataSetPage class) ---
        // replace existing RightPanelToggleButton_Click with this
        private void RightPanelToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (RightColumn == null || RightPanelToggleButton == null)
                    return;

                // Capture mouse position relative to the canvas before layout change so we can correct drawing state after.
                SWPoint mouseBefore = new SWPoint(0, 0);
                try
                {
                    if (BoundingBoxCanvas != null)
                        mouseBefore = Mouse.GetPosition(BoundingBoxCanvas);
                }
                catch { }

                if (!_rightPanelCollapsed)
                {
                    // collapse right panel
                    _savedRightColumnWidth = RightColumn.Width;
                    RightColumn.Width = new GridLength(0);
                    if (AnnotationControlsPanel != null)
                        AnnotationControlsPanel.Visibility = Visibility.Collapsed;

                    // show floating class stats overlay
                    if (ClassStatsPanel != null)
                    {
                        ClassStatsPanel.Visibility = Visibility.Visible;
                        if (ClassStatsBody != null) ClassStatsBody.Visibility = Visibility.Visible;
                        if (ClassStatsToggleButton != null) ClassStatsToggleButton.Content = "▾";
                        _classStatsOverlayShown = true;
                    }

                    RightPanelToggleButton.Content = "▶";
                    RightPanelToggleButton.ToolTip = "Expand Controls";
                    _rightPanelCollapsed = true;
                    SetStatus("Right panel collapsed. Class statistics shown.");
                }
                else
                {
                    // expand right panel
                    RightColumn.Width = _savedRightColumnWidth;
                    if (AnnotationControlsPanel != null)
                        AnnotationControlsPanel.Visibility = Visibility.Visible;

                    RightPanelToggleButton.Content = "◀";
                    RightPanelToggleButton.ToolTip = "Collapse Controls";
                    _rightPanelCollapsed = false;

                    SetStatus("Right panel expanded.");
                }

                // Force layout update and compute mouse displacement caused by the layout change.
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        if (BoundingBoxCanvas != null)
                        {
                            BoundingBoxCanvas.UpdateLayout();
                            var mouseAfter = Mouse.GetPosition(BoundingBoxCanvas);
                            double dx = mouseAfter.X - mouseBefore.X;
                            double dy = mouseAfter.Y - mouseBefore.Y;

                            // If layout change moved the canvas under the cursor, adjust internal state so drawing/dragging doesn't "slip".
                            if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
                            {
                                // Adjust pending / mouse-down reference used to begin a drag
                                if (_mouseLeftDown)
                                {
                                    _mouseDownPoint = new SWPoint(_mouseDownPoint.X + dx, _mouseDownPoint.Y + dy);
                                }

                                // Adjust dragging start so the current drag remains smooth
                                if (_isDraggingShape)
                                {
                                    _dragStartPoint = new SWPoint(_dragStartPoint.X + dx, _dragStartPoint.Y + dy);
                                }

                                // If user is drawing, shift the current visual and the stored start point(s)
                                if (_isDrawing && _currentDrawingShapeInfo != null)
                                {
                                    // Shift visual shape
                                    var shape = _currentDrawingShapeInfo.Shape;
                                    if (shape is Rectangle rect)
                                    {
                                        Canvas.SetLeft(rect, Canvas.GetLeft(rect) + dx);
                                        Canvas.SetTop(rect, Canvas.GetTop(rect) + dy);

                                        // Update record points (two points for rectangle)
                                        if (_currentDrawingShapeInfo.Record?.Points != null && _currentDrawingShapeInfo.Record.Points.Count >= 1)
                                        {
                                            for (int i = 0; i < _currentDrawingShapeInfo.Record.Points.Count; i++)
                                            {
                                                var p = _currentDrawingShapeInfo.Record.Points[i];
                                                _currentDrawingShapeInfo.Record.Points[i] = new SWPoint(p.X + dx, p.Y + dy);
                                            }
                                        }

                                        // Move label
                                        Canvas.SetLeft(_currentDrawingShapeInfo.LabelBlock, Canvas.GetLeft(_currentDrawingShapeInfo.LabelBlock) + dx);
                                        Canvas.SetTop(_currentDrawingShapeInfo.LabelBlock, Canvas.GetTop(_currentDrawingShapeInfo.LabelBlock) + dy);
                                    }
                                    else if (shape is Polyline polyline)
                                    {
                                        // Shift each point
                                        var pts = polyline.Points;
                                        for (int i = 0; i < pts.Count; i++)
                                            pts[i] = new Point(pts[i].X + dx, pts[i].Y + dy);
                                        polyline.Points = new PointCollection(pts);

                                        if (_currentDrawingShapeInfo.Record?.Points != null)
                                        {
                                            for (int i = 0; i < _currentDrawingShapeInfo.Record.Points.Count; i++)
                                            {
                                                var p = _currentDrawingShapeInfo.Record.Points[i];
                                                _currentDrawingShapeInfo.Record.Points[i] = new SWPoint(p.X + dx, p.Y + dy);
                                            }
                                        }

                                        // Move label (use existing offset)
                                        Canvas.SetLeft(_currentDrawingShapeInfo.LabelBlock, Canvas.GetLeft(_currentDrawingShapeInfo.LabelBlock) + dx);
                                        Canvas.SetTop(_currentDrawingShapeInfo.LabelBlock, Canvas.GetTop(_currentDrawingShapeInfo.LabelBlock) + dy);
                                    }
                                    else if (shape is Polygon polygon)
                                    {
                                        var pts = polygon.Points;
                                        for (int i = 0; i < pts.Count; i++)
                                            pts[i] = new Point(pts[i].X + dx, pts[i].Y + dy);
                                        polygon.Points = new PointCollection(pts);

                                        if (_currentDrawingShapeInfo.Record?.Points != null)
                                        {
                                            for (int i = 0; i < _currentDrawingShapeInfo.Record.Points.Count; i++)
                                            {
                                                var p = _currentDrawingShapeInfo.Record.Points[i];
                                                _currentDrawingShapeInfo.Record.Points[i] = new SWPoint(p.X + dx, p.Y + dy);
                                            }
                                        }

                                        Canvas.SetLeft(_currentDrawingShapeInfo.LabelBlock, Canvas.GetLeft(_currentDrawingShapeInfo.LabelBlock) + dx);
                                        Canvas.SetTop(_currentDrawingShapeInfo.LabelBlock, Canvas.GetTop(_currentDrawingShapeInfo.LabelBlock) + dy);
                                    }

                                    // Refresh handles for consistency
                                    RefreshHandles(_currentDrawingShapeInfo);
                                }

                                // If handles exist for an active shape, shift them too
                                if (_handles != null && _handles.Count > 0)
                                {
                                    foreach (var h in _handles)
                                    {
                                        Canvas.SetLeft(h, Canvas.GetLeft(h) + dx);
                                        Canvas.SetTop(h, Canvas.GetTop(h) + dy);
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"RightPanelToggleButton post-layout adjust error: {ex}");
                    }
                });
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RightPanelToggleButton_Click error: {ex}");
            }
        }
        private void FloatingProcessingMenu_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (FloatingProcessingMenu == null || BoundingBoxCanvas == null) return;

                // Record mouse down relative to the bounding canvas (drag reference).
                _processingMenuMouseDownPoint = e.GetPosition(BoundingBoxCanvas);

                // Ensure a TranslateTransform exists for movement.
                if (FloatingProcessingMenu.RenderTransform is not TranslateTransform)
                    FloatingProcessingMenu.RenderTransform = new TranslateTransform();

                var tt = (TranslateTransform)FloatingProcessingMenu.RenderTransform;
                _processingMenuStartX = tt.X;
                _processingMenuStartY = tt.Y;

                // Don't capture here — begin capture only once movement passes threshold so normal clicks still work.
            }
            catch { /* non-critical */ }
        }
        // --- Handlers for dragging the ClassStats panel ---
        private void ClassStatsPanel_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (ClassStatsPanel == null || BoundingBoxCanvas == null) return;

                // Record starting point relative to the canvas
                _classStatsMouseDownPoint = e.GetPosition(BoundingBoxCanvas);

                // Ensure a TranslateTransform exists for movement.
                if (ClassStatsPanel.RenderTransform is not TranslateTransform)
                    ClassStatsPanel.RenderTransform = new TranslateTransform();

                var tt = (TranslateTransform)ClassStatsPanel.RenderTransform;
                _classStatsStartX = tt.X;
                _classStatsStartY = tt.Y;

                // Do not capture yet; capture once movement passes threshold so clicks still work.
            }
            catch { /* non-critical */ }
        }

        private void ClassStatsPanel_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (ClassStatsPanel == null || BoundingBoxCanvas == null) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;

                var pos = e.GetPosition(BoundingBoxCanvas);
                double dx = pos.X - _classStatsMouseDownPoint.X;
                double dy = pos.Y - _classStatsMouseDownPoint.Y;

                // Begin drag after small threshold to avoid interfering with clicks.
                if (!_isDraggingClassStats)
                {
                    if (Math.Sqrt(dx * dx + dy * dy) < 3.0) return;
                    _isDraggingClassStats = true;
                    ClassStatsPanel.CaptureMouse();
                }

                if (ClassStatsPanel.RenderTransform is not TranslateTransform)
                    ClassStatsPanel.RenderTransform = new TranslateTransform();

                var tt = (TranslateTransform)ClassStatsPanel.RenderTransform;
                double newX = _classStatsStartX + dx;
                double newY = _classStatsStartY + dy;

                // Compute reasonable bounds based on canvas size and panel size to avoid drifting far off-screen.
                double canvasW = Math.Max(1.0, BoundingBoxCanvas.ActualWidth);
                double canvasH = Math.Max(1.0, BoundingBoxCanvas.ActualHeight);
                double panelW = Math.Max(1.0, ClassStatsPanel.ActualWidth);
                double panelH = Math.Max(1.0, ClassStatsPanel.ActualHeight);

                // Allow movement within a generous range but clamp to keep panel visible
                double marginFactor = 0.9;
                double minX = -canvasW * marginFactor;
                double maxX = canvasW * marginFactor;
                double minY = -canvasH * marginFactor;
                double maxY = canvasH * marginFactor;

                tt.X = Math.Max(minX, Math.Min(maxX, newX));
                tt.Y = Math.Max(minY, Math.Min(maxY, newY));
            }
            catch { /* non-critical */ }
        }

        private void ClassStatsPanel_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDraggingClassStats)
                {
                    _isDraggingClassStats = false;
                    if (ClassStatsPanel != null && ClassStatsPanel.IsMouseCaptured)
                        ClassStatsPanel.ReleaseMouseCapture();
                    e.Handled = true;
                }
            }
            catch { /* non-critical */ }
        }
        private void FloatingProcessingMenu_MouseMove(object sender, MouseEventArgs e)
        {
            try
            {
                if (FloatingProcessingMenu == null || BoundingBoxCanvas == null) return;
                if (e.LeftButton != MouseButtonState.Pressed) return;

                var pos = e.GetPosition(BoundingBoxCanvas);
                double dx = pos.X - _processingMenuMouseDownPoint.X;
                double dy = pos.Y - _processingMenuMouseDownPoint.Y;

                // Start drag after small threshold to avoid interfering with clicks.
                if (!_isDraggingProcessingMenu)
                {
                    if (Math.Sqrt(dx * dx + dy * dy) < 3.0) return;
                    _isDraggingProcessingMenu = true;
                    FloatingProcessingMenu.CaptureMouse();
                }

                // Ensure RenderTransform exists and is a TranslateTransform
                if (FloatingProcessingMenu.RenderTransform is not TranslateTransform)
                    FloatingProcessingMenu.RenderTransform = new TranslateTransform();

                var tt = (TranslateTransform)FloatingProcessingMenu.RenderTransform;
                double newX = _processingMenuStartX + dx;
                double newY = _processingMenuStartY + dy;

                // Allow moving left of initial position: use symmetric clamping similar to Thumb drag.
                // Compute reasonable bounds based on canvas size and menu size.
                double canvasW = Math.Max(1.0, BoundingBoxCanvas.ActualWidth);
                double canvasH = Math.Max(1.0, BoundingBoxCanvas.ActualHeight);
                double menuW = Math.Max(1.0, FloatingProcessingMenu.ActualWidth);
                double menuH = Math.Max(1.0, FloatingProcessingMenu.ActualHeight);

                // Allow the menu to move roughly within the visible canvas area (with small margin).
                double marginFactor = 0.9;
                double minX = -canvasW * marginFactor;
                double maxX = canvasW * marginFactor;
                double minY = -canvasH * marginFactor;
                double maxY = canvasH * marginFactor;

                // Clamp values so the menu doesn't drift far off-screen.
                tt.X = Math.Max(minX, Math.Min(maxX, newX));
                tt.Y = Math.Max(minY, Math.Min(maxY, newY));
            }
            catch { /* non-critical */ }
        }

        private void FloatingProcessingMenu_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            try
            {
                if (_isDraggingProcessingMenu)
                {
                    _isDraggingProcessingMenu = false;
                    if (FloatingProcessingMenu != null && FloatingProcessingMenu.IsMouseCaptured)
                        FloatingProcessingMenu.ReleaseMouseCapture();
                    e.Handled = true;
                }
            }
            catch { /* non-critical */ }
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

            UpdateSelectedClassBadge();
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
        private Ellipse CreateHandle(SWPoint point)
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
            // Only handle right-click when we are actively drawing and the current visual is a polyline
            if (!_isDrawing || _currentDrawingShapeInfo?.Shape is not Polyline poly)
                return;

            var clamped = ClampPointToImage(e.GetPosition(BoundingBoxCanvas));

            // POLYGON: update last point and finalize when there are >= 3 points
            if (_currentDrawingMode == DrawingMode.Polygon)
            {
                if (_currentPolygonPoints.Count > 0)
                    _currentPolygonPoints[_currentPolygonPoints.Count - 1] = clamped;
                else
                    _currentPolygonPoints.Add(clamped);

                if (_currentPolygonPoints.Count > 2)
                {
                    poly.Points = new PointCollection(_currentPolygonPoints);
                    FinalizePolygon();
                }
                else
                {
                    // Cancel incomplete polygon
                    BoundingBoxCanvas.Children.Remove(poly);
                    if (_currentDrawingShapeInfo?.LabelBlock != null)
                        BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                    _shapeInfos.Remove(_currentDrawingShapeInfo);
                    _currentDrawingShapeInfo = null;
                    _currentPolygonPoints.Clear();
                    _isDrawing = false;
                    SetStatus("Polygon requires at least 3 points. Drawing canceled.");
                }

                e.Handled = true;
                return;
            }

            // FREE PEN: finalize stroke — use the actual poly.Points / record (not the polygon buffer)
            if (_currentDrawingMode == DrawingMode.FreePen)
            {
                // Make sure final point equals clamped mouse pos
                if (poly.Points.Count > 0)
                    poly.Points[poly.Points.Count - 1] = clamped;
                else
                    poly.Points.Add(clamped);

                // Sync authoritative record with the visual points
                _currentDrawingShapeInfo.Record.Points = poly.Points.ToList();

                if (_currentDrawingShapeInfo.Record.Points.Count > 1)
                {
                    // FinalizeFreePen expects the final clamped point and will
                    // add the annotation, save undo state and refresh the UI.
                    FinalizeFreePen(clamped);
                }
                else
                {
                    // Discard too-short stroke and clean up safely
                    BoundingBoxCanvas.Children.Remove(poly);
                    if (_currentDrawingShapeInfo?.LabelBlock != null)
                        BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                    if (_shapeInfos.Contains(_currentDrawingShapeInfo))
                        _shape_infos.Remove(_currentDrawingShapeInfo);
                    SetStatus("Free-pen stroke too short.");
                }

                // Reset drawing state and release mouse capture
                _currentDrawingShapeInfo = null;
                _currentPolygonPoints.Clear(); // safe to clear shared buffer
                _isDrawing = false;
                BoundingBoxCanvas.ReleaseMouseCapture();

                e.Handled = true;
                return;
            }
        
        }



        private void ToggleProcessingPanelMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Determine "show" from the sender first (MenuItem toggles its IsChecked).
                bool show;
                if (sender is MenuItem mi)
                    show = mi.IsChecked == true;
                else
                    show = ToggleProcessingPanelMenu?.IsChecked == true;

                // Ensure UI updates happen on UI thread.
                Dispatcher.Invoke(() =>
                {
                    if (ProcessingMenuToggle != null)
                    {
                        ProcessingMenuToggle.IsChecked = show;
                        ProcessingMenuToggle.Content = show ? "Processing ▾" : "Processing ▴";
                    }

                    if (ProcessingButtonsPanel != null)
                        ProcessingButtonsPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                    if (FloatingProcessingMenu != null)
                        FloatingProcessingMenu.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

                    // Keep the MenuItem field consistent when sender wasn't the menu item itself.
                    if (!(sender is MenuItem) && ToggleProcessingPanelMenu != null)
                        ToggleProcessingPanelMenu.IsChecked = show;

                    SetStatus(show ? "Processing panel shown." : "Processing panel hidden.");
                });
            }
            catch (Exception ex)
            {
                // Log for debugging; do not throw to avoid breaking UI.
                Debug.WriteLine($"ToggleProcessingPanelMenu_Click error: {ex}");
            }
        }
        private void BoundingBoxCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            BoundingBoxCanvas.Focus();
            SWPoint pt = ClampPointToImage(e.GetPosition(BoundingBoxCanvas));
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
                _dragStartPoint = pt; // Uncommenting to store the drag start point
                StartRectangle(pt, label);
                _mouseLeftDown = false;
            }
            else if(_currentDrawingMode==DrawingMode.FreePen)
            {
                _isDrawing = true;
                StartFreePen(pt,label); _mouseLeftDown = false;
            }

        }

        private void StartFreePen(SWPoint pt, string label)
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
                Points = new List<SWPoint> { pt }
            };

            var info = new ShapeInfo
            {
                Shape = polyline,
                LabelBlock = labelBlock,
                Record = record,
                Metadata = new ShapeMetadata { Label = label, Type = AnnotationType.FreePen }
            };

            _currentDrawingShapeInfo = info;
            _shape_infos.Add(info);

            BoundingBoxCanvas.Children.Add(polyline);
            BoundingBoxCanvas.Children.Add(labelBlock);

            // ensure keyboard focus so Space works immediately
            BoundingBoxCanvas.Focus();
        }

        private void BoundingBoxCanvas_MouseMove(object sender, MouseEventArgs e)
        {
            SWPoint pt = e.GetPosition(BoundingBoxCanvas);

            if (_activeHandle != null && _reshapeShapeInfo != null && e.LeftButton == MouseButtonState.Pressed)
            {
                SWPoint clampedPt = ClampPointToImage(pt);
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
                SWPoint clampedPt = ClampPointToImage(pt);

                if (_currentDrawingMode == DrawingMode.Rectangle)
                {
                    UpdateRectangle(clampedPt);
                }
                else if (_currentDrawingMode == DrawingMode.FreePen && _currentDrawingShapeInfo.Shape is Polyline poly)
                {
                    // Add point with simple thinning to reduce excessive points
                    var last = poly.Points.Count > 0 ? poly.Points[poly.Points.Count - 1] : new SWPoint(double.NaN, double.NaN);
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
                SWPoint clampedPt = ClampPointToImage(pt);
                polyline.Points[vertexHit.VertexIndex] = clampedPt;
                UpdateAnnotationRecordFromShape(_reshapeShapeInfo);
                RefreshHandles(_reshapeShapeInfo);
                return;
            }
        }

        private void BoundingBoxCanvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            SWPoint pt = e.GetPosition(BoundingBoxCanvas);
            SWPoint clampedPt = ClampPointToImage(pt);

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
                //FinalizeFreePen(clampedPt);
                updateFreePen(clampedPt);
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
                    _shape_infos.Remove(_currentDrawingShapeInfo);
                    SetStatus("Rectangle must be fully inside the image.");
                }
                _isDrawing = false;
                _currentDrawingShapeInfo = null;
            }
        }
        #endregion

        #region Drawing/Shape Helper Methods 

        private void StartRectangle(SWPoint start, string label)
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
                Points = new List<SWPoint> { start, start }
            };

            var info = new ShapeInfo
            {
                Shape = rect,
                LabelBlock = labelBlock,
                Record = record,
                Metadata = new ShapeMetadata { Label = label, Type = AnnotationType.Rectangle }
            };

            _currentDrawingShapeInfo = info;
            _shape_infos.Add(info);

            BoundingBoxCanvas.Children.Add(rect);
            BoundingBoxCanvas.Children.Add(labelBlock);

            // ensure keyboard focus so Space works immediately
            BoundingBoxCanvas.Focus();
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

        private void UpdateRectangle(SWPoint current)
        {
            if (_currentDrawingShapeInfo?.Shape is Rectangle rect)
            {
                var start = (SWPoint)_currentDrawingShapeInfo.Record.Points[0];
                SWPoint clamped = ClampPointToImage(current);
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

                _currentDrawingShapeInfo.Record.Points = new List<SWPoint> { start, clamped };
            }
        }

        private void FinalizeRectangle(SWPoint end)
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
                    _shape_infos.Remove(_currentDrawingShapeInfo);
                    SetStatus("Rectangle must be fully inside the image.");
                }
                _currentDrawingShapeInfo = null;
                CheckAutoLabelEnable();
            }
        }

        private void StartPolygon(SWPoint start, string label)
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
                    Points = new List<SWPoint> { start }
                };

                var info = new ShapeInfo
                {
                    Shape = polyline,
                    LabelBlock = labelBlock,
                    Record = record,
                    Metadata = new ShapeMetadata { Label = label, Type = AnnotationType.Polygon }
                };

                _currentDrawingShapeInfo = info;
                _shape_infos.Add(info);

                BoundingBoxCanvas.Children.Add(polyline);
                BoundingBoxCanvas.Children.Add(labelBlock);
                _currentPolygonPoints = new List<SWPoint>();

                // ensure keyboard focus so Space works immediately
                BoundingBoxCanvas.Focus();
            }

            if (!_currentPolygonPoints.Contains(start))
                _currentPolygonPoints.Add(start);

            if (_currentDrawingShapeInfo.Shape is Polyline currentPolyline)
            {
                currentPolyline.Points = new PointCollection(_currentPolygonPoints);
            }
        }
        private void updatePolygon(SWPoint clampedPt)
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
        private void updateFreePen(SWPoint clampedPt)
        {
            if (_currentDrawingShapeInfo?.Shape is Polyline poly)
            {
                // Mirror updatePolygon: update the last point to the clamped position
                if (poly.Points.Count > 0)
                {
                    poly.Points[poly.Points.Count - 1] = clampedPt;
                }
                else
                {
                    poly.Points.Add(clampedPt);
                }

                // Keep the annotation record in sync with the visual
                _currentDrawingShapeInfo.Record.Points = poly.Points.ToList();
            }
        }

        // Helper used by right-click finalize path
        private void FinalizeFreePen(SWPoint clampedPt)
        {
            if (_currentDrawingShapeInfo != null && _currentDrawingShapeInfo.Shape is Polyline poly)
            {
                // Make sure final point equals clamped mouse pos
                if (poly.Points.Count > 0)
                    poly.Points[poly.Points.Count - 1] = clampedPt;
                else
                    poly.Points.Add(clampedPt);
                // Sync authoritative record with the visual points
                _currentDrawingShapeInfo.Record.Points = poly.Points.ToList();
                if (_currentDrawingShapeInfo.Record.Points.Count > 1)
                {
                    Annotations.Add(_currentDrawingShapeInfo.Record);
                    SaveStateForUndo();
                    RefreshAnnotations();
                    SetStatus($"Free-pen annotation added with label '{_currentDrawingShapeInfo.Metadata.Label}'.");
                }
                else
                {
                    // Discard too-short stroke
                    BoundingBoxCanvas.Children.Remove(poly);
                    if (_currentDrawingShapeInfo?.LabelBlock != null)
                        BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                    _shapeInfos.Remove(_currentDrawingShapeInfo);
                    SetStatus("Free-pen stroke too short.");
                }
                // Reset drawing state
                _currentDrawingShapeInfo = null;
                _currentPolygonPoints.Clear(); // safe to clear shared buffer
                _isDrawing = false;
                CheckAutoLabelEnable();
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

                _currentDrawingShapeInfo.Record.Points = new List<SWPoint>(_currentPolygonPoints);
                Annotations.Add(_currentDrawingShapeInfo.Record);
                SaveStateForUndo();
                RefreshAnnotations();
                SetStatus($"Polygon annotation added with label '{_currentDrawingShapeInfo.Metadata.Label}'.");
                _currentDrawingShapeInfo = null;
                _currentPolygonPoints.Clear();
                _isDrawing = false;
                CheckAutoLabelEnable();
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

                var p0 = (SWPoint)info.Record.Points[0];
                var p1 = (SWPoint)info.Record.Points[1];
                info.Record.Points[0] = new SWPoint(p0.X + clampedDelta.X, p0.Y + clampedDelta.Y);
                info.Record.Points[1] = new SWPoint(p1.X + clampedDelta.X, p1.Y + clampedDelta.Y);
            }
        }

        private void ResizeRectangle(ShapeInfo info, HitType hit, SWPoint pt)
        {
            if (info.Shape is not Rectangle rect) return;
            var points = info.Record.Points.ToList();

            var p0 = (SWPoint)points[0];
            var p1 = (SWPoint)points[1];

            pt = ClampPointToImage(pt);

            switch (hit)
            {
                case HitType.TopLeft:
                    p0 = pt;
                    break;
                case HitType.TopRight:
                    p0 = new SWPoint(p0.X, pt.Y);
                    p1 = new SWPoint(pt.X, p1.Y);
                    break;
                case HitType.BottomLeft:
                    p0 = new SWPoint(pt.X, p0.Y);
                    p1 = new SWPoint(p1.X, pt.Y);
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

        private bool IsPointInImageBounds(SWPoint pt)
        {
            return pt.X >= 0 && pt.Y >= 0 && pt.X <= _currentImageWidth && pt.Y <= _currentImageHeight;
        }

        private SWPoint ClampPointToImage(SWPoint pt)
        {
            return new SWPoint(
                Math.Max(0, Math.Min(_currentImageWidth, pt.X)),
                Math.Max(0, Math.Min(_currentImageHeight, pt.Y))
            );
        }

        private bool IsShapeFullyInImage(ShapeInfo info)
        {
            if (info.Record.Points.Count != 2) return false;
            var p0 = (SWPoint)info.Record.Points[0];
            var p1 = (SWPoint)info.Record.Points[1];
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

        private bool IsPointOverHandle(SWPoint pt, Ellipse handle)
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
            if (info == null || info.Shape == null || info.Record == null)
                return;

            if (info.Shape is Rectangle rect)
            {
                double x = Canvas.GetLeft(rect);
                double y = Canvas.GetTop(rect); // use GetTop, not SetTop
                double w = rect.Width;
                double h = rect.Height;

                // Ensure Points list exists and has two entries
                if (info.Record.Points == null)
                {
                    info.Record.Points = new List<SWPoint> { new SWPoint(x, y), new SWPoint(x + w, y + h) };
                }
                else if (info.Record.Points.Count >= 2)
                {
                    info.Record.Points[0] = new SWPoint(x, y);
                    info.Record.Points[1] = new SWPoint(x + w, y + h);
                }
                else if (info.Record.Points.Count == 1)
                {
                    info.Record.Points[0] = new SWPoint(x, y);
                    info.Record.Points.Add(new SWPoint(x + w, y + h));
                }
                else
                {
                    // empty list
                    info.Record.Points.Add(new SWPoint(x, y));
                    info.Record.Points.Add(new SWPoint(x + w, y + h));
                }
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
                new SWPoint(((SWPoint)points[0]).X, ((SWPoint)points[0]).Y), // TopLeft
                new SWPoint(((SWPoint)points[1]).X, ((SWPoint)points[0]).Y), // TopRight
                new SWPoint(((SWPoint)points[0]).X, ((SWPoint)points[1]).Y), // BottomLeft
                new SWPoint(((SWPoint)points[1]).X, ((SWPoint)points[1]).Y), // BottomRight
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
                CheckAutoLabelEnable();
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

            // update class statistics panel
            UpdateClassStats();
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
                "Keyboard Shortcuts (current):\n" +
                "F        : Next image\n" +
                "S        : Previous image\n" +
                "Space    : Finish current drawing (Rectangle/Polygon/FreePen)\n" +
                "Esc      : Cancel drawing / cancel drag / cancel reshape\n" +
                "Enter    : Save annotations\n" +
                "Delete   : Remove selected annotation (from list)\n" +
                "A        : Add box (placeholder - no implementation)\n" +
                "D        : Remove selected (same as Delete)\n" +
                "Ctrl+Z   : Undo\n" +
                "Ctrl+Y   : Redo\n" +
                "Mouse Wheel : Zoom in/out\n\n" +
                "Notes:\n" +
                "- Make sure the page or canvas has keyboard focus (canvas.Focus() is called when starting a draw).\n" +
                "- Polygon: left-click to add points; finish with Space (or right-click if enabled).\n" +
                "- Free-pen: draw with left mouse button; finish with Space (or right-click if enabled).\n",
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

            UpdateClassStats();

            // show small badge on image when class selected and image loaded
            UpdateSelectedClassBadge();
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
            // NEW: finish current drawing with Space
            else if (e.Key == Key.Space)
            {
                if (_isDrawing && _currentDrawingShapeInfo != null)
                {
                    // Use current mouse position on canvas as the final point
                    var rawPos = Mouse.GetPosition(BoundingBoxCanvas);
                    var clamped = ClampPointToImage(rawPos);

                    if (_currentDrawingMode == DrawingMode.Polygon)
                    {
                        if (_currentPolygonPoints.Count > 0)
                            _currentPolygonPoints[_currentPolygonPoints.Count - 1] = clamped;
                        else
                            _currentPolygonPoints.Add(clamped);

                        if (_currentDrawingShapeInfo.Shape is Polyline poly)
                            poly.Points = new PointCollection(_currentPolygonPoints);
                    }
                    else if (_currentDrawingMode == DrawingMode.FreePen)
                    {
                        if (_currentDrawingShapeInfo.Shape is Polyline poly)
                        {
                            if (poly.Points.Count > 0)
                                poly.Points[poly.Points.Count - 1] = clamped;
                            else
                                poly.Points.Add(clamped);
                            _currentDrawingShapeInfo.Record.Points = poly.Points.ToList();
                        }
                    }
                    else if (_currentDrawingMode == DrawingMode.Rectangle)
                    {
                        UpdateRectangle(clamped);
                    }

                    FinishDrawing_Click(this, new RoutedEventArgs());
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Escape)
            {
                // existing Escape handling...
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

        // XAML-generated wiring expects the standard (object, RoutedEventArgs) signature.
        // Forward to the existing implementation that takes a DataSetPage instance.
        private void FinishDrawing_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                FinishDrawing_Click(this, e);
            }
            catch (Exception ex)
            {
                // Defensive: surface error to status so user sees something instead of a crash.
                SetStatus($"Finish drawing failed: {ex.Message}");
            }
        }

        private void FinishDrawing_Click(DataSetPage dataSetPage, RoutedEventArgs routedEventArgs)
        {
            if (!_isDrawing || _currentDrawingShapeInfo == null)
            {
                SetStatus("Nothing to finish.");
                return;
            }

            // RECTANGLE: finalize using the recorded end point (if any)
            if (_currentDrawingMode == DrawingMode.Rectangle)
            {
                var pts = _currentDrawingShapeInfo.Record.Points;
                var end = pts != null && pts.Count > 1 ? (SWPoint)pts[1] : (SWPoint)pts[0];
                FinalizeRectangle(end);
            }
            // POLYGON: require at least 3 points, otherwise cancel
            else if (_currentDrawingMode == DrawingMode.Polygon)
            {
                if (_currentPolygonPoints != null && _currentPolygonPoints.Count > 2)
                {
                    FinalizePolygon();
                }
                else
                {
                    // remove the tentative visual and cancel
                    if (_currentDrawingShapeInfo.Shape is Polyline poly)
                        BoundingBoxCanvas.Children.Remove(poly);
                    if (_currentDrawingShapeInfo.LabelBlock != null)
                        BoundingBoxCanvas.Children.Remove(_currentDrawingShapeInfo.LabelBlock);
                    _shapeInfos.Remove(_currentDrawingShapeInfo);
                    _currentPolygonPoints.Clear();
                    _isDrawing = false;
                    _currentDrawingShapeInfo = null;
                    BoundingBoxCanvas.ReleaseMouseCapture();
                    SetStatus("Polygon requires at least 3 points. Drawing canceled.");
                    return;
                }
            }
            // FREE PEN: finalize using last point of the stroke
            else if (_currentDrawingMode == DrawingMode.FreePen)
            {
                if (_currentDrawingShapeInfo.Shape is Polyline poly)
                {
                    var last = poly.Points.Count > 0 ? poly.Points[poly.Points.Count - 1] : new SWPoint(0, 0);
                    FinalizeFreePen(last);
                }
            }

            // common cleanup (safe even if Finalize* already cleared some state)
            _isDrawing = false;
            _currentDrawingShapeInfo = null;
            _currentPolygonPoints.Clear();
            BoundingBoxCanvas.ReleaseMouseCapture();
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
                // Insert inside LoadFolder_Click after _currentImageIndex = 0; await LoadImageAtIndex...
                if (_currentProject != null)
                {
                    var res = MessageBox.Show("Augment images now to increase dataset size and update project? (You can skip and run later)", "Augment dataset", MessageBoxButton.YesNo, MessageBoxImage.Question);
                    if (res == MessageBoxResult.Yes)
                    {
                        await AugmentCurrentProjectImagesAsync();
                        SaveProject_Click(sender, e); // optional: save project after augmentation
                    }
                }
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

            // update badge visibility based on loaded image and selected label
            UpdateSelectedClassBadge();
        }

        private void ApplyZoomCentered(double newZoom)
        {
            // Ensure ZoomTransform exists (defined in XAML as a ScaleTransform named "ZoomTransform")
            if (ZoomTransform == null) return;

            // Determine center in image/local coordinates. Prefer the displayed control size (actual), fall back to raw pixel size.
            double centerX = LabelingImage != null && LabelingImage.ActualWidth > 0
                ? LabelingImage.ActualWidth * 0.5
                : (_currentImageWidth > 0 ? _currentImageWidth * 0.5 : 0);

            double centerY = LabelingImage != null && LabelingImage.ActualHeight > 0
                ? LabelingImage.ActualHeight * 0.5
                : (_currentImageHeight > 0 ? _currentImageHeight * 0.5 : 0);

            // Set the ScaleTransform center so scaling happens around the center of the image / image box.
            ZoomTransform.CenterX = centerX;
            ZoomTransform.CenterY = centerY;

            // Apply the scale
            ZoomTransform.ScaleX = newZoom;
            ZoomTransform.ScaleY = newZoom;

            SetStatus($"Zoom: {newZoom * 100:0}%");
        }

        private void ZoomIn_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Min(_zoom + ZoomStep, ZoomMax);
            ApplyZoomCentered(_zoom);
        }

        private void ZoomOut_Click(object sender, RoutedEventArgs e)
        {
            _zoom = Math.Max(_zoom - ZoomStep, ZoomMin);
            ApplyZoomCentered(_zoom);
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

        // Drag handler for the floating processing menu thumb (uses fully-qualified DragDeltaEventArgs to avoid adding a using)
        private void MenuDragThumb_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        {
            if (FloatingProcessingMenu == null) return;

            // Ensure RenderTransform is a TranslateTransform
            if (FloatingProcessingMenu.RenderTransform is not TranslateTransform tt)
            {
                tt = new TranslateTransform();
                FloatingProcessingMenu.RenderTransform = tt;
            }

            double newX = tt.X + e.HorizontalChange;
            double newY = tt.Y + e.VerticalChange;

            // Optional: clamp to canvas size so the menu doesn't drift off-screen
            double canvasW = Math.Max(1.0, BoundingBoxCanvas.ActualWidth);
            double canvasH = Math.Max(1.0, BoundingBoxCanvas.ActualHeight);
            double menuW = Math.Max(1.0, FloatingProcessingMenu.ActualWidth);
            double menuH = Math.Max(1.0, FloatingProcessingMenu.ActualHeight);

            // Allow the menu to move roughly within the visible canvas area (with small margin).
            double marginFactor = 0.9;
            double minX = -canvasW * marginFactor;
            double maxX = canvasW * marginFactor;
            double minY = -canvasH * marginFactor;
            double maxY = canvasH * marginFactor;

            // Clamp values so the menu doesn't drift far off-screen.
            tt.X = Math.Max(minX, Math.Min(maxX, newX));
            tt.Y = Math.Max(minY, Math.Min(maxY, newY));
        }

        private void ToggleProcessingMenu_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                bool isOpen = ProcessingMenuToggle?.IsChecked == true;

                // Main buttons panel
                if (ProcessingButtonsPanel != null)
                    ProcessingButtonsPanel.Visibility = isOpen ? Visibility.Visible : Visibility.Collapsed;

                // When collapsed, ensure all option panels and preview are also collapsed
                if (!isOpen)
                {
                    if (GrayscaleOptionsPanel != null) GrayscaleOptionsPanel.Visibility = Visibility.Collapsed;
                    if (EdgeOptionsPanel != null) EdgeOptionsPanel.Visibility = Visibility.Collapsed;
                    if (ContourOptionsPanel != null) ContourOptionsPanel.Visibility = Visibility.Collapsed;
                    if (ProcessingPreviewBadge != null) ProcessingPreviewBadge.Visibility = Visibility.Collapsed;

                    // Reset any UI state that may imply an active processing mode
                    _selectedProcessingMode = ImageProcessMode.None;
                    if (ProcessingPreviewText != null) ProcessingPreviewText.Text = "Preview";
                }

                // Update toggle content
                if (ProcessingMenuToggle != null)
                    ProcessingMenuToggle.Content = isOpen ? "Processing ▾" : "Processing ▴";

                SetStatus(isOpen ? "Processing options expanded." : "Processing options collapsed.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"ToggleProcessingMenu_Click error: {ex}");
            }
        }

        private void ProcessGrayscale_Click(object sender, RoutedEventArgs e)
        {
            _selectedProcessingMode = ImageProcessMode.Grayscale;

            if (ProcessingPreviewBadge != null && ProcessingPreviewText != null)
            {
                ProcessingPreviewBadge.Visibility = Visibility.Visible;
                ProcessingPreviewText.Text = "Preview: Grayscale";
            }

            if (GrayscaleOptionsPanel != null) GrayscaleOptionsPanel.Visibility = Visibility.Visible;
            if (EdgeOptionsPanel != null) EdgeOptionsPanel.Visibility = Visibility.Collapsed;
            if (ContourOptionsPanel != null) ContourOptionsPanel.Visibility = Visibility.Collapsed;

            SetStatus($"Processing preview: {ProcessingPreviewText?.Text}");
        }

        private void ProcessEdges_Click(object sender, RoutedEventArgs e)
        {
            _selectedProcessingMode = ImageProcessMode.Edges;

            if (ProcessingPreviewBadge != null && ProcessingPreviewText != null)
            {
                ProcessingPreviewBadge.Visibility = Visibility.Visible;
                ProcessingPreviewText.Text = "Preview: Edges (Canny)";
            }

            if (GrayscaleOptionsPanel != null) GrayscaleOptionsPanel.Visibility = Visibility.Collapsed;
            if (EdgeOptionsPanel != null) EdgeOptionsPanel.Visibility = Visibility.Visible;
            if (ContourOptionsPanel != null) ContourOptionsPanel.Visibility = Visibility.Collapsed;

            SetStatus($"Processing preview: {ProcessingPreviewText?.Text}");
        }

        private void ProcessContours_Click(object sender, RoutedEventArgs e)
        {
            _selectedProcessingMode = ImageProcessMode.Contours;

            if (ProcessingPreviewBadge != null && ProcessingPreviewText != null)
            {
                ProcessingPreviewBadge.Visibility = Visibility.Visible;
                ProcessingPreviewText.Text = "Preview: Contours";
            }

            if (GrayscaleOptionsPanel != null) GrayscaleOptionsPanel.Visibility = Visibility.Collapsed;
            if (EdgeOptionsPanel != null) EdgeOptionsPanel.Visibility = Visibility.Collapsed;
            if (ContourOptionsPanel != null) ContourOptionsPanel.Visibility = Visibility.Visible;

            SetStatus($"Processing preview: {ProcessingPreviewText?.Text}");
        }

        private void ProcessContourRects_Click(object sender, RoutedEventArgs e)
        {
            _selectedProcessingMode = ImageProcessMode.ContourRects;

            if (ProcessingPreviewBadge != null && ProcessingPreviewText != null)
            {
                ProcessingPreviewBadge.Visibility = Visibility.Visible;
                ProcessingPreviewText.Text = "Preview: Contour Rects";
            }

            if (GrayscaleOptionsPanel != null) GrayscaleOptionsPanel.Visibility = Visibility.Collapsed;
            if (EdgeOptionsPanel != null) EdgeOptionsPanel.Visibility = Visibility.Collapsed;
            if (ContourOptionsPanel != null) ContourOptionsPanel.Visibility = Visibility.Visible;

            SetStatus($"Processing preview: {ProcessingPreviewText?.Text}");
        }

        private void ResetProcessing_Click(object sender, RoutedEventArgs e)
        {
            if (ProcessingPreviewBadge != null) ProcessingPreviewBadge.Visibility = Visibility.Collapsed;

            // Restore original image if available
            if (!string.IsNullOrEmpty(_currentImagePath) && File.Exists(_currentImagePath))
            {
                try
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.UriSource = new Uri(_currentImagePath);
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.EndInit();
                    bmp.Freeze();
                    LabelingImage.Source = bmp;
                }
                catch
                {
                    // Ignore restore errors
                }
            }

            // Hide all option panels
            if (GrayscaleOptionsPanel != null) GrayscaleOptionsPanel.Visibility = Visibility.Collapsed;
            if (EdgeOptionsPanel != null) EdgeOptionsPanel.Visibility = Visibility.Collapsed;
            if (ContourOptionsPanel != null) ContourOptionsPanel.Visibility = Visibility.Collapsed;

            SetStatus("Processing cleared. Original image retained.");
        }

        private async void ApplyProcessingButton_Click(object? sender, RoutedEventArgs e)
        {
            if (string.IsNullOrEmpty(_currentImagePath) || !File.Exists(_currentImagePath))
            {
                SetStatus("No image loaded to process.");
                return;
            }

            if (_selectedProcessingMode == ImageProcessMode.None)
            {
                SetStatus("Select a processing mode first (Grayscale/Edges/Contours).");
                return;
            }

            // Capture UI-controlled parameters before running on background thread
            int minContourArea = 50;
            try
            {
                if (MinContourAreaSlider != null)
                    minContourArea = Math.Max(0, (int)MinContourAreaSlider.Value);
            }
            catch
            {
                minContourArea = 50;
            }

            // Hard-coded maximum as requested
            int maxContourArea = 1000;

            SetStatus("Processing...");

            try
            {
                // Run processing on thread-pool and get a frozen BitmapSource from the worker thread.
                var result = await Task.Run(() => ProcessImage(_currentImagePath!, _selectedProcessingMode, minContourArea, maxContourArea));

                if (result != null)
                {
                    // result is already frozen inside ProcessImage; safe to assign on UI thread.
                    Dispatcher.Invoke(() =>
                    {
                        LabelingImage.Source = result;
                        _lastProcessedImage = result;
                        if (ProcessingPreviewBadge != null && ProcessingPreviewText != null)
                        {
                            ProcessingPreviewBadge.Visibility = Visibility.Visible;
                            ProcessingPreviewText.Text = _selectedProcessingMode switch
                            {
                                ImageProcessMode.Grayscale => "Preview: Grayscale",
                                ImageProcessMode.Edges => "Preview: Edges",
                                ImageProcessMode.Contours => "Preview: Contours",
                                ImageProcessMode.ContourRects => "Preview: Contour Rects",
                                _ => "Preview"
                            };
                        }
                        SetStatus($"Processing preview ready: {ProcessingPreviewText?.Text}");
                    });
                }
                else
                {
                    Dispatcher.Invoke(() => SetStatus("Processing produced no result."));
                }
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() => SetStatus($"Processing failed: {ex.Message}"));
            }
        }

        // ProcessImage now accepts contour area limits and filters small/large connected components
        private BitmapSource? ProcessImage(string imagePath, ImageProcessMode mode, int minContourArea = 50, int maxContourArea = int.MaxValue)
        {
            try
            {
                // Load image fully into memory
                BitmapImage src;
                using (var fs = File.OpenRead(imagePath))
                {
                    src = new BitmapImage();
                    src.BeginInit();
                    src.CacheOption = BitmapCacheOption.OnLoad;
                    src.StreamSource = fs;
                    src.EndInit();
                    src.Freeze();
                }

                // --- GRAYSCALE ---
                if (mode == ImageProcessMode.Grayscale)
                {
                    var conv = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                    conv.Freeze();
                    return conv;
                }

                // Convert to Gray8 for pixel processing
                var gray = new FormatConvertedBitmap(src, PixelFormats.Gray8, null, 0);
                gray.Freeze();

                int width = gray.PixelWidth;
                int height = gray.PixelHeight;
                int stride = (width * gray.Format.BitsPerPixel + 7) / 8;

                var pixels = new byte[height * stride];
                gray.CopyPixels(pixels, stride, 0);

                // --- EDGES ---
                if (mode == ImageProcessMode.Edges)
                {
                    int[] gx = { -1, 0, 1, -2, 0, 2, -1, 0, 1 };
                    int[] gy = { -1, -2, -1, 0, 0, 0, 1, 2, 1 };

                    var outPixels = new byte[height * stride];

                    for (int y = 1; y < height - 1; y++)
                    {
                        for (int x = 1; x < width - 1; x++)
                        {
                            int sumX = 0, sumY = 0, k = 0;

                            for (int ky = -1; ky <= 1; ky++)
                            {
                                for (int kx = -1; kx <= 1; kx++, k++)
                                {
                                    int sample = pixels[(y + ky) * stride + (x + kx)];
                                    sumX += sample * gx[k];
                                    sumY += sample * gy[k];
                                }
                            }

                            int mag = (int)Math.Sqrt(sumX * sumX + sumY * sumY);
                            outPixels[y * stride + x] = (byte)Math.Min(255, mag);
                        }
                    }

                    var outBmp = BitmapSource.Create(width, height, src.DpiX, src.DpiY,
                                                     PixelFormats.Gray8, null, outPixels, stride);
                    outBmp.Freeze();
                    return outBmp;
                }

                // --- CONTOURS (outer + inner / component-filtered) ---
                #region Contours
                if (mode == ImageProcessMode.Contours)
                {
                    byte thresh = 128;
                    bool[,] bin = new bool[height, width];
                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            bin[y, x] = pixels[y * stride + x] > thresh;

                    var visitedFg = new bool[height, width];
                    var outline = new byte[height * stride];
                    var dirs = new (int dx, int dy)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

                    // 1) Find foreground components, apply area filter, mark outer boundary pixels
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (!bin[y, x] || visitedFg[y, x]) continue;

                            var stack = new Stack<(int x, int y)>();
                            var comp = new List<(int x, int y)>();
                            stack.Push((x, y));
                            visitedFg[y, x] = true;

                            while (stack.Count > 0)
                            {
                                var (cx, cy) = stack.Pop();
                                comp.Add((cx, cy));

                                foreach (var d in dirs)
                                {
                                    int nx = cx + d.dx, ny = cy + d.dy;
                                    if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                        !visitedFg[ny, nx] && bin[ny, nx])
                                    {
                                        visitedFg[ny, nx] = true;
                                        stack.Push((nx, ny));
                                    }
                                }
                            }

                            int compArea = comp.Count;
                            if (compArea < minContourArea || compArea > maxContourArea)
                                continue;

                            // mark outer boundary: foreground pixel that has at least one background neighbor
                            foreach (var (cx, cy) in comp)
                            {
                                bool isBoundary = false;
                                foreach (var d in dirs)
                                {
                                    int nx = cx + d.dx, ny = cy + d.dy;
                                    if (nx < 0 || nx >= width || ny < 0 || ny >= height || !bin[ny, nx])
                                    {
                                        isBoundary = true;
                                        break;
                                    }
                                }

                                if (isBoundary)
                                {
                                    outline[cy * stride + cx] = 255; // outer contour bright
                                }
                            }
                        }
                    }

                    // 2) Find background components (potential holes). Any background component that does NOT touch image border is a hole.
                    var visitedBg = new bool[height, width];
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (bin[y, x] || visitedBg[y, x]) continue;

                            var stack = new Stack<(int x, int y)>();
                            var compBg = new List<(int x, int y)>();
                            bool touchesBorder = false;
                            stack.Push((x, y));
                            visitedBg[y, x] = true;

                            while (stack.Count > 0)
                            {
                                var (cx, cy) = stack.Pop();
                                compBg.Add((cx, cy));

                                if (cx == 0 || cy == 0 || cx == width - 1 || cy == height - 1)
                                    touchesBorder = true;

                                foreach (var d in dirs)
                                {
                                    int nx = cx + d.dx, ny = cy + d.dy;
                                    if (nx >= 0 && nx < width && ny >= 0 && ny < height && !visitedBg[ny, nx] && !bin[ny, nx])
                                    {
                                        visitedBg[ny, nx] = true;
                                        stack.Push((nx, ny));
                                    }
                                }
                            }

                            // only treat as hole if background component does NOT touch image border
                            if (touchesBorder) continue;

                            // optional: apply hole-area filter too (use same min/max as foreground)
                            int holeArea = compBg.Count;
                            if (holeArea < minContourArea || holeArea > maxContourArea)
                            {
                                continue;
                            }

                            // mark inner boundary: background pixel adjacent to foreground
                            foreach (var (cx, cy) in compBg)
                            {
                                bool isBoundary = false;
                                foreach (var d in dirs)
                                {
                                    int nx = cx + d.dx, ny = cy + d.dy;
                                    if (nx >= 0 && nx < width && ny >= 0 && ny < height && bin[ny, nx])
                                    {
                                        isBoundary = true;
                                        break;
                                    }
                                }

                                if (isBoundary)
                                {
                                    // if outer already marked, keep outer (255). otherwise set inner value (180).
                                    if (outline[cy * stride + cx] == 0)
                                        outline[cy * stride + cx] = 180; // inner contour (hole)
                                }
                            }
                        }
                    }

                    var outBmp = BitmapSource.Create(width, height, src.DpiX, src.DpiY,
                                                     PixelFormats.Gray8, null, outline, stride);
                    outBmp.Freeze();
                    return outBmp;
                } 
                #endregion

                // --- CONTOUR RECTS ---
                if (mode == ImageProcessMode.ContourRects)
                {
                    byte thresh = 128;
                    bool[,] bin = new bool[height, width];

                    for (int y = 0; y < height; y++)
                        for (int x = 0; x < width; x++)
                            bin[y, x] = pixels[y * stride + x] > thresh;

                    var visited = new bool[height, width];
                    var outPixels = new byte[height * stride];

                    var dirs = new (int dx, int dy)[] { (1, 0), (-1, 0), (0, 1), (0, -1) };

                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            if (!bin[y, x] || visited[y, x]) continue;

                            int minX = x, maxX = x, minY = y, maxY = y;
                            int componentCount = 0;

                            var stack = new Stack<(int x, int y)>();
                            stack.Push((x, y));
                            visited[y, x] = true;

                            while (stack.Count > 0)
                            {
                                var (cx, cy) = stack.Pop();
                                componentCount++;

                                minX = Math.Min(minX, cx);
                                maxX = Math.Max(maxX, cx);
                                minY = Math.Min(minY, cy);
                                maxY = Math.Max(maxY, cy);

                                foreach (var d in dirs)
                                {
                                    int nx = cx + d.dx, ny = cy + d.dy;
                                    if (nx >= 0 && nx < width && ny >= 0 && ny < height &&
                                        !visited[ny, nx] && bin[ny, nx])
                                    {
                                        visited[ny, nx] = true;
                                        stack.Push((nx, ny));
                                    }
                                }
                            }

                            // Filter by component pixel count (area) using provided limits.
                            if (componentCount < minContourArea || componentCount > maxContourArea)
                            {
                                // skip drawing this component's rectangle
                                continue;
                            }

                            int pad = 1;
                            minX = Math.Max(0, minX - pad);
                            minY = Math.Max(0, minY - pad);
                            maxX = Math.Min(width - 1, maxX + pad);
                            maxY = Math.Min(height - 1, maxY + pad);

                            for (int rx = minX; rx <= maxX; rx++)
                            {
                                outPixels[minY * stride + rx] = 255;
                                outPixels[maxY * stride + rx] = 255;
                            }
                            for (int ry = minY; ry <= maxY; ry++)
                            {
                                outPixels[ry * stride + minX] = 255;
                                outPixels[ry * stride + maxX] = 255;
                            }
                        }
                    }

                    var outBmp = BitmapSource.Create(width, height, src.DpiX, src.DpiY,
                                                     PixelFormats.Gray8, null, outPixels, stride);
                    outBmp.Freeze();
                    return outBmp;
                }

                // If mode is unknown
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("ProcessImage error: " + ex);
                return null;
            }
        }

        /// <summary>
        /// Called by the ContextMenu to save the processed preview (if available) or the visible image.
        /// </summary>
        private void SaveProcessedImage_ContextMenu_Click(object sender, RoutedEventArgs e)
        {
            // Prefer the last processed preview (if any), fall back to current Image.Source
            var bmp = _lastProcessedImage ?? (LabelingImage?.Source as BitmapSource);

            if (bmp == null)
            {
                SetStatus("No processed preview available to save.");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*",
                FileName = $"Processed_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".png"
            };

            if (dlg.ShowDialog() == true)
            {
                if (SaveBitmapSourceToPath(bmp, dlg.FileName))
                    SetStatus($"Saved processed preview: {dlg.FileName}");
                else
                    SetStatus("Failed to save processed preview.");
            }
        }

        /// <summary>
        /// Save whatever is currently shown in the image control (useful if preview wasn't created separately).
        /// </summary>
        private void SaveCurrentImage_ContextMenu_Click(object sender, RoutedEventArgs e)
        {
            var bmp = LabelingImage?.Source as BitmapSource;
            if (bmp == null)
            {
                SetStatus("No image loaded to save.");
                return;
            }

            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg;*.jpeg)|*.jpg;*.jpeg|All Files (*.*)|*.*",
                FileName = $"Image_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultExt = ".png"
            };

            if (dlg.ShowDialog() == true)
            {
                if (SaveBitmapSourceToPath(bmp, dlg.FileName))
                    SetStatus($"Saved image: {dlg.FileName}");
                else
                    SetStatus("Failed to save image.");
            }
        }

        /// <summary>
        /// Helper to write a frozen BitmapSource to disk (PNG/JPEG by extension).
        /// </summary>
        private bool SaveBitmapSourceToPath(BitmapSource bmp, string path)
        {
            try
            {
                BitmapEncoder encoder;
                var ext = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (ext == ".jpg" || ext == ".jpeg")
                    encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                else
                    encoder = new PngBitmapEncoder();

                encoder.Frames.Add(BitmapFrame.Create(bmp));
                using var fs = File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None);
                encoder.Save(fs);
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"SaveBitmapSourceToPath error: {ex}");
                return false;
            }
        }

        private void BoundingBoxCanvas_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            // Enable/disable items based on current state
            if (CtxSaveProcessed != null)
                CtxSaveProcessed.IsEnabled = _lastProcessedImage != null;

            if (CtxSaveCurrent != null)
                CtxSaveCurrent.IsEnabled = LabelingImage?.Source != null;

            // Enable editing/removal only when an annotation is selected in the list
            bool hasSelection = AnnotationListView?.SelectedItem != null;
            if (CtxRemoveSelected != null)
                CtxRemoveSelected.IsEnabled = hasSelection;
            if (CtxEditAnnotation != null)
                CtxEditAnnotation.IsEnabled = hasSelection;
        }

        private void UpdateClassStats()
        {
            // Get ordered label list from the UI so display order matches LabelComboBox.
            var labels = LabelComboBox?.Items.Cast<object>()
                      .Select(i => i?.ToString() ?? "")
                      .Where(s => !string.IsNullOrEmpty(s))
                      .ToList() ?? new List<string>();

            // Count annotations across the whole project (Annotations holds project annotations).
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var lbl in labels) counts[lbl] = 0;

            foreach (var ann in Annotations)
            {
                if (string.IsNullOrEmpty(ann.Label)) continue;
                if (!counts.ContainsKey(ann.Label))
                    counts[ann.Label] = 0;
                counts[ann.Label]++;
            }

            // Prepare items in the same order as labels; include any extra labels found in annotations.
            var items = labels.Select(l => new { Label = l, Count = counts.TryGetValue(l, out var c) ? c : 0 }).ToList();
            var extras = counts.Keys.Except(labels, StringComparer.OrdinalIgnoreCase)
                 .Select(l => new { Label = l, Count = counts[l] });
            items.AddRange(extras);

            Dispatcher.Invoke(() =>
            {
                if (ClassStatsItems != null)
                    ClassStatsItems.ItemsSource = items;

                if (ClassStatsTotal != null)
                    ClassStatsTotal.Text = $"Total annotations: {Annotations?.Count ?? 0}";

                if (ClassStatsPanel != null)
                    ClassStatsPanel.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            });
        }

        // Toggle handler for the Class Statistics collapse/expand button.
        private void ClassStatsToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ClassStatsBody == null || ClassStatsToggleButton == null) return;

                if (ClassStatsBody.Visibility == Visibility.Visible)
                {
                    ClassStatsBody.Visibility = Visibility.Collapsed;
                    ClassStatsToggleButton.Content = "▸"; // collapsed glyph
                    ClassStatsToggleButton.ToolTip = "Expand statistics";
                }
                else
                {
                    ClassStatsBody.Visibility = Visibility.Visible;
                    ClassStatsToggleButton.Content = "▾"; // expanded glyph
                    ClassStatsToggleButton.ToolTip = "Collapse statistics";
                }
            }
            catch
            {
                // Non-critical UI toggle; swallow exceptions.
            }
        }

        // Optional programmatic helper to set collapsed state
        public void SetClassStatsCollapsed(bool collapsed)
        {
            try
            {
                if (ClassStatsBody == null || ClassStatsToggleButton == null) return;
                ClassStatsBody.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
                ClassStatsToggleButton.Content = collapsed ? "▸" : "▾";
                ClassStatsToggleButton.ToolTip = collapsed ? "Expand statistics" : "Collapse statistics";
            }
            catch { }
        }

        // replace existing ClassStatsDockToggleButton_Click with this (keeps internal body expanded when showing)
        //private void ClassStatsDockToggleButton_Click(object sender, RoutedEventArgs e)
        //{
        //    try
        //    {
        //        if (ClassStatsPanel == null || ClassStatsDockToggleButton == null) return;

        //        if (ClassStatsPanel.Visibility == Visibility.Visible)
        //        {
        //            ClassStatsPanel.Visibility = Visibility.Collapsed;
        //            ClassStatsDockToggleButton.Content = "▶";
        //            _classStatsOverlayShown = false;
        //            SetStatus("Class statistics hidden.");
        //        }
        //        else
        //        {
        //            ClassStatsPanel.Visibility = Visibility.Visible;
        //            // ensure body expanded when overlay appears
        //            if (ClassStatsBody != null) ClassStatsBody.Visibility = Visibility.Visible;
        //            if (ClassStatsToggleButton != null) { ClassStatsToggleButton.Content = "▾"; ClassStatsToggleButton.ToolTip = "Collapse statistics"; }
        //            ClassStatsDockToggleButton.Content = "◀";
        //            _classStatsOverlayShown = true;
        //            SetStatus("Class statistics shown.");
        //        }
        //    }
        //    catch { /* non-critical UI toggle */ }
        //}

        //private void ClassStatsDockToggleButton_Click(object sender, RoutedEventArgs e)
        //{
        //    try
        //    {
        //        if (ClassStatsPanel == null || ClassStatsDockToggleButton == null) return;

        //        ClassStatsDockToggleButton.Content = (ClassStatsPanel != null && ClassStatsPanel.Visibility == Visibility.Visible) ? "◀" : "▶";
        //    }
        //    catch { /* non-critical */ }
        //}

        private async void AutoLabelButton_Click(object sender, RoutedEventArgs e)
        {
            // Call export yolo8OBB save to Light-DataSet folder


        }
        #region Auto-Label
        // Update Execute handler to handle the revised ComboBox options.
        private async void AutoLabelExecuteButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                var selected = (AutoLabelComboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                if (string.IsNullOrEmpty(selected))
                {
                    SetStatus("Select an Auto Label action first.");
                    return;
                }

                switch (selected)
                {
                    case "Auto label":
                        SetStatus("Running auto-labeler...");
                        try
                        {
                            var svc = new VisionAICam.Services.AutoLabelerService();
                            bool added = await svc.RunAutoLabelingAsync(null, 0.5).ConfigureAwait(false);

                            Dispatcher.Invoke(() =>
                            {
                                if (added)
                                {
                                    if (ProjectSession.Annotations != null)
                                    {
                                        Annotations = ProjectSession.Annotations;
                                        RefreshAnnotations();
                                    }
                                    else
                                    {
                                        RefreshAnnotations();
                                    }
                                    SetStatus("Auto-labeling finished — annotations added.");
                                }
                                else
                                {
                                    SetStatus("Auto-labeling finished — no annotations were added.");
                                }
                                CheckAutoLabelEnable();
                            });
                        }
                        catch (Exception ex)
                        {
                            Dispatcher.Invoke(() => SetStatus($"Auto-label failed: {ex.Message}"));
                        }
                        break;

                    case "Save Project":
                        SaveProject_Click(sender, e);
                        break;

                    case "ExportYolo8n-obb":
                        ExportYolo8_OBB_Mini_Click(sender, e);
                        break;

                    case "Load DataSet(obb)":
                        {
                            var dlg = new Ookii.Dialogs.Wpf.VistaFolderBrowserDialog
                            {
                                Description = "Select folder containing OBB dataset (.txt annotation files)",
                                UseDescriptionForTitle = true
                            };
                            if (dlg.ShowDialog() != true)
                            {
                                SetStatus("Load DataSet canceled.");
                                break;
                            }

                            var folder = dlg.SelectedPath;
                            SetStatus("Verifying OBB dataset format...");
                            var (ok, report) = await Task.Run(() => VerifyObbFolder(folder));
                            if (ok)
                            {
                                SetStatus("OBB dataset verification succeeded.");
                                MessageBox.Show(report, "OBB Dataset Verified", MessageBoxButton.OK, MessageBoxImage.Information);
                            }
                            else
                            {
                                SetStatus("OBB dataset verification failed.");
                                MessageBox.Show(report, "OBB Dataset Verification Failed", MessageBoxButton.OK, MessageBoxImage.Warning);
                            }
                        }
                        break;

                    case "Train model":
                        {
                            SetStatus("Preparing training dataset...");
                            try
                            {
                                var svc = new VisionAICam.Services.AutoLabelerService();
                                // Run dataset preparation off UI thread
                                bool ok = await Task.Run(() => svc.PrepareTrainingDataset(10, VisionAICam.YoloExportFormat.YoloV8));
                                if (ok)
                                {
                                    SetStatus("Training dataset prepared. Invoke external training pipeline as needed.");
                                    MessageBox.Show("Training dataset prepared. Run your training pipeline separately.", "Train model", MessageBoxButton.OK, MessageBoxImage.Information);
                                }
                                else
                                {
                                    SetStatus("Failed to prepare training dataset.");
                                    MessageBox.Show("PrepareTrainingDataset returned false.", "Train model", MessageBoxButton.OK, MessageBoxImage.Warning);
                                }
                            }
                            catch (Exception ex)
                            {
                                SetStatus($"Train model failed: {ex.Message}");
                            }
                        }
                        break;

                    default:
                        SetStatus("Unknown Auto Label action.");
                        break;
                }
            }
            catch (Exception ex)
            {
                SetStatus($"Execute failed: {ex.Message}");
            }
        }

        private void AutoLabelComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
        {
            try
            {
                // Re-evaluate whether Execute should be enabled for the newly selected action.
                CheckAutoLabelEnable();

                // Optionally update status so user sees the selected action
                var selected = (AutoLabelComboBox?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";
                if (!string.IsNullOrEmpty(selected))
                    SetStatus($"Auto Label action: {selected}");
            }
            catch
            {
                // Non-critical UI handler — swallow exceptions to avoid breaking the page.
            }
        }
        //p Make enabling logic respect the newly added options.
        private void CheckAutoLabelEnable()
        {
            try
            {
                Dispatcher.Invoke(() =>
                {
                    if (AutoLabelExecuteButton == null || AutoLabelComboBox == null)
                        return;

                    var selected = (AutoLabelComboBox.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "";

                    switch (selected)
                    {
                        case "Save Project":
                            AutoLabelExecuteButton.IsEnabled = _currentProject != null;
                            break;

                        case "ExportYolo8n-obb":
                            AutoLabelExecuteButton.IsEnabled = _currentProject != null && _currentProject.ImagePaths != null && _currentProject.ImagePaths.Count > 0;
                            break;

                        case "Load DataSet(obb)":
                            // Always allow loading/verification
                            AutoLabelExecuteButton.IsEnabled = true;
                            break;

                        case "Train model":
                        case "Auto label":
                            // Require per-class minimum annotations (same rule as before)
                            if (_currentProject == null || _currentProject.ClassLabels == null || _currentProject.ClassLabels.Count == 0)
                            {
                                AutoLabelExecuteButton.IsEnabled = false;
                                break;
                            }

                            const int requiredCount = 10;
                            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                            foreach (var lbl in _currentProject.ClassLabels)
                                counts[lbl] = 0;

                            foreach (var ann in Annotations)
                            {
                                if (string.IsNullOrWhiteSpace(ann.Label)) continue;
                                if (counts.ContainsKey(ann.Label)) counts[ann.Label]++;
                            }

                            bool allReached = _currentProject.ClassLabels.All(lbl => counts.TryGetValue(lbl, out var c) && c >= requiredCount);
                            AutoLabelExecuteButton.IsEnabled = allReached;
                            break;

                        default:
                            AutoLabelExecuteButton.IsEnabled = false;
                            break;
                    }
                });
            }
            catch
            {
                // non-critical
            }
        }


        // Mini export handler: creates a train-ready folder (no split) for YOLOv8 OBB.
        // Adds images/ and labels/ and writes a simple data.yaml.
        private void ExportYolo8_OBB_Mini_Click(object sender, RoutedEventArgs e)
        {
            if (_currentProject == null || _currentProject.ImagePaths == null || _currentProject.ImagePaths.Count == 0)
            {
                SetStatus("No project or images to export.");
                return;
            }

            var dialog = new VistaFolderBrowserDialog
            {
                Description = "Select output folder for YOLOv8 OBB mini export (no split)",
                UseDescriptionForTitle = true
            };

            if (dialog.ShowDialog() != true)
            {
                SetStatus("YOLOv8 OBB mini export canceled.");
                return;
            }

            string outputFolder = System.IO.Path.Combine(dialog.SelectedPath, $"Yolo8OBB_Mini_{DateTime.Now:yyyyMMdd_HHmmss}");

            try
            {
                // Convert annotations to rotated boxes (non-destructive)
                var projectForExport = Convert2RotatedBox(_currentProject);

                ExportYoloV8_OBB_MiniSave(projectForExport, outputFolder);

                SetStatus($"YOLOv8 OBB mini export complete: {outputFolder}");
            }
            catch (Exception ex)
            {
                SetStatus($"YOLOv8 OBB mini export failed: {ex.Message}");
            }
        }

        // Helper: perform the actual copy + label writing for a no-split YOLOv8 OBB training folder.
        private void ExportYoloV8_OBB_MiniSave(AnnotationProject project, string outputFolder)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));
            Directory.CreateDirectory(outputFolder);

            string imagesOut = System.IO.Path.Combine(outputFolder, "images");
            string labelsOut = System.IO.Path.Combine(outputFolder, "labels");
            Directory.CreateDirectory(imagesOut);
            Directory.CreateDirectory(labelsOut);

            var classLabels = project.ClassLabels ?? new List<string>();

            // Group annotations by image name — this ensures we export only images that have at least one annotation.
            var annsByImage = (project.Annotations ?? Enumerable.Empty<AnnotationRecord>())
                .Where(a => !string.IsNullOrWhiteSpace(a.ImageName))
                .GroupBy(a => a.ImageName, StringComparer.OrdinalIgnoreCase);

            foreach (var grp in annsByImage)
            {
                string imageName = grp.Key;

                try
                {
                    // Find source path for this image name
                    var srcPath = (project.ImagePaths ?? Enumerable.Empty<string>())
                        .FirstOrDefault(p => System.IO.Path.GetFileName(p).Equals(imageName, StringComparison.OrdinalIgnoreCase));

                    if (string.IsNullOrEmpty(srcPath) || !File.Exists(srcPath))
                    {
                        Debug.WriteLine($"Export skipped: image file not found for annotation entries: {imageName}");
                        continue;
                    }

                    string destImage = System.IO.Path.Combine(imagesOut, imageName);
                    File.Copy(srcPath, destImage, overwrite: true);

                    var lines = new List<string>();

                    foreach (var ann in grp)
                    {
                        int classId = 0;
                        if (!string.IsNullOrEmpty(ann.Label) && classLabels != null && classLabels.Count > 0)
                        {
                            int idx = classLabels.FindIndex(l => string.Equals(l, ann.Label, StringComparison.OrdinalIgnoreCase));
                            classId = idx >= 0 ? idx : 0;
                        }

                        // Prefer RawValues for OBB (8 values), otherwise flatten Points as fallback.
                        if (ann.AnnotationType == AnnotationType.RotatedBox && ann.RawValues != null && ann.RawValues.Count == 8)
                        {
                            var parts = ann.RawValues.Select(v => v.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                            lines.Add(classId + " " + string.Join(" ", parts));
                        }
                        else if (ann.Points != null && ann.Points.Count >= 4)
                        {
                            var pts = ann.Points.Take(4).SelectMany(p => new[] {
                        p.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        p.Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    });
                            lines.Add(classId + " " + string.Join(" ", pts));
                        }
                        else if (ann.Points != null && ann.Points.Count > 0)
                        {
                            var pts = ann.Points.SelectMany(p => new[] {
                        p.X.ToString("F2", System.Globalization.CultureInfo.InvariantCulture),
                        p.Y.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)
                    });
                            lines.Add(classId + " " + string.Join(" ", pts));
                        }
                        else
                        {
                            // unsupported annotation -> skip this annotation record
                            continue;
                        }
                    }

                    // Only write label file if there is at least one valid annotation line
                    if (lines.Count > 0)
                    {
                        string labelFile = System.IO.Path.Combine(labelsOut, System.IO.Path.ChangeExtension(imageName, ".txt"));
                        File.WriteAllLines(labelFile, lines);
                    }
                    else
                    {
                        Debug.WriteLine($"No valid annotations to write for image: {imageName}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Export error for {imageName}: {ex}");
                    // continue with other images
                }
            }

            // Write a minimal data.yaml for training convenience
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"path: {outputFolder.Replace("\\", "/")}");
                sb.AppendLine($"train: ./images"); // using same folder for train (no split)
                sb.AppendLine($"val: ./images");
                sb.AppendLine("names:");
                if (classLabels != null && classLabels.Count > 0)
                {
                    for (int i = 0; i < classLabels.Count; i++)
                        sb.AppendLine($"  {i}: {classLabels[i]}");
                }
                else
                {
                    sb.AppendLine("  0: class0");
                }

                File.WriteAllText(System.IO.Path.Combine(outputFolder, "data.yaml"), sb.ToString());
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to write data.yaml: {ex}");
            }
        }

        private (bool ok, string report) VerifyObbFolder(string folderPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
                    return (false, "Folder does not exist.");

                // Search recursively because many OBB datasets store label files in nested folders (e.g. "labels/")
                var txtFiles = Directory.EnumerateFiles(folderPath, "*.txt", SearchOption.AllDirectories).ToList();
                if (txtFiles.Count == 0)
                    return (false, "No .txt annotation files found in the selected folder (searched recursively). " +
                                   "Make sure you selected the folder that contains the label .txt files (or the dataset root).");

                int totalFiles = txtFiles.Count;
                int totalLines = 0;
                int badLines = 0;
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Found {totalFiles} .txt files (searching recursively). Showing up to first 20 problems:");

                // Validate lines: accept files where last 8 tokens are numeric (handles optional leading class token).
                foreach (var f in txtFiles)
                {
                    string[] lines;
                    try
                    {
                        lines = File.ReadAllLines(f);
                    }
                    catch (Exception ex)
                    {
                        badLines++;
                        sb.AppendLine($"{SWPath.GetFileName(f)}: FAILED to read file ({ex.Message})");
                        continue;
                    }

                    for (int i = 0; i < lines.Length; i++)
                    {
                        var line = lines[i].Trim();
                        if (string.IsNullOrEmpty(line)) continue;
                        totalLines++;

                        // split on whitespace and commas
                        var tokens = line.Split(new[] { ' ', '\t', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (tokens.Length < 8)
                        {
                            badLines++;
                            if (sb.Length < 8000) sb.AppendLine($"{SWPath.GetFileName(f)}: line {i + 1} has {tokens.Length} tokens (expected >=8).");
                            continue;
                        }

                        // Look at the last 8 tokens (common OBB forms contain 8 coords; some files might include a class token at start)
                        int startIdx = tokens.Length - 8;
                        bool allNumeric = true;
                        for (int t = startIdx; t < tokens.Length; t++)
                        {
                            if (!double.TryParse(tokens[t], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
                            {
                                allNumeric = false;
                                break;
                            }
                        }

                        if (!allNumeric)
                        {
                            badLines++;
                            if (sb.Length < 8000) sb.AppendLine($"{SWPath.GetFileName(f)}: line {i + 1} contains non-numeric coordinate(s).");
                        }
                    }
                }

                if (badLines == 0)
                    return (true, $"OK: {totalFiles} .txt files, {totalLines} annotation lines validated.");
                else
                    return (false, $"Found {badLines} invalid lines across {totalFiles} files:\n{sb}");
            }
            catch (Exception ex)
            {
                return (false, $"Verification failed: {ex.Message}");
            }
        }


        #endregion




        private async Task AugmentCurrentProjectImagesAsync()
        {
            if (_currentProject == null || _imagePaths == null || _imagePaths.Count == 0)
            {
                SetStatus("No project or images to augment.");
                return;
            }

            SetStatus("Starting augmentation...");

            var originalList = _imagePaths.ToList(); // freeze list

            var augList = new List<(string suffix, Matrix m)>
    {
        ("_flipH", VisionAICam.Services.ImageAugmentation.FlipHorizontalMatrix(_currentImageWidth)),
        ("_flipV", VisionAICam.Services.ImageAugmentation.FlipVerticalMatrix(_currentImageHeight)),
        ("_rot90", VisionAICam.Services.ImageAugmentation.Rotate90CWMatrix(_currentImageWidth, _currentImageHeight)),
        ("_rot180", VisionAICam.Services.ImageAugmentation.Rotate180Matrix(_currentImageWidth, _currentImageHeight)),
        ("_rot270", VisionAICam.Services.ImageAugmentation.Rotate270CWMatrix(_currentImageWidth, _currentImageHeight)),
        ("_shearX", VisionAICam.Services.ImageAugmentation.ShearXMatrix(_currentImageWidth, _currentImageHeight, 0.2)),
        ("_shearY", VisionAICam.Services.ImageAugmentation.ShearYMatrix(_currentImageWidth, _currentImageHeight, 0.2)),
        ("_translate", VisionAICam.Services.ImageAugmentation.TranslateMatrix(20, 20)),
        ("_persp", VisionAICam.Services.ImageAugmentation.PerspectiveMatrix(_currentImageWidth, _currentImageHeight, 0.05))
    };

            try
            {
                foreach (var srcPath in originalList)
                {
                    if (!File.Exists(srcPath)) continue;

                    BitmapSource srcBmp = null;
                    await Task.Run(() =>
                    {
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.UriSource = new Uri(srcPath);
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.EndInit();
                        bmp.Freeze();
                        srcBmp = bmp;
                    });

                    double w = srcBmp.PixelWidth;
                    double h = srcBmp.PixelHeight;
                    string baseName = System.IO.Path.GetFileNameWithoutExtension(srcPath);
                    string ext = ".png";

                    // same folder as source
                    string folder = SWPath.GetDirectoryName(srcPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

                    // collect annotations for this image (filename-match)
                    var imageName = System.IO.Path.GetFileName(srcPath);
                    var annsForImage = Annotations.Where(a => string.Equals(a.ImageName, imageName, StringComparison.OrdinalIgnoreCase)).ToList();

                    foreach (var (suffix, matrix) in augList)
                    {
                        BitmapSource transformed = null;
                        try
                        {
                            transformed = await Task.Run(() => VisionAICam.Services.ImageAugmentation.TransformBitmap(srcBmp, matrix));
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Transform failed for {srcPath} {suffix}: {ex}");
                            continue;
                        }

                        // ensure unique filename in same folder
                        string outFile = System.IO.Path.Combine(folder, $"{baseName}{suffix}{ext}");
                        int counter = 1;
                        while (File.Exists(outFile))
                        {
                            outFile = System.IO.Path.Combine(folder, $"{baseName}{suffix}_{counter}{ext}");
                            counter++;
                        }

                        try
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(transformed));
                            using var fs = File.Open(outFile, FileMode.Create, FileAccess.Write, FileShare.None);
                            encoder.Save(fs);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to save augmented image {outFile}: {ex}");
                            continue;
                        }

                        // add augmented image to project and local lists
                        _currentProject.ImagePaths.Add(outFile);
                        _imagePaths.Add(outFile);

                        // transform and copy annotations (if any)
                        foreach (var ann in annsForImage)
                        {
                            var newAnn = new AnnotationRecord
                            {
                                ImageName = System.IO.Path.GetFileName(outFile),
                                Label = ann.Label,
                                AnnotationType = ann.AnnotationType,
                                Points = ann.Points != null ? ann.Points.Select(p => new Point(p.X, p.Y)).ToList() : new List<Point>(),
                                RawValues = ann.RawValues != null ? new List<double>(ann.RawValues) : null
                            };

                            if (newAnn.Points != null && newAnn.Points.Count > 0)
                            {
                                newAnn.Points = VisionAICam.Services.ImageAugmentation.TransformPoints(newAnn.Points, matrix, w, h);
                            }

                            if (newAnn.RawValues != null && newAnn.RawValues.Count == 8)
                            {
                                newAnn.RawValues = VisionAICam.Services.ImageAugmentation.TransformRawValuesOBB(newAnn.RawValues, matrix, w, h);
                            }

                            _currentProject.Annotations.Add(newAnn);
                            Annotations.Add(newAnn);
                        }

                        // choose mode: ScaleDownIfLarger keeps small images padded (black) and scales down large ones.
                        CropPadScaleMode mode = CropPadScaleMode.ScaleDownIfLarger;

                        int offsetX = 0, offsetY = 0;
                        transformed = EnsureCropPadScale(transformed, (int)w, (int)h, mode, out offsetX, out offsetY);

                        // save final image
                        try
                        {
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(transformed));
                            using var fs = File.Open(outFile, FileMode.Create, FileAccess.Write, FileShare.None);
                            encoder.Save(fs);
                        }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"Failed to save final augmented image {outFile}: {ex}");
                            continue;
                        }

                        // adjust annotation points for offset (transformPoints subtracts offset)
                        foreach (var ann in annsForImage)
                        {
                            if (ann.Points != null && ann.Points.Count > 0)
                            {
                                for (int i = 0; i < ann.Points.Count; i++)
                                {
                                    var p = ann.Points[i];
                                    ann.Points[i] = new Point(p.X - offsetX, p.Y - offsetY);
                                }
                            }

                            if (ann.RawValues != null && ann.RawValues.Count == 8)
                            {
                                // OBB: apply offset to all 8 values
                                for (int i = 0; i < 8; i += 2)
                                {
                                    ann.RawValues[i] -= offsetX;
                                    ann.RawValues[i + 1] -= offsetY;
                                }
                            }
                        }
                    }
                }

                // persist session
                ProjectSession.CurrentProject = _currentProject;
                ProjectSession.Annotations = Annotations;
                ProjectSession.ImagePaths = _imagePaths;

                // Ensure UI reflects the new image count and annotations
                Dispatcher.Invoke(() =>
                {
                    RefreshAnnotations();
                    UpdateClassStats();

                    // Update the image progress text so the new total appears immediately.
                    if (ImageProgressText != null)
                    {
                        if (_imagePaths != null && _imagePaths.Count > 0 && _currentImageIndex >= 0 && _currentImageIndex < _imagePaths.Count)
                        {
                            ImageProgressText.Text = $"Image {_currentImageIndex + 1} of {_imagePaths.Count} ({(int)(((_currentImageIndex + 1) * 100.0) / _imagePaths.Count)}%) - {System.IO.Path.GetFileName(_currentImagePath ?? "")}";
                        }
                        else
                        {
                            ImageProgressText.Text = $"No images loaded";
                        }
                    }

                    SetStatus($"Augmentation finished. {_imagePaths.Count} images in project. Augmented images saved next to originals.");
                });
            }
            catch (Exception ex)
            {
                SetStatus($"Augmentation failed: {ex.Message}");
            }
        }

        // place inside the DataSetPage class (near other helpers)
        private enum CropPadScaleMode
        {
            None,               // no scaling: center, crop if larger, pad if smaller
            ScaleDownIfLarger,  // scale down when transformed image is larger than target; never upscale
            ScaleToFill         // scale so image covers target (may crop), useful when you want "cover" behavior
        }

        // Replace previous EnsureCropAndPad/EnsureBitmapSize uses with this single helper.
        // Returns final frozen BitmapSource sized to targetWidth/targetHeight and out offsets
        // offsetX/offsetY such that finalPoint = transformedPoint - offset (useful to adjust annotation coords).
        private BitmapSource EnsureCropPadScale(BitmapSource src, int targetWidth, int targetHeight, CropPadScaleMode mode, out int offsetX, out int offsetY)
        {
            offsetX = 0;
            offsetY = 0;

            if (src == null)
                return null;

            int srcW = src.PixelWidth;
            int srcH = src.PixelHeight;

            // If exact match, nothing to do.
            if (srcW == targetWidth && srcH == targetHeight)
            {
                if (!src.IsFrozen) src.Freeze();
                offsetX = 0;
                offsetY = 0;
                return src;
            }

            double drawLeft = 0.0;
            double drawTop = 0.0;
            double drawW = srcW;
            double drawH = srcH;

            switch (mode)
            {
                case CropPadScaleMode.None:
                    // no scaling, draw at native size centered -> crop if larger, pad if smaller
                    drawW = srcW;
                    drawH = srcH;
                    drawLeft = (targetWidth - drawW) / 2.0;
                    drawTop = (targetHeight - drawH) / 2.0;
                    break;

                case CropPadScaleMode.ScaleDownIfLarger:
                    // scale down so the transformed image fits inside target when it is larger.
                    // Do NOT upscale small images (user asked to fill with black instead of upscaling).
                    {
                        double scale = Math.Min(1.0, Math.Min((double)targetWidth / srcW, (double)targetHeight / srcH));
                        drawW = Math.Max(1.0, srcW * scale);
                        drawH = Math.Max(1.0, srcH * scale);
                        drawLeft = (targetWidth - drawW) / 2.0;
                        drawTop = (targetHeight - drawH) / 2.0;
                    }
                    break;

                case CropPadScaleMode.ScaleToFill:
                    // scale so image covers the target (may crop). Upscales small images as needed.
                    {
                        double scale = Math.Max((double)targetWidth / srcW, (double)targetHeight / srcH);
                        drawW = Math.Max(1.0, srcW * scale);
                        drawH = Math.Max(1.0, srcH * scale);
                        drawLeft = (targetWidth - drawW) / 2.0;
                        drawTop = (targetHeight - drawH) / 2.0;
                    }
                    break;
            }

            // offset used for annotation adjustment: finalPoint = transformedPoint + drawOffset
            // we follow existing convention where caller subtracts offsetX/offsetY from transformed points,
            // therefore provide offsetX = -drawLeft
            offsetX = (int)Math.Round(-drawLeft);
            offsetY = (int)Math.Round(-drawTop);

            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                // black background
                dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, targetWidth, targetHeight));
                // draw (scaled) transformed image at computed position; DrawImage will crop automatically if negative offsets
                dc.DrawImage(src, new Rect(drawLeft, drawTop, drawW, drawH));
            }

            var rtb = new RenderTargetBitmap(
                Math.Max(1, targetWidth),
                Math.Max(1, targetHeight),
                src.DpiX,
                src.DpiY,
                PixelFormats.Pbgra32);

            RenderOptions.SetBitmapScalingMode(rtb, BitmapScalingMode.HighQuality);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
