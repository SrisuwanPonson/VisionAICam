using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using OpenCvSharp;
using System.Management;
using VisionAICam; // For AppSettings and SettingsManager
using ClearEngine.Devices.Camera; // class library
using WpfSize = System.Windows.Size;
using System.Windows.Media;

namespace VisionAICam.Pages
{
    public partial class CameraPage : Page
    {
        private ICamera? _camera;
        private AppSettings? _appSettings;

        // Prevent re-running initialClass more than one per application run
        private bool _initialClassesLoaded = false;

        // Suppress saving while programmatically setting slider values
        private bool _suspendSliderSave = false;

        // Track pending (unsaved) camera slider values and dirty state
        private bool _cameraSettingsDirty = false;
        private double _pendingBrightness = 128.0;
        private double _pendingContrast = 128.0;
        private double _pendingExposure = -6.0;

        private List<System.Windows.Rect>? _overlaySourceRects;
        private System.Windows.Shapes.Rectangle[]? _overlayRects;
        private TextBlock[]? _overlayLabels;
        private string[]? _overlayLabelTexts;

        // Overlays for calibration preview
        private System.Windows.Shapes.Rectangle[]? _calibOverlayRects;
        private TextBlock[]? _calibOverlayLabels;

        private BitmapSource? _lastFrame;

        // Reference color tuples and small UI helpers
        private (byte R, byte G, byte B)? _refRed;
        private (byte R, byte G, byte B)? _refGreen;
        private (byte R, byte G, byte B)? _refBlue;
        double toleranceR;
        double toleranceG;
        double toleranceB;

        // TaskCompletionSource for inline decision panel
        private TaskCompletionSource<MessageBoxResult>? _calibDecisionTcs;
        private TaskCompletionSource<bool>? _samplePrepareTcs;

        public CameraPage()
        {
            InitializeComponent();

            _appSettings = SettingsManager.Load();

            // load persisted per-channel tolerances (fallback to 5.0)
            try
            {
                toleranceR = _appSettings?.TolerancePercentR ?? 20.0;
                toleranceG = _appSettings?.TolerancePercentG ?? 20.0;
                toleranceB = _appSettings?.TolerancePercentB ?? 20.0;
            }
            catch
            {
                toleranceR = toleranceG = toleranceB = 15.0;
            }

            // Apply persisted reference colors to runtime refs and UI rects
            try
            {
                if (_appSettings != null)
                {
                    _refRed = (_appSettings.RefRed.R, _appSettings.RefRed.G, _appSettings.RefRed.B);
                    _refGreen = (_appSettings.RefGreen.R, _appSettings.RefGreen.G, _appSettings.RefGreen.B);
                    _refBlue = (_appSettings.RefBlue.R, _appSettings.RefBlue.G, _appSettings.RefBlue.B);

                    try { ApplyColorToRect(RectRed, _refRed.Value); } catch { }
                    try { ApplyColorToRect(RectGreen, _refGreen.Value); } catch { }
                    try { ApplyColorToRect(RectBlue, _refBlue.Value); } catch { }
                }
            }
            catch { /* tolerate settings read errors */ }

            DiscoverAndPopulateCameras();
            initialClass();

            Unloaded += CameraPage_Unloaded;
            IsVisibleChanged += CameraPage_IsVisibleChanged;
            Loaded += CameraPage_Loaded;

            CameraImage.SizeChanged += CameraImage_SizeChanged;
            CameraImage.LayoutUpdated += CameraImage_LayoutUpdated;

            // ensure calibration preview follows layout changes as well
            CalibImage.SizeChanged += CalibImage_SizeChanged;
            CalibImage.LayoutUpdated += CameraImage_LayoutUpdated;

            if (_appSettings != null)
            {
                _suspendSliderSave = true;
                try
                {
                    BrightnessSlider.Value = _appSettings!.Brightness;
                    ContrastSlider.Value = _appSettings!.Contrast;
                    ExposureSlider.Value = _appSettings!.Exposure;

                    // initialize pending values from persisted settings so explicit Save is meaningful
                    _pendingBrightness = BrightnessSlider.Value;
                    _pendingContrast = ContrastSlider.Value;
                    _pendingExposure = ExposureSlider.Value;
                }
                catch { }
                finally { _suspendSliderSave = false; }
            }

            // Ensure Save button state consistent at startup
            try
            {
                if (SaveCameraButton != null) SaveCameraButton.IsEnabled = false;
                if (SaveStatusSmall != null) SaveStatusSmall.Text = "";
                if (lblSaveStatus != null && string.IsNullOrEmpty(lblSaveStatus.Text)) lblSaveStatus.Text = "";
            }
            catch { }
        }

        private void CalibImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_overlaySourceRects != null && _overlaySourceRects.Count > 0)
                ShowOverlayCanvasRects(_overlaySourceRects);
        }

        private void CalibImage_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_overlaySourceRects != null && _overlaySourceRects.Count > 0)
                ShowOverlayCanvasRects(_overlaySourceRects);
        }

        private void initialClass()
        {
            if (_initialClassesLoaded) return;

            string classFile = "class_list.txt";
            string categoryFile = "category_list.txt";

            try
            {
                if (ClassComboBox != null)
                {
                    ClassComboBox.ItemsSource = null;
                    ClassComboBox.Items.Clear();
                }

                if (CategoryComboBox != null)
                {
                    CategoryComboBox.ItemsSource = null;
                    CategoryComboBox.Items.Clear();
                }

                if (System.IO.File.Exists(classFile))
                {
                    var classes = System.IO.File.ReadAllLines(classFile);
                    if (ClassComboBox != null)
                        ClassComboBox.ItemsSource = classes;
                }
                else
                {
                    var defaults = new[] { "WallPlug", "Screw", "Anchor", "Bracket", "Clip" };
                    System.IO.File.WriteAllLines(classFile, defaults);
                    if (ClassComboBox != null)
                        ClassComboBox.ItemsSource = defaults;
                }

                if (System.IO.File.Exists(categoryFile))
                {
                    var categories = System.IO.File.ReadAllLines(categoryFile);
                    if (CategoryComboBox != null)
                        CategoryComboBox.ItemsSource = categories;
                }
                else
                {
                    var defaults = new[] { "Fastener", "Anchor", "Fixture", "Electrical", "Tool" };
                    System.IO.File.WriteAllLines(categoryFile, defaults);
                    if (CategoryComboBox != null)
                        CategoryComboBox.ItemsSource = defaults;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to initialize class/category lists: {ex.Message}", "Initialization Error", MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            _initialClassesLoaded = true;
        }

        private void CameraImage_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (_overlaySourceRects != null && _overlaySourceRects.Count > 0)
                ShowOverlayCanvasRects(_overlaySourceRects);
        }

        private void CameraImage_LayoutUpdated(object? sender, EventArgs e)
        {
            if (_overlaySourceRects != null && _overlaySourceRects.Count > 0)
                ShowOverlayCanvasRects(_overlaySourceRects);
        }

        private void CameraPage_Loaded(object sender, RoutedEventArgs e) { }

        private void CameraPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (e.NewValue is bool isVisible && !isVisible) StopCamera();
        }

        private void CameraPage_Unloaded(object sender, RoutedEventArgs e)
        {
            StopCamera();

            Unloaded -= CameraPage_Unloaded;
            IsVisibleChanged -= CameraPage_IsVisibleChanged;
            Loaded -= CameraPage_Loaded;
        }

        private void StopCamera()
        {
            try
            {
                if (_camera != null)
                {
                    try { _camera.FrameReady -= OnFrameReady; } catch { }
                    try { _camera.Stop(); } catch { }
                    try { _camera.Dispose(); } catch { }
                    _camera = null;
                }
            }
            catch { }
            finally
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { CameraImage.Source = null; } catch { }
                    try { CalibImage.Source = null; } catch { }
                });
            }
        }

        private void OnFrameReady(BitmapSource bitmap)
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (!IsLoaded) return;

                try
                {
                    var copy = bitmap.Clone();
                    if (copy.CanFreeze) copy.Freeze();
                    _lastFrame = copy;

                    // show in main preview
                    CameraImage.Source = copy;

                    // show same image in calibrate preview
                    // reusing the same frozen BitmapSource is fine for both Image controls
                    CalibImage.Source = copy;

                    // re-draw overlays on both canvases if boxes exist
                    if (_overlaySourceRects != null && _overlaySourceRects.Count > 0)
                        ShowOverlayCanvasRects(_overlaySourceRects);
                }
                catch
                {
                    try { CameraImage.Source = bitmap; } catch { }
                    try { CalibImage.Source = bitmap; } catch { }
                    _lastFrame = bitmap;
                }
            });
        }

        private void DiscoverAndPopulateCameras()
        {
            CameraComboBox.Items.Clear();
            var devices = new List<string>();
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT * FROM Win32_PnPEntity WHERE (PNPClass = 'Image' OR PNPClass = 'Camera')");
                foreach (ManagementObject device in searcher.Get())
                {
                    var name = device["Name"]?.ToString();
                    if (!string.IsNullOrEmpty(name)) devices.Add(name);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"WMI device discovery error: {ex.Message}");
            }

            foreach (var device in devices)
                CameraComboBox.Items.Add(new ComboBoxItem { Content = device });

            if (CameraComboBox.Items.Count == 0)
            {
                MessageBox.Show("No imaging devices or cameras detected.");
                CameraComboBox.SelectedIndex = -1;
            }
            else
            {
                if (_appSettings != null && _appSettings.CameraIndex >= 0 && _appSettings.CameraIndex < CameraComboBox.Items.Count)
                    CameraComboBox.SelectedIndex = _appSettings.CameraIndex;
                else
                    CameraComboBox.SelectedIndex = 0;
            }
        }

        private void CameraComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_appSettings == null) return;
            _appSettings.CameraIndex = CameraComboBox.SelectedIndex;
            try { SettingsManager.Save(_appSettings); } catch { }
        }

        private void SelectCameraButton_Click_1(object sender, RoutedEventArgs e)
        {
            StopCamera();
            _appSettings = SettingsManager.Load();

            int camIndex = CameraComboBox.SelectedIndex;
            if (camIndex < 0)
            {
                MessageBox.Show("Please select a camera.");
                return;
            }

            try
            {
                _camera = CameraFactory.Create(CameraBackend.OpenCv);
                _camera.FrameReady += OnFrameReady;

                var options = _appSettings != null
                    ? new CameraOptions { Brightness = _appSettings.Brightness, Contrast = _appSettings.Contrast, Exposure = _appSettings.Exposure }
                    : null;

                _camera.Start(camIndex, options);

                if (!_camera.IsOpened)
                {
                    MessageBox.Show("Could not open selected camera.");
                    StopCamera();
                    return;
                }

                _suspendSliderSave = true;
                Dispatcher.Invoke(() =>
                {
                    try
                    {
                        BrightnessSlider.Value = _camera.GetProperty(VideoCaptureProperties.Brightness);
                        ContrastSlider.Value = _camera.GetProperty(VideoCaptureProperties.Contrast);
                        ExposureSlider.Value = _camera.GetProperty(VideoCaptureProperties.Exposure);

                        // ensure pending values reflect actual device values
                        _pendingBrightness = BrightnessSlider.Value;
                        _pendingContrast = ContrastSlider.Value;
                        _pendingExposure = ExposureSlider.Value;

                        // clear dirty state after syncing from device
                        _cameraSettingsDirty = false;
                        if (SaveCameraButton != null) SaveCameraButton.IsEnabled = false;
                        if (SaveStatusSmall != null) SaveStatusSmall.Text = "";
                    }
                    catch { }
                });
                _suspendSliderSave = false;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to start camera: {ex.Message}", "Camera Error", MessageBoxButton.OK, MessageBoxImage.Error);
                StopCamera();
            }
        }

        private void StopCameraButton_Click(object sender, RoutedEventArgs e) => StopCamera();

        private void SnapshotButton_Click(object sender, RoutedEventArgs e)
        {
            Mat? mat = null;
            try
            {
                mat = _camera?.CaptureCurrentFrame();
                if (mat == null)
                {
                    MessageBox.Show("Camera is not running.", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                string basePath = _appSettings?.DefaultImagePath ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                string folderName = $"captureImage_{DateTime.Now:yyyyMMdd}";
                string savePath = System.IO.Path.Combine(basePath, folderName);

                if (!System.IO.Directory.Exists(savePath))
                    System.IO.Directory.CreateDirectory(savePath);

                string className = ClassComboBox.Text ?? "Class";
                string category = CategoryComboBox.Text ?? "Category";
                string fileName = $"{className}_{category}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string filePath = System.IO.Path.Combine(savePath, fileName);

                mat.SaveImage(filePath);
                MessageBox.Show($"Snapshot saved to {filePath}.", "Snapshot", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Failed to save snapshot: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                try { mat?.Dispose(); } catch { }
            }
        }

        private void BrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendSliderSave) return;

            try
            {
                // Apply to live camera immediately if running
                if (_camera != null && _camera.IsOpened)
                    _camera.SetProperty(VideoCaptureProperties.Brightness, e.NewValue);
            }
            catch { }

            // store pending value until user explicitly saves
            _pendingBrightness = e.NewValue;
            _cameraSettingsDirty = true;
            try
            {
                if (SaveCameraButton != null) SaveCameraButton.IsEnabled = true;
                if (SaveStatusSmall != null) SaveStatusSmall.Text = "Modified";
                if (lblSaveStatus != null) lblSaveStatus.Text = "Modified";
            }
            catch { }
        }

        private void ContrastSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendSliderSave) return;

            try
            {
                if (_camera != null && _camera.IsOpened)
                    _camera.SetProperty(VideoCaptureProperties.Contrast, e.NewValue);
            }
            catch { }

            _pendingContrast = e.NewValue;
            _cameraSettingsDirty = true;
            try
            {
                if (SaveCameraButton != null) SaveCameraButton.IsEnabled = true;
                if (SaveStatusSmall != null) SaveStatusSmall.Text = "Modified";
                if (lblSaveStatus != null) lblSaveStatus.Text = "Modified";
            }
            catch { }
        }

        private void ExposureSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (_suspendSliderSave) return;

            try
            {
                if (_camera != null && _camera.IsOpened)
                    _camera.SetProperty(VideoCaptureProperties.Exposure, e.NewValue);
            }
            catch { }

            _pendingExposure = e.NewValue;
            _cameraSettingsDirty = true;
            try
            {
                if (SaveCameraButton != null) SaveCameraButton.IsEnabled = true;
                if (SaveStatusSmall != null) SaveStatusSmall.Text = "Modified";
                if (lblSaveStatus != null) lblSaveStatus.Text = "Modified";
            }
            catch { }
        }

        private (byte R, byte G, byte B)? SampleMatPatch(OpenCvSharp.Rect roi)
        {
            Mat? mat = _camera?.CaptureCurrentFrame();
            if (mat == null) return null;

            try
            {
                int x = Math.Max(0, Math.Min(roi.X, mat.Width - 1));
                int y = Math.Max(0, Math.Min(roi.Y, mat.Height - 1));
                int w = Math.Max(1, Math.Min(roi.Width, mat.Width - x));
                int h = Math.Max(1, Math.Min(roi.Height, mat.Height - y));
                var rroi = new OpenCvSharp.Rect(x, y, w, h);

                using var patch = new Mat(mat, rroi);
                var mean = Cv2.Mean(patch);

                byte b = (byte)Math.Clamp((int)Math.Round(mean.Val0), 0, 255);
                byte g = (byte)Math.Clamp((int)Math.Round(mean.Val1), 0, 255);
                byte r = (byte)Math.Clamp((int)Math.Round(mean.Val2), 0, 255);

                return (r, g, b);
            }
            catch
            {
                return null;
            }
            finally
            {
                mat.Dispose();
            }
        }

        private void DrawThreeRectsOnImage(int patchSize = 80)
        {
            if (_lastFrame == null) return;

            int imgW = _lastFrame.PixelWidth;
            int imgH = _lastFrame.PixelHeight;
            int half = Math.Max(1, patchSize / 2);

            var sourceRects = new List<System.Windows.Rect>
            {
                new System.Windows.Rect(Math.Max(0, (int)Math.Round(imgW * 0.25) - half), Math.Max(0, (int)Math.Round(imgH * 0.5) - half), patchSize, patchSize),
                new System.Windows.Rect(Math.Max(0, (int)Math.Round(imgW * 0.50) - half), Math.Max(0, (int)Math.Round(imgH * 0.5) - half), patchSize, patchSize),
                new System.Windows.Rect(Math.Max(0, (int)Math.Round(imgW * 0.75) - half), Math.Max(0, (int)Math.Round(imgH * 0.5) - half), patchSize, patchSize)
            };

            _overlaySourceRects = sourceRects;
            ShowOverlayCanvasRects(sourceRects);
        }

        // Render overlays on both main preview and calibrate preview so boxes appear in both places.
        private void ShowOverlayCanvasRects(List<System.Windows.Rect> sourceRects)
        {
            // render on main preview
            try
            {
                RenderOverlayFor(CameraImage, OverlayCanvas, ref _overlayRects, ref _overlayLabels, sourceRects);
            }
            catch { /* tolerate errors */ }

            // render on calibration preview
            try
            {
                RenderOverlayFor(CalibImage, CalibOverlayCanvas, ref _calibOverlayRects, ref _calibOverlayLabels, sourceRects);
            }
            catch { /* tolerate errors */ }
        }

        // Generic renderer to draw rectangles and labels for a given Image/Canvas pair.
        private void RenderOverlayFor(Image img, Canvas canvas, ref System.Windows.Shapes.Rectangle[]? rectsRef, ref TextBlock[]? labelsRef, List<System.Windows.Rect> sourceRects)
        {
            if (img == null || canvas == null || _lastFrame == null) return;

            try
            {
                if (img.ActualWidth <= 0 || img.ActualHeight <= 0)
                {
                    img.UpdateLayout();
                    canvas.UpdateLayout();
                }

                double srcW = _lastFrame.PixelWidth;
                double srcH = _lastFrame.PixelHeight;
                double ctrlW = img.ActualWidth;
                double ctrlH = img.ActualHeight;

                if (srcW <= 0 || srcH <= 0 || ctrlW <= 0 || ctrlH <= 0) return;

                double scale = Math.Min(ctrlW / srcW, ctrlH / srcH);
                double dispW = srcW * scale;
                double dispH = srcH * scale;
                double offsetX = (ctrlW - dispW) / 2.0;
                double offsetY = (ctrlH - dispH) / 2.0;

                canvas.Width = Math.Max(canvas.Width, img.ActualWidth);
                canvas.Height = Math.Max(canvas.Height, img.ActualHeight);

                if (rectsRef == null)
                {
                    rectsRef = new System.Windows.Shapes.Rectangle[3];
                    labelsRef = new TextBlock[3];
                    for (int i = 0; i < 3; i++)
                    {
                        var rect = new System.Windows.Shapes.Rectangle
                        {
                            Fill = System.Windows.Media.Brushes.Transparent,
                            StrokeThickness = 3,
                            Visibility = Visibility.Collapsed
                        };
                        canvas.Children.Add(rect);
                        rectsRef[i] = rect;

                        var tb = new TextBlock
                        {
                            FontSize = 12,
                            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(200, 0, 0, 0)),
                            Foreground = System.Windows.Media.Brushes.White,
                            Padding = new Thickness(4, 2, 4, 2),
                            TextWrapping = TextWrapping.Wrap,
                            Visibility = Visibility.Collapsed
                        };
                        canvas.Children.Add(tb);
                        labelsRef[i] = tb;
                    }
                }

                System.Windows.Media.Brush[] strokeBrushes = new System.Windows.Media.Brush[3];
                try
                {
                    strokeBrushes[0] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(236, 10, 10));
                    strokeBrushes[1] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 236, 10));
                    strokeBrushes[2] = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(10, 10, 236));
                    foreach (var b in strokeBrushes) if (b is System.Windows.Media.SolidColorBrush sb) sb.Freeze();
                }
                catch
                {
                    strokeBrushes[0] = System.Windows.Media.Brushes.Red;
                    strokeBrushes[1] = System.Windows.Media.Brushes.Lime;
                    strokeBrushes[2] = System.Windows.Media.Brushes.DodgerBlue;
                }

                GeneralTransform imageToCanvas;
                try { imageToCanvas = img.TransformToVisual(canvas); }
                catch { imageToCanvas = null!; }

                for (int i = 0; i < 3; i++)
                {
                    if (i >= sourceRects.Count)
                    {
                        if (rectsRef[i] != null) rectsRef[i].Visibility = Visibility.Collapsed;
                        if (labelsRef[i] != null) labelsRef[i].Visibility = Visibility.Collapsed;
                        continue;
                    }

                    var s = sourceRects[i];

                    var tlImg = new System.Windows.Point(offsetX + s.X * scale, offsetY + s.Y * scale);
                    var brImg = new System.Windows.Point(offsetX + (s.X + s.Width) * scale, offsetY + (s.Y + s.Height) * scale);

                    System.Windows.Point tlCanvas, brCanvas;
                    try
                    {
                        if (imageToCanvas != null)
                        {
                            tlCanvas = imageToCanvas.Transform(tlImg);
                            brCanvas = imageToCanvas.Transform(brImg);
                        }
                        else
                        {
                            tlCanvas = tlImg;
                            brCanvas = brImg;
                        }
                    }
                    catch
                    {
                        tlCanvas = tlImg;
                        brCanvas = brImg;
                    }

                    double left = Math.Min(tlCanvas.X, brCanvas.X);
                    double top = Math.Min(tlCanvas.Y, brCanvas.Y);
                    double w = Math.Max(2.0, Math.Abs(brCanvas.X - tlCanvas.X));
                    double h = Math.Max(2.0, Math.Abs(brCanvas.Y - tlCanvas.Y));

                    var rect = rectsRef[i];
                    rect.Width = w;
                    rect.Height = h;
                    rect.Stroke = strokeBrushes[Math.Min(i, strokeBrushes.Length - 1)];
                    Canvas.SetLeft(rect, left);
                    Canvas.SetTop(rect, top);
                    rect.Visibility = Visibility.Visible;

                    var label = labelsRef[i];
                    string? text = ( _overlayLabelTexts != null && i < _overlayLabelTexts.Length) ? _overlayLabelTexts[i] : null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        label.Text = text;
                        label.Measure(new WpfSize(canvas.ActualWidth, canvas.ActualHeight));
                        double lblW = label.DesiredSize.Width;
                        double lblH = label.DesiredSize.Height;

                        double lblLeft = left;
                        double lblTop = top + h + 6;

                        if (lblLeft + lblW > canvas.ActualWidth)
                            lblLeft = Math.Max(2.0, canvas.ActualWidth - lblW - 2.0);

                        if (lblTop + lblH > canvas.ActualHeight)
                            lblTop = Math.Max(2.0, top - lblH - 6.0);

                        Canvas.SetLeft(label, lblLeft);
                        Canvas.SetTop(label, lblTop);
                        label.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        label.Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch
            {
                // tolerate overlay errors
            }
        }

        private MessageBoxResult AskUser(string text, string caption, MessageBoxButton buttons)
        {
            try { return MessageBox.Show(text, caption, buttons, MessageBoxImage.Question); }
            catch { return MessageBoxResult.Cancel; }
        }

        private double ComputeErrorAgainstRefs(List<(byte R, byte G, byte B)?> samples)
        {
            double err = 0.0;
            try
            {
                for (int i = 0; i < samples.Count && i < 3; i++)
                {
                    var s = samples[i];
                    if (!s.HasValue) continue;
                    var (r, g, b) = s.Value;

                    (byte R, byte G, byte B)? tref = i == 0 ? _refRed : i == 1 ? _refGreen : _refBlue;
                    if (!tref.HasValue) continue;
                    var (tr, tg, tb) = tref.Value;

                    err += Math.Pow(r - tr, 2) + Math.Pow(g - tg, 2) + Math.Pow(b - tb, 2);
                }
            }
            catch { }
            return err;
        }

        private async Task<List<(byte R, byte G, byte B)?>> SampleRoisAsync(List<System.Windows.Rect> rois)
        {
            return await Task.Run(() =>
            {
                var results = new List<(byte R, byte G, byte B)?>();

                foreach (var r in rois)
                {
                    var rr = new OpenCvSharp.Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
                    results.Add(SampleMatPatch(rr));
                }

                return results;
            });
        }
        
        private async Task<(bool Success, (byte R, byte G, byte B)? Box1, (byte R, byte G, byte B)? Box2, (byte R, byte G, byte B)? Box3, double Brightness, double Contrast, double Exposure)> AutoTuneToRefsAsync(List<System.Windows.Rect> rois, int tolerance = 2)
        {
            if (_camera == null || !_camera.IsOpened) return (false, null, null, null, 0, 0, 0);
            if (!_refRed.HasValue || !_refGreen.HasValue || !_refBlue.HasValue) return (false, null, null, null, 0, 0, 0);

            try
            {
                // Local readonly reference copies — tuning must not overwrite stored refs.
                var refRedLocal = _refRed.Value;
                var refGreenLocal = _refGreen.Value;
                var refBlueLocal = _refBlue.Value;

                // Read initial camera properties
                double currentB = SafeGet(VideoCaptureProperties.Brightness);
                double currentC = SafeGet(VideoCaptureProperties.Contrast);
                double currentE = SafeGet(VideoCaptureProperties.Exposure);

                double bestB = currentB, bestC = currentC, bestE = currentE;

                // Initial sample + error
                var baseline = await SampleRoisAsync(rois);
                if (baseline == null || baseline.Count < rois.Count)
                    return (false, null, null, null, currentB, currentC, currentE);

                double bestErr = ComputeErrorAgainstRefs(baseline);

                // If already within tolerance, return current readings
                if (bestErr <= tolerance)
                {
                    return (true, baseline.ElementAtOrDefault(0), baseline.ElementAtOrDefault(1), baseline.ElementAtOrDefault(2), currentB, currentC, currentE);
                }

                // Tuning policy:
                // - Try small signed steps for each property (positive & negative) and combinations.
                // - Accept any candidate that reduces the error.
                // - If none improve, reduce the step and try again (finer search).
                // - Stop when error <= tolerance or step becomes very small or max iterations reached.

                const int maxIters = 30;
                const int settleMs = 200;
                double step = 6.0;             // initial step size (camera property units)
                const double minStep = 0.25;   // stop when step smaller than this
                const double stepShrink = 0.5; // shrink factor when no improvement
                const double stepGrow = 1.15;  // grow factor after improvement (bounded)

                var lastReadings = baseline.ToArray();

                for (int iter = 0; iter < maxIters; iter++)
                {
                    // Read latest camera-reported values
                    currentB = SafeGet(VideoCaptureProperties.Brightness);
                    currentC = SafeGet(VideoCaptureProperties.Contrast);
                    currentE = SafeGet(VideoCaptureProperties.Exposure);

                    // Build candidate deltas: try +/- step on each control and useful combinations.
                    var deltas = new List<(double dB, double dC, double dE)>();

                    // Individual moves
                    deltas.Add((step, 0, 0));
                    deltas.Add((-step, 0, 0));
                    deltas.Add((0, step, 0));
                    deltas.Add((0, -step, 0));
                    deltas.Add((0, 0, step));
                    deltas.Add((0, 0, -step));

                    // Pairwise combinations
                    deltas.Add((step, step, 0));
                    deltas.Add((step, -step, 0));
                    deltas.Add((-step, step, 0));
                    deltas.Add((-step, -step, 0));

                    deltas.Add((step, 0, step));
                    deltas.Add((step, 0, -step));
                    deltas.Add((-step, 0, step));
                    deltas.Add((-step, 0, -step));

                    deltas.Add((0, step, step));
                    deltas.Add((0, step, -step));
                    deltas.Add((0, -step, step));
                    deltas.Add((0, -step, -step));

                    // Full combination (coarse)
                    deltas.Add((step, step, step));
                    deltas.Add((-step, -step, -step));
                    deltas.Add((step, -step, step));
                    deltas.Add((-step, step, -step));

                    bool anyImproved = false;
                    double bestCandidateErr = bestErr;
                    double candBestB = bestB, candBestC = bestC, candBestE = bestE;
                    (byte R, byte G, byte B)?[] candBestSamples = lastReadings;

                    // Evaluate each candidate (apply, wait, sample, evaluate), pick the best that improves
                    foreach (var (dB, dC, dE) in deltas)
                    {
                        double tryB = currentB + dB;
                        double tryC = currentC + dC;
                        double tryE = currentE + dE;

                        // Apply candidate
                        SafeSet(VideoCaptureProperties.Brightness, tryB);
                        SafeSet(VideoCaptureProperties.Contrast, tryC);
                        SafeSet(VideoCaptureProperties.Exposure, tryE);

                        // Allow camera to adjust
                        await Task.Delay(settleMs);

                        // Sample and compute error
                        var samples = await SampleRoisAsync(rois);
                        if (samples == null || samples.Count < rois.Count)
                        {
                            // revert to previous camera state and continue
                            SafeSet(VideoCaptureProperties.Brightness, currentB);
                            SafeSet(VideoCaptureProperties.Contrast, currentC);
                            SafeSet(VideoCaptureProperties.Exposure, currentE);
                            await Task.Delay(80);
                            continue;
                        }

                        double err = ComputeErrorAgainstRefs(samples);

                        if (err < bestCandidateErr - 1e-9) // strict improvement
                        {
                            anyImproved = true;
                            bestCandidateErr = err;

                            // read back actual applied properties reported by camera
                            candBestB = SafeGet(VideoCaptureProperties.Brightness);
                            candBestC = SafeGet(VideoCaptureProperties.Contrast);
                            candBestE = SafeGet(VideoCaptureProperties.Exposure);

                            candBestSamples = samples.ToArray();
                        }

                        // Revert to current state before trying next candidate to maintain consistent baseline
                        SafeSet(VideoCaptureProperties.Brightness, currentB);
                        SafeSet(VideoCaptureProperties.Contrast, currentC);
                        SafeSet(VideoCaptureProperties.Exposure, currentE);

                        // brief pause to allow revert to take effect (keeps camera stable between candidates)
                        await Task.Delay(80);
                    }

                    if (anyImproved)
                    {
                        // Accept the best found candidate
                        bestErr = bestCandidateErr;
                        bestB = candBestB;
                        bestC = candBestC;
                        bestE = candBestE;
                        lastReadings = candBestSamples;

                        // Apply accepted values and allow settle
                        SafeSet(VideoCaptureProperties.Brightness, bestB);
                        SafeSet(VideoCaptureProperties.Contrast, bestC);
                        SafeSet(VideoCaptureProperties.Exposure, bestE);
                        await Task.Delay(settleMs);

                        // Slightly increase step to converge faster when progress is being made
                        step = Math.Min(20.0, step * stepGrow);
                    }
                    else
                    {
                        // No candidate improved -> shrink step and try more precise adjustments
                        step = Math.Max(minStep, step * stepShrink);
                    }

                    // Update overlay quickly to show latest measured values and refs
                    try
                    {
                        _overlayLabelTexts ??= new string[rois.Count];
                        for (int i = 0; i < Math.Min(rois.Count, lastReadings.Length); i++)
                        {
                            var v = lastReadings[i];
                            string measText = v.HasValue ? $"Meas:{v.Value.R},{v.Value.G},{v.Value.B}" : "Meas:---";
                            // Correct per-channel reference text (previous code mistakenly used refGreenLocal for red's G/B).
                            string refText = i == 0 ? $"{refRedLocal.R},{refRedLocal.G},{refRedLocal.B}"
                                               : i == 1 ? $"{refGreenLocal.R},{refGreenLocal.G},{refGreenLocal.B}"
                                                        : $"{refBlueLocal.R},{refBlueLocal.G},{refBlueLocal.B}";
                            _overlayLabelTexts[i] = v.HasValue ? $"{measText}\nRef:{refText}" : $"Meas:---\nRef:{refText}";
                        }
                        await Dispatcher.BeginInvoke(() => { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); });
                    }
                    catch { /* ignore UI issues */ }

                    // Termination conditions
                    if (bestErr <= tolerance) break;
                    if (step <= minStep + 1e-9) break;
                }

                // Apply final best-known settings to device
                SafeSet(VideoCaptureProperties.Brightness, bestB);
                SafeSet(VideoCaptureProperties.Contrast, bestC);
                SafeSet(VideoCaptureProperties.Exposure, bestE);
                await Task.Delay(150);

                // Final sample for return
                var finalSamples = await SampleRoisAsync(rois);
                var finalReadings = (finalSamples != null && finalSamples.Count >= rois.Count) ? finalSamples.ToArray() : lastReadings;

                // Update UI sliders without triggering save during assignment
                await Dispatcher.BeginInvoke(() =>
                {
                    _suspendSliderSave = true;
                    try
                    {
                        BrightnessSlider.Value = bestB;
                        ContrastSlider.Value = bestC;
                        ExposureSlider.Value = bestE;

                        // update pending values to match tuned results
                        _pendingBrightness = bestB;
                        _pendingContrast = bestC;
                        _pendingExposure = bestE;

                        // reflect that these are now unsaved until user presses Save
                        _cameraSettingsDirty = true;
                        if (SaveCameraButton != null) SaveCameraButton.IsEnabled = true;
                        if (SaveStatusSmall != null) SaveStatusSmall.Text = "Modified";
                        if (lblSaveStatus != null) lblSaveStatus.Text = "Modified";
                    }
                    catch { }
                    finally { _suspendSliderSave = false; }
                });

                // Persist final camera properties and APPLY them to the running camera immediately
                try
                {
                    if (_appSettings == null) _appSettings = SettingsManager.Load() ?? new AppSettings();

                    _appSettings.Brightness = bestB;
                    _appSettings.Contrast = bestC;
                    _appSettings.Exposure = bestE;

                    // persist per-channel tolerances as well (keeps UI/behavior consistent)
                    _appSettings.TolerancePercentR = toleranceR;
                    _appSettings.TolerancePercentG = toleranceG;
                    _appSettings.TolerancePercentB = toleranceB;

                    // Apply to current camera (explicitly call ICamera.SetProperty in addition to SafeSet)
                    try
                    {
                        if (_camera != null && _camera.IsOpened)
                        {
                            _camera.SetProperty(VideoCaptureProperties.Brightness, bestB);
                            _camera.SetProperty(VideoCaptureProperties.Contrast, bestC);
                            _camera.SetProperty(VideoCaptureProperties.Exposure, bestE);
                        }
                    }
                    catch { /* tolerate device write error */ }

                    try { SettingsManager.Save(_appSettings); } catch { /* tolerate save error */ }

                    // after AutoTune we saved final tuned values into persisted settings; clear dirty flag
                    _cameraSettingsDirty = false;
                    if (SaveCameraButton != null) SaveCameraButton.IsEnabled = false;
                    try { if (SaveStatusSmall != null) SaveStatusSmall.Text = "Saved"; } catch { }
                }
                catch { /* tolerate persistence errors */ }

                // Read back actual camera-reported final values
                double finalB = SafeGet(VideoCaptureProperties.Brightness);
                double finalC = SafeGet(VideoCaptureProperties.Contrast);
                double finalE = SafeGet(VideoCaptureProperties.Exposure);

                return (true, finalReadings.ElementAtOrDefault(0), finalReadings.ElementAtOrDefault(1), finalReadings.ElementAtOrDefault(2), finalB, finalC, finalE);
            }
            catch
            {
                return (false, null, null, null, 0, 0, 0);
            }
        }
        // Safe wrappers for reading/writing camera properties (avoid CS0103)
        private double SafeGet(VideoCaptureProperties prop)
        {
            try
            {
                if (_camera != null && _camera.IsOpened)
                    return _camera.GetProperty(prop);
            }
            catch
            {
                // tolerate device read errors
            }

            // Fall back to slider values when available (keeps UI and returned values consistent)
            try
            {
                return prop switch
                {
                    VideoCaptureProperties.Brightness => BrightnessSlider?.Value ?? 0.0,
                    VideoCaptureProperties.Contrast => ContrastSlider?.Value ?? 0.0,
                    VideoCaptureProperties.Exposure => ExposureSlider?.Value ?? 0.0,
                    _ => 0.0
                };
            }
            catch
            {
                return 0.0;
            }
        }

        private void SafeSet(VideoCaptureProperties prop, double value)
        {
            try
            {
                if (_camera != null && _camera.IsOpened)
                    _camera.SetProperty(prop, value);
            }
            catch
            {
                // tolerate device set errors
            }

            // Keep UI sliders in sync when calling SafeSet from non-UI threads
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (prop == VideoCaptureProperties.Brightness && BrightnessSlider != null) BrightnessSlider.Value = value;
                        if (prop == VideoCaptureProperties.Contrast && ContrastSlider != null) ContrastSlider.Value = value;
                        if (prop == VideoCaptureProperties.Exposure && ExposureSlider != null) ExposureSlider.Value = value;
                    }
                    catch { }
                });
            }
            catch { }
        }
        private double AutoTunePromptThresholdPercent;
        //private const int AutoTuneTolerance = 3;

        private static double PercentDifference((byte R, byte G, byte B) measured, (byte R, byte G, byte B) reference)
        {
            double mr = measured.R, mg = measured.G, mb = measured.B;
            double rr = reference.R, rg = reference.G, rb = reference.B;
            double dist = Math.Sqrt((mr - rr) * (mr - rr) + (mg - rg) * (mg - rg) + (mb - rb) * (mb - rb));
            double refMag = Math.Sqrt(rr * rr + rg * rg + rb * rb);
            if (refMag < 1e-6) return 100.0;
            return (dist / refMag) * 100.0;
        }

        private static double ChannelPercentDiff(byte measured, byte reference)
        {
            // If reference is zero, treat identical zero as 0% diff; otherwise 100% (can't normalize)
            if (reference == 0) return measured == 0 ? 0.0 : 100.0;
            return Math.Abs(measured - reference) / (double)reference * 100.0;
        }

        private void UpdateRefTextDisplays()
        {
            try
            {
                if (TxtRefRed != null && _refRed.HasValue)
                    TxtRefRed.Text = $"{_refRed.Value.R},{_refRed.Value.G},{_refRed.Value.B}";

                if (TxtRefGreen != null && _refGreen.HasValue)
                    TxtRefGreen.Text = $"{_refGreen.Value.R},{_refGreen.Value.G},{_refGreen.Value.B}";

                if (TxtRefBlue != null && _refBlue.HasValue)
                    TxtRefBlue.Text = $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}";

                if (TxtRefRed != null) TxtRefRed.IsReadOnly = true;
                if (TxtRefGreen != null) TxtRefGreen.IsReadOnly = true;
                if (TxtRefBlue != null) TxtRefBlue.IsReadOnly = true;
            }
            catch { }
        }

        private void EditSaveRef_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button btn) return;
            string name = btn.Name ?? "";

            TextBox? tb = name.Contains("Red") ? TxtRefRed
                          : name.Contains("Green") ? TxtRefGreen
                          : name.Contains("Blue") ? TxtRefBlue
                          : null;

            if (tb == null) return;

            if (btn.Content?.ToString()?.StartsWith("Edit", StringComparison.OrdinalIgnoreCase) == true)
            {
                var ok = AskUser(
                    "Editing reference values is sensitive. Are you sure you want to edit this reference?\nYou will be able to Save or Cancel after editing.",
                    "Edit Reference",
                    MessageBoxButton.OKCancel);

                if (ok != MessageBoxResult.OK) return;

                tb.IsReadOnly = false;
                tb.Focus();
                btn.Content = "Save";
                SetCalibStatus("Editing reference...");
                return;
            }

            if (btn.Content?.ToString()?.StartsWith("Save", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (!TryParseRgb(tb.Text, out var rgb))
                {
                    MessageBox.Show("Invalid RGB format. Use: R,G,B (0-255).", "Parse Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                var confirm = AskUser("Save this reference value to application settings?", "Confirm Save", MessageBoxButton.YesNo);
                if (confirm != MessageBoxResult.Yes)
                {
                    tb.IsReadOnly = true;
                    btn.Content = "Edit";
                    SetCalibStatus("Reference edit cancelled.");
                    return;
                }

                try
                {
                    if (name.Contains("Red")) { _refRed = rgb; if (_appSettings != null) _appSettings.RefRed = rgb; }
                    if (name.Contains("Green")) { _refGreen = rgb; if (_appSettings != null) _appSettings.RefGreen = rgb; }
                    if (name.Contains("Blue")) { _refBlue = rgb; if (_appSettings != null) _appSettings.RefBlue = rgb; }

                    try { SettingsManager.Save(_appSettings); } catch { }

                    try
                    {
                        if (name.Contains("Red") && _refRed.HasValue) ApplyColorToRect(RectRed, _refRed.Value);
                        if (name.Contains("Green") && _refGreen.HasValue) ApplyColorToRect(RectGreen, _refGreen.Value);
                        if (name.Contains("Blue") && _refBlue.HasValue) ApplyColorToRect(RectBlue, _refBlue.Value);
                    }
                    catch { }

                    try { UpdateRefTextDisplays(); } catch { }

                    try
                    {
                        if (_overlayLabelTexts != null)
                        {
                            for (int i = 0; i < _overlayLabelTexts.Length && i < 3; i++)
                            {
                                var parts = _overlayLabelTexts[i].Split(new[] { '\n' }, 2);
                                string meas = parts.Length > 0 ? parts[0] : _overlayLabelTexts[i];
                                string refLine = i == 0 ? $"{_refRed.Value.R},{_refRed.Value.G},{_refRed.Value.B}"
                                                   : i == 1 ? $"{_refGreen.Value.R},{_refGreen.Value.G},{_refGreen.Value.B}"
                                                            : i == 2 ? $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}" : "";
                                _overlayLabelTexts[i] = string.IsNullOrEmpty(refLine) ? meas : $"{meas}\nRef:{refLine}";
                            }

                            Dispatcher.BeginInvoke(() => { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); });
                        }
                    }
                    catch { }

                    tb.IsReadOnly = true;
                    btn.Content = "Edit";
                    SetCalibStatus("Reference saved.");
                }
                catch
                {
                    tb.IsReadOnly = true;
                    btn.Content = "Edit";
                    SetCalibStatus("Failed to save reference.");
                }
            }
        }

        private static bool TryParseRgb(string text, out (byte R, byte G, byte B) rgb)
        {
            rgb = (0, 0, 0);
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 3) return false;
            if (!int.TryParse(parts[0], out var r)) return false;
            if (!int.TryParse(parts[1], out var g)) return false;
            if (!int.TryParse(parts[2], out var b)) return false;
            if (r < 0 || r > 255 || g < 0 || g > 255 || b < 0 || b > 255) return false;
            rgb = ((byte)r, (byte)g, (byte)b);
            return true;
        }

        private async Task ReadBoxesAndAnnotateAsync()
        {
            if (_overlaySourceRects == null || _overlaySourceRects.Count == 0)
            {
                DrawThreeRectsOnImage();
                if (_overlaySourceRects == null) return;
            }

            await Dispatcher.BeginInvoke(() => { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); });

            bool ok;
            try
            {
                ok = await ShowSamplePreparePanelAsync(
                    "Please adjust the three boxes so each is centered inside its target color area.\nClick OK when ready, or Cancel to abort.");
            }
            catch
            {
                ok = false;
            }

            if (ok != true)
            {
                SetCalibStatus("Measurement cancelled by user.");
                return;
            }

            await Task.Delay(150);

            var rois = _overlaySourceRects!;
            var sampled = await SampleRoisAsync(rois);

            for (int i = 0; i < sampled.Count; i++)
            {
                if (sampled[i] == null)
                {
                    SetCalibStatus($"Failed to sample box {i + 1}.");
                    return;
                }
            }

            _overlayLabelTexts = new string[3];
            for (int i = 0; i < 3; i++)
            {
                var s = sampled[i]!.Value;
                string measured = $"{s.R},{s.G},{s.B}";
                string refLine = "";
                try
                {
                    if (i == 0 && _refRed.HasValue) refLine = $"{_refRed.Value.R},{_refRed.Value.G},{_refRed.Value.B}";
                    if (i == 1 && _refGreen.HasValue) refLine = $"{_refGreen.Value.R},{_refGreen.Value.G},{_refGreen.Value.B}";
                    if (i == 2 && _refBlue.HasValue) refLine = $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}";
                }
                catch { }
                _overlayLabelTexts[i] = string.IsNullOrEmpty(refLine) ? $"Meas:{measured}" : $"Meas:{measured}\nRef:{refLine}";
            }

            await Dispatcher.BeginInvoke(() => { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); });

            UpdateRefTextDisplays();

            // Clarified prompt so user knows which choice updates stored refs vs continues with auto-tune.
            MessageBoxResult updateRefs;
            try
            {
                if (CalibDecisionPanel != null)
                {
                    // Inline panel on Calibrate tab (must have ShowCalibDecisionPanelAsync implemented)
                    updateRefs = await ShowCalibDecisionPanelAsync();
                }
                else
                {
                    // Fallback modal dialog
                    var dlg = new CalibDecisionDialog();
                    try { dlg.Owner = System.Windows.Window.GetWindow(this); } catch { }
                    bool? modalResult = dlg.ShowDialog();
                    updateRefs = dlg.SelectedResult;
                }
            }
            catch
            {
                updateRefs = MessageBoxResult.Cancel;
            }

            if (updateRefs == MessageBoxResult.Yes)
            {
                try
                {
                    // Ensure we have three valid measured samples before overwriting refs
                    if (sampled != null && sampled.Count >= 3 && sampled.All(s => s.HasValue))
                    {
                        // Apply measured values as the new references
                        _refRed = sampled[0]!.Value;
                        _refGreen = sampled[1]!.Value;
                        _refBlue = sampled[2]!.Value;

                        // Persist to app settings
                        if (_appSettings == null) _appSettings = SettingsManager.Load() ?? new AppSettings();
                        _appSettings.RefRed = _refRed.Value;
                        _appSettings.RefGreen = _refGreen.Value;
                        _appSettings.RefBlue = _refBlue.Value;
                        try { SettingsManager.Save(_appSettings); } catch { /* tolerate save error */ }

                        // Update UI color swatches
                        try { ApplyColorToRect(RectRed, _refRed.Value); } catch { }
                        try { ApplyColorToRect(RectGreen, _refGreen.Value); } catch { }
                        try { ApplyColorToRect(RectBlue, _refBlue.Value); } catch { }

                        // Refresh overlay label lines to include saved Ref: values
                        try
                        {
                            if (_overlayLabelTexts != null)
                            {
                                for (int i = 0; i < _overlayLabelTexts.Length && i < 3; i++)
                                {
                                    var parts = _overlayLabelTexts[i].Split(new[] { '\n' }, 2);
                                    string meas = parts.Length > 0 ? parts[0] : _overlayLabelTexts[i];
                                    string refLine = i == 0 ? $"{_refRed.Value.R},{_refRed.Value.G},{_refRed.Value.B}"
                                                   : i == 1 ? $"{_refGreen.Value.R},{_refGreen.Value.G},{_refGreen.Value.B}"
                                                            : i == 2 ? $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}" : "";
                                    _overlayLabelTexts[i] = $"{meas}\nRef:{refLine}";
                                }

                                Dispatcher.BeginInvoke(() => { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); });
                            }
                        }
                        catch { /* tolerate overlay update errors */ }

                        // Update text boxes showing references
                        try { UpdateRefTextDisplays(); } catch { }

                        SetCalibStatus("References updated and saved.");
                    }
                    else
                    {
                        SetCalibStatus("Cannot update refs: measurements invalid.");
                    }
                }
                catch
                {
                    SetCalibStatus("Failed to save references.");
                }
            }
            else if (updateRefs == MessageBoxResult.No)
            {
                try
                {
                    if (_overlaySourceRects == null || _overlaySourceRects.Count < 3)
                    {
                        SetCalibStatus("Auto-tune aborted: ROIs unavailable.");
                        return;
                    }

                    if (!_refRed.HasValue || !_refGreen.HasValue || !_refBlue.HasValue)
                    {
                        SetCalibStatus("Auto-tune aborted: reference colors not set.");
                        return;
                    }

                    // Check whether tuning is required (any measured box outside per-channel tolerance)
                    bool needTune = false;
                    for (int i = 0; i < 3; i++)
                    {
                        var measured = sampled[i]!.Value;
                        var reference = i == 0 ? _refRed.Value : i == 1 ? _refGreen.Value : _refBlue.Value;

                        double pr = ChannelPercentDiff(measured.R, reference.R);
                        double pg = ChannelPercentDiff(measured.G, reference.G);
                        double pb = ChannelPercentDiff(measured.B, reference.B);

                        if (pr > toleranceR || pg > toleranceG || pb > toleranceB)
                        {
                            needTune = true;
                            break;
                        }
                    }

                    if (!needTune)
                    {
                        SetCalibStatus("Auto-tune not required: measurements already within tolerance.");
                        return;
                    }

                    SetCalibStatus("Tuning camera controls...");

                    // pass numeric tolerance as max of per-channel tolerances (AutoTune expects single int)
                    int tolInt = Math.Max((int)Math.Round(toleranceR), Math.Max((int)Math.Round(toleranceG), (int)Math.Round(toleranceB)));
                    tolInt = Math.Max(1, tolInt);

                    var tuneResult = await AutoTuneToRefsAsync(rois, tolerance: tolInt);
                    if (!tuneResult.Success)
                    {
                        SetCalibStatus("Auto-tune failed.");
                        return;
                    }

                    // Use returned per-box RGB readings (Box1, Box2, Box3) for post-check
                    var postReadings = new[] { tuneResult.Box1, tuneResult.Box2, tuneResult.Box3 };

                    bool success = true;
                    var diffs = new double[3];

                    // Also compute per-channel maxima across boxes for better reporting
                    double[] perBoxR = new double[3];
                    double[] perBoxG = new double[3];
                    double[] perBoxB = new double[3];

                    for (int i = 0; i < 3; i++)
                    {
                        if (postReadings[i] == null)
                        {
                            success = false;
                            diffs[i] = double.PositiveInfinity;
                            perBoxR[i] = perBoxG[i] = perBoxB[i] = double.PositiveInfinity;
                            continue;
                        }

                        var measured = postReadings[i]!.Value;
                        var reference = i == 0 ? _refRed.Value : i == 1 ? _refGreen.Value : _refBlue.Value;

                        double pr = ChannelPercentDiff(measured.R, reference.R);
                        double pg = ChannelPercentDiff(measured.G, reference.G);
                        double pb = ChannelPercentDiff(measured.B, reference.B);

                        // store the worst channel percent for reporting
                        diffs[i] = Math.Max(pr, Math.Max(pg, pb));

                        perBoxR[i] = pr;
                        perBoxG[i] = pg;
                        perBoxB[i] = pb;

                        if (pr > toleranceR || pg > toleranceG || pb > toleranceB) success = false;
                    }

                    if (success)
                    {
                        SetCalibStatus("Auto-tune successful: measurements within tolerance.");
                        // Update overlay labels with new measured values
                        if (_overlayLabelTexts != null)
                        {
                            for (int i = 0; i < _overlayLabelTexts.Length && i < 3; i++)
                            {
                                var meas = postReadings[i] != null ? $"{postReadings[i]!.Value.R},{postReadings[i]!.Value.G},{postReadings[i]!.Value.B}" : "Meas:---";
                                var refLine = i == 0 ? $"{_refRed.Value.R},{_refRed.Value.G},{_refRed.Value.B}"
                                            : i == 1 ? $"{_refGreen.Value.R},{_refGreen.Value.G},{_refGreen.Value.B}"
                                                     : $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}";
                                _overlayLabelTexts[i] = $"Meas:{meas}\nRef:{refLine}";
                            }

                            Dispatcher.BeginInvoke(() => { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); });
                        }
                    }
                    else
                    {
                        // Find maximum error percentage (overall, per-box, per-channel)
                        double overallMax = diffs.Where(d => double.IsFinite(d)).DefaultIfEmpty(0.0).Max();
                        double maxR = perBoxR.Where(v => double.IsFinite(v)).DefaultIfEmpty(0.0).Max();
                        double maxG = perBoxG.Where(v => double.IsFinite(v)).DefaultIfEmpty(0.0).Max();
                        double maxB = perBoxB.Where(v => double.IsFinite(v)).DefaultIfEmpty(0.0).Max();

                        SetCalibStatus("Auto-tune completed but targets not reached.");

                        double maxTol = Math.Max(toleranceR, Math.Max(toleranceG, toleranceB));

                        string details =
                            $"Auto-tune finished; targets not reached.\n\n" +
                            $"Overall worst error: {overallMax:F2}% (tolerance used: R:{toleranceR:F2}%, G:{toleranceG:F2}%, B:{toleranceB:F2}%)\n\n" +
                            $"Per-box worst errors:\n" +
                            $" Box 1: {(double.IsFinite(diffs[0]) ? diffs[0].ToString("F2") + "%" : "N/A")}\n" +
                            $" Box 2: {(double.IsFinite(diffs[1]) ? diffs[1].ToString("F2") + "%" : "N/A")}\n" +
                            $" Box 3: {(double.IsFinite(diffs[2]) ? diffs[2].ToString("F2") + "%" : "N/A")}\n\n" +
                            $"Per-channel worst errors: R:{maxR:F2}%, G:{maxG:F2}%, B:{maxB:F2}%\n\n" +
                            $"Tolerances: R:{toleranceR:F2}%, G:{toleranceG:F2}%, B:{toleranceB:F2}%";

                        MessageBox.Show(details, "Auto-tune Result", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                }
                catch (Exception ex)
                {
                    SetCalibStatus($"Auto-tune error: {ex.Message}");
                }
            }
            else
            {
                SetCalibStatus("Calibration cancelled.");
            }
        }

        private void UpdateCollapseButton()
        {
            if (CollapseButton == null || ControlExpander == null) return;
            CollapseButton.Content = ControlExpander.IsExpanded ? "▾" : "▴";
            CollapseButton.ToolTip = ControlExpander.IsExpanded ? "Collapse controls" : "Expand controls";
        }

        private void ControlExpander_Expanded(object sender, RoutedEventArgs e) { try { UpdateCollapseButton(); } catch { } }
        private void ControlExpander_Collapsed(object sender, RoutedEventArgs e) { try { UpdateCollapseButton(); } catch { } }

        private void CollapseButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ControlExpander == null) return;
                ControlExpander.IsExpanded = !ControlExpander.IsExpanded;
                UpdateCollapseButton();
            }
            catch { }
        }

        private async void SaveCameraSettingsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_appSettings == null) _appSettings = SettingsManager.Load() ?? new AppSettings();

            // use pending (unsaved) values to persist
            double brightness = _pendingBrightness;
            double contrast = _pendingContrast;
            double exposure = _pendingExposure;

            try
            {
                if (_camera != null && _camera.IsOpened)
                {
                    _camera.SetProperty(VideoCaptureProperties.Brightness, brightness);
                    _camera.SetProperty(VideoCaptureProperties.Contrast, contrast);
                    _camera.SetProperty(VideoCaptureProperties.Exposure, exposure);
                }
            }
            catch { }

            // Persist to settings
            _appSettings.Brightness = brightness;
            _appSettings.Contrast = contrast;
            _appSettings.Exposure = exposure;
            try { SettingsManager.Save(_appSettings); } catch { }

            // Clear dirty state and update UI
            _cameraSettingsDirty = false;
            try
            {
                if (SaveCameraButton != null) SaveCameraButton.IsEnabled = false;
                if (SaveStatusSmall != null) SaveStatusSmall.Text = "Saved";
                if (lblSaveStatus != null) lblSaveStatus.Text = "Saved";

                await Task.Delay(1500);

                if (SaveStatusSmall != null) SaveStatusSmall.Text = "";
                // keep the main lblSaveStatus visible a bit longer, but clear if desired:
                if (lblSaveStatus != null) lblSaveStatus.Text = "--";
            }
            catch { }
        }

        private void ApplyColorToRect(Border rect, (byte R, byte G, byte B) col)
        {
            if (rect == null) return;
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(col.R, col.G, col.B));
            brush.Freeze();
            rect.Background = brush;
        }

        // Update both CalibStatus (General tab) and CalibTabStatus (Calibrate tab) so status is visible from either tab.
        private void SetCalibStatus(string text)
        {
            try { if (CalibStatus != null) CalibStatus.Content = text; } catch { }
            try { if (CalibTabStatus != null) CalibTabStatus.Content = text; } catch { }
        }

        private async void AutoCalibrateButton_Click(object sender, RoutedEventArgs e)
        {
            if (_camera == null || !_camera.IsOpened)
            {
                MessageBox.Show("Open camera first and make sure preview is running.", "Auto Calibrate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_lastFrame == null)
            {
                MessageBox.Show("No live frame available. Start camera and try again.", "Auto Calibrate", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            DrawThreeRectsOnImage();
            await ReadBoxesAndAnnotateAsync();

            if (_overlaySourceRects != null && _overlaySourceRects.Count >= 3)
            {
                var sampled = await Task.Run(() =>
                {
                    var results = new List<(byte R, byte G, byte B)?>();
                    foreach (var r in _overlaySourceRects)
                    {
                        var rr = new OpenCvSharp.Rect((int)Math.Round(r.X), (int)Math.Round(r.Y), (int)Math.Round(r.Width), (int)Math.Round(r.Height));
                        results.Add(SampleMatPatch(rr));
                    }
                    return results;
                });

                bool ok = sampled.Count >= 3 && sampled.All(s => s != null);
                if (ok)
                {
                    _refRed = sampled[0]!.Value;
                    _refGreen = sampled[1]!.Value;
                    _refBlue = sampled[2]!.Value;

                    try { ApplyColorToRect(RectRed, _refRed.Value); } catch { }
                    try { ApplyColorToRect(RectGreen, _refGreen.Value); } catch { }
                    try { ApplyColorToRect(RectBlue, _refBlue.Value); } catch { }

                    try
                    {
                        if (_appSettings == null) _appSettings = SettingsManager.Load() ?? new AppSettings();
                        _appSettings.RefRed = _refRed.Value;
                        _appSettings.RefGreen = _refGreen.Value;
                        _appSettings.RefBlue = _refBlue.Value;
                        SettingsManager.Save(_appSettings);
                    }
                    catch { }

                    try { if (_overlaySourceRects != null) ShowOverlayCanvasRects(_overlaySourceRects); } catch { }
                }
                else
                {
                    SetCalibStatus("Auto-calibrate: sampling failed.");
                    return;
                }
            }

            FinishCalibrationAndPromptSave();
        }
        private void FinishCalibrationAndPromptSave()
        {
            try
            {
                // Ensure UI update runs on UI thread
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        // switch to Calibrate tab (index 1) so the inline prompt is visible on the Calibrate page only
                        try
                        {
                            if (MainTabControl != null && MainTabControl.Items.Count > 1)
                                MainTabControl.SelectedIndex = 1;
                        }
                        catch { /* tolerate tab switch errors */ }

                        // hide overlay visuals immediately
                        ClearOverlayVisuals();

                        // show inline calibration-complete prompt (user can Save or Don't Save)
                        ShowCalibrationCompletePrompt();
                    }
                    catch
                    {
                        // fallback: show a modal prompt if inline prompt fails
                        try
                        {
                            var result = MessageBox.Show(
                                "Station calibration complete.\n\nSave current Brightness / Contrast / Exposure to application settings?",
                                "Station Calibration Complete",
                                MessageBoxButton.YesNo,
                                MessageBoxImage.Question);

                            if (result == MessageBoxResult.Yes)
                            {
                                // reuse existing save handler to persist current sliders
                                _ = Task.Run(() =>
                                {
                                    Dispatcher.BeginInvoke(async () => await Task.Run(() => SaveCameraSettingsButton_Click(null!, null!)));
                                });
                            }

                            SetCalibStatus(result == MessageBoxResult.Yes ? "Calibration complete — settings saved." : "Calibration complete — settings not saved.");
                        }
                        catch { }
                    }
                });
            }
            catch { }
        }
        private void ImgPrepToggleButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (ImagePrepGroupBox == null || ImgPrepToggleButton == null) return;

                if (ImagePrepGroupBox.Visibility == Visibility.Visible)
                {
                    ImagePrepGroupBox.Visibility = Visibility.Collapsed;
                    ImgPrepToggleButton.Content = "▴";
                }
                else
                {
                    ImagePrepGroupBox.Visibility = Visibility.Visible;
                    ImgPrepToggleButton.Content = "▾";
                }
            }
            catch
            {
                // tolerate UI toggle errors
            }
        }
        // Clear any existing overlay visuals (rectangles, labels) from the UI and internal state.
        private void ClearOverlayVisuals()
        {
            try
            {
                // Clear logical overlay data
                _overlaySourceRects = null;
                _overlayLabelTexts = null;

                // Collapse any existing overlay shapes/labels on UI
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (_overlayRects != null)
                        {
                            foreach (var r in _overlayRects) if (r != null) r.Visibility = Visibility.Collapsed;
                        }
                        if (_overlayLabels != null)
                        {
                            foreach (var l in _overlayLabels) if (l != null) l.Visibility = Visibility.Collapsed;
                        }
                        if (_calibOverlayRects != null)
                        {
                            foreach (var r in _calibOverlayRects) if (r != null) r.Visibility = Visibility.Collapsed;
                        }
                        if (_calibOverlayLabels != null)
                        {
                            foreach (var l in _calibOverlayLabels) if (l != null) l.Visibility = Visibility.Collapsed;
                        }
                    }
                    catch { /* tolerate UI errors */ }
                });
            }
            catch { /* tolerate errors */ }
        }

        // Show the inline "calibration complete" prompt (does not save)
        private void ShowCalibrationCompletePrompt()
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (CalibCompletePanel != null)
                            CalibCompletePanel.Visibility = Visibility.Visible;
                        SetCalibStatus("Station calibration complete.");
                    }
                    catch { }
                });
            }
            catch { }
        }

        private void HideCalibrationCompletePrompt()
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try
                    {
                        if (CalibCompletePanel != null)
                            CalibCompletePanel.Visibility = Visibility.Collapsed;
                    }
                    catch { }
                });
            }
            catch { }
        }

        // User clicked "Save Settings" on inline prompt
        private async void CalibSaveButton_Click(object? sender, RoutedEventArgs e)
        {
            try
            {
                // Persist current slider values to settings
                if (_appSettings == null) _appSettings = SettingsManager.Load() ?? new AppSettings();

                _appSettings.Brightness = BrightnessSlider?.Value ?? _appSettings.Brightness;
                _appSettings.Contrast = ContrastSlider?.Value ?? _appSettings.Contrast;
                _appSettings.Exposure = ExposureSlider?.Value ?? _appSettings.Exposure;

                try { SettingsManager.Save(_appSettings); } catch { /* tolerate save error */ }

                // Apply to running camera
                try
                {
                    if (_camera != null && _camera.IsOpened)
                    {
                        _camera.SetProperty(VideoCaptureProperties.Brightness, _appSettings.Brightness);
                        _camera.SetProperty(VideoCaptureProperties.Contrast, _appSettings.Contrast);
                        _camera.SetProperty(VideoCaptureProperties.Exposure, _appSettings.Exposure);
                    }
                }
                catch { /* tolerate device write error */ }

                SetCalibStatus("Calibration complete — settings saved.");
            }
            catch
            {
                SetCalibStatus("Calibration complete — failed to save.");
            }
            finally
            {
                ClearOverlayVisuals();
                HideCalibrationCompletePrompt();
                // briefly show saved indicator in the ImagePrep area
                try
                {
                    if (lblSaveStatus != null)
                    {
                        lblSaveStatus.Text = "Saved";
                        await Task.Delay(1200);
                        lblSaveStatus.Text = "";
                    }
                }
                catch { }
            }
        }

        // User clicked "Don't Save" on inline prompt
        private void CalibDontSaveButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ClearOverlayVisuals();
                HideCalibrationCompletePrompt();
                SetCalibStatus("Calibration complete — settings not saved.");
            }
            catch { }
        }

        // Inline decision panel support ------------------------------------------------

        // Shows the inline decision panel and awaits the user's selection.
        // Returns MessageBoxResult.Yes to save refs, No to auto-tune, Cancel to abort.
        public Task<MessageBoxResult> ShowCalibDecisionPanelAsync()
        {
            var tcs = new TaskCompletionSource<MessageBoxResult>();
            _calibDecisionTcs = tcs;

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (CalibDecisionPanel != null)
                    {
                        CalibDecisionPanel.Visibility = Visibility.Visible;
                        // optional: focus the primary action
                        try { CalibDecisionSaveButton?.Focus(); } catch { }
                    }
                    else
                    {
                        // fallback to showing dialog immediately if panel missing
                        tcs.TrySetResult(MessageBoxResult.Cancel);
                    }
                }
                catch
                {
                    tcs.TrySetResult(MessageBoxResult.Cancel);
                }
            });

            return tcs.Task;
        }

        private void CalibDecision_SaveRefs_Click(object sender, RoutedEventArgs e)
        {
            CompleteCalibDecision(MessageBoxResult.Yes);
        }

        private void CalibDecision_AutoTune_Click(object sender, RoutedEventArgs e)
        {
            CompleteCalibDecision(MessageBoxResult.No);
        }

        private void CalibDecision_Cancel_Click(object sender, RoutedEventArgs e)
        {
            CompleteCalibDecision(MessageBoxResult.Cancel);
        }

        private void CompleteCalibDecision(MessageBoxResult result)
        {
            try
            {
                Dispatcher.BeginInvoke(() =>
                {
                    try { if (CalibDecisionPanel != null) CalibDecisionPanel.Visibility = Visibility.Collapsed; } catch { }
                });
            }
            catch { }

            try
            {
                _calibDecisionTcs?.TrySetResult(result);
            }
            catch { }
            finally
            {
                _calibDecisionTcs = null;
            }
        }

        // Add this public helper inside the CameraPage class (near other helpers, e.g. after SetCalibStatus)
        public void RefreshTolerances()
        {
            try
            {
                // Load fresh settings and update runtime tolerances used by CameraPage
                var s = SettingsManager.Load();
                if (s != null)
                {
                    toleranceR = s.TolerancePercentR;
                    toleranceG = s.TolerancePercentG;
                    toleranceB = s.TolerancePercentB;
                }

                // Give short UI feedback so user knows new tolerances applied
                try
                {
                    SetCalibStatus($"Tolerances loaded: R:{toleranceR:F2}% G:{toleranceG:F2}% B:{toleranceB:F2}%");
                }
                catch { }
            }
            catch
            {
                // tolerate errors silently
            }
        }

        // Add these members near the other private fields at top of class:
        //private TaskCompletionSource<bool>? _samplePrepareTcs;

        // Add this helper method inside CameraPage class (anywhere with other helpers)
        public Task<bool> ShowSamplePreparePanelAsync(string message)
        {
            var tcs = new TaskCompletionSource<bool>();
            _samplePrepareTcs = tcs;

            Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    if (SamplePrepareText != null) SamplePrepareText.Text = message ?? SamplePrepareText.Text;
                    if (SamplePreparePanel != null)
                    {
                        SamplePreparePanel.Visibility = Visibility.Visible;
                        try { SamplePrepareOk?.Focus(); } catch { }
                    }
                    else
                    {
                        tcs.TrySetResult(false);
                    }
                }
                catch
                {
                    tcs.TrySetResult(false);
                }
            });

            return tcs.Task;
        }

        // Add these two button handlers to the CameraPage class:
        private void SamplePrepareOk_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Dispatcher.BeginInvoke(() => { if (SamplePreparePanel != null) SamplePreparePanel.Visibility = Visibility.Collapsed; });
            }
            catch { }

            try { _samplePrepareTcs?.TrySetResult(true); } catch { } finally { _samplePrepareTcs = null; }
        }

        private void SamplePrepareCancel_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Dispatcher.BeginInvoke(() => { if (SamplePreparePanel != null) SamplePreparePanel.Visibility = Visibility.Collapsed; });
            }
            catch { }

            try { _samplePrepareTcs?.TrySetResult(false); } catch { } finally { _samplePrepareTcs = null; }
        }
    }
}