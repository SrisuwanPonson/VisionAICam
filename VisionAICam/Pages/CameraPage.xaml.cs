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

        public CameraPage()
        {
            InitializeComponent();

            _appSettings = SettingsManager.Load();

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
            CalibImage.LayoutUpdated += CalibImage_LayoutUpdated;

            if (_appSettings != null)
            {
                _suspendSliderSave = true;
                try
                {
                    BrightnessSlider.Value = _appSettings!.Brightness;
                    ContrastSlider.Value = _appSettings.Contrast;
                    ExposureSlider.Value = _appSettings.Exposure;
                }
                catch { }
                finally { _suspendSliderSave = false; }
            }
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
                if (_camera != null && _camera.IsOpened)
                    _camera.SetProperty(VideoCaptureProperties.Brightness, e.NewValue);

                if (_appSettings != null)
                {
                    _appSettings.Brightness = e.NewValue;
                    SettingsManager.Save(_appSettings);
                }
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

                if (_appSettings != null)
                {
                    _appSettings.Contrast = e.NewValue;
                    SettingsManager.Save(_appSettings);
                }
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

                if (_appSettings != null)
                {
                    _appSettings.Exposure = e.NewValue;
                    SettingsManager.Save(_appSettings);
                }
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

        private async Task<bool> AutoTuneToRefsAsync(List<System.Windows.Rect> rois, int tolerance = 10)
        {
            if (_camera == null || !_camera.IsOpened) return false;
            if (!_refRed.HasValue || !_refGreen.HasValue || !_refBlue.HasValue) return false;

            try
            {
                double curB = 0, curC = 0, curE = 0;
                try { curB = _camera.GetProperty(VideoCaptureProperties.Brightness); } catch { }
                try { curC = _camera.GetProperty(VideoCaptureProperties.Contrast); } catch { }
                try { curE = _camera.GetProperty(VideoCaptureProperties.Exposure); } catch { }

                var baselineSamples = await SampleRoisAsync(rois);
                double bestErr = ComputeErrorAgainstRefs(baselineSamples);
                double bestB = curB, bestC = curC, bestE = curE;

                double[][] offsetSets = { new double[] { -30, -15, 0, 15, 30 }, new double[] { -8, -4, 0, 4, 8 } };

                foreach (var offsets in offsetSets)
                {
                    foreach (var off in offsets)
                    {
                        double cand = bestB + off;
                        try { _camera.SetProperty(VideoCaptureProperties.Brightness, cand); } catch { }
                        try { _camera.SetProperty(VideoCaptureProperties.Contrast, bestC); } catch { }
                        try { _camera.SetProperty(VideoCaptureProperties.Exposure, bestE); } catch { }

                        await Task.Delay(200);
                        var samples = await SampleRoisAsync(rois);
                        double err = ComputeErrorAgainstRefs(samples);
                        if (err < bestErr) { bestErr = err; bestB = cand; }
                    }

                    foreach (var off in offsets)
                    {
                        double cand = bestC + off;
                        try { _camera.SetProperty(VideoCaptureProperties.Brightness, bestB); } catch { }
                        try { _camera.SetProperty(VideoCaptureProperties.Contrast, cand); } catch { }
                        try { _camera.SetProperty(VideoCaptureProperties.Exposure, bestE); } catch { }

                        await Task.Delay(200);
                        var samples = await SampleRoisAsync(rois);
                        double err = ComputeErrorAgainstRefs(samples);
                        if (err < bestErr) { bestErr = err; bestC = cand; }
                    }

                    foreach (var off in offsets)
                    {
                        double cand = bestE + off;
                        try { _camera.SetProperty(VideoCaptureProperties.Brightness, bestB); } catch { }
                        try { _camera.SetProperty(VideoCaptureProperties.Contrast, bestC); } catch { }
                        try { _camera.SetProperty(VideoCaptureProperties.Exposure, cand); } catch { }

                        await Task.Delay(200);
                        var samples = await SampleRoisAsync(rois);
                        double err = ComputeErrorAgainstRefs(samples);
                        if (err < bestErr) { bestErr = err; bestE = cand; }
                    }
                }

                try { _camera.SetProperty(VideoCaptureProperties.Brightness, bestB); } catch { }
                try { _camera.SetProperty(VideoCaptureProperties.Contrast, bestC); } catch { }
                try { _camera.SetProperty(VideoCaptureProperties.Exposure, bestE); } catch { }

                await Dispatcher.BeginInvoke(() =>
                {
                    _suspendSliderSave = true;
                    try
                    {
                        BrightnessSlider.Value = bestB;
                        ContrastSlider.Value = bestC;
                        ExposureSlider.Value = bestE;
                    }
                    catch { }
                    finally { _suspendSliderSave = false; }
                });

                if (_appSettings == null) _appSettings = SettingsManager.Load() ?? new AppSettings();
                _appSettings.Brightness = bestB;
                _appSettings.Contrast = bestC;
                _appSettings.Exposure = bestE;
                try { SettingsManager.Save(_appSettings); } catch { }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private const double AutoTunePromptThresholdPercent = 5.0;
        private const int AutoTuneTolerance = 3;

        private static double PercentDifference((byte R, byte G, byte B) measured, (byte R, byte G, byte B) reference)
        {
            double mr = measured.R, mg = measured.G, mb = measured.B;
            double rr = reference.R, rg = reference.G, rb = reference.B;
            double dist = Math.Sqrt((mr - rr) * (mr - rr) + (mg - rg) * (mg - rg) + (mb - rb) * (mb - rb));
            double refMag = Math.Sqrt(rr * rr + rg * rg + rb * rb);
            if (refMag < 1e-6) return 100.0;
            return (dist / refMag) * 100.0;
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
                                string refLine = i == 0 && _refRed.HasValue ? $"{_refRed.Value.R},{_refRed.Value.G},{_refRed.Value.B}"
                                       : i == 1 && _refGreen.HasValue ? $"{_refGreen.Value.R},{_refGreen.Value.G},{_refGreen.Value.B}"
                                       : i == 2 && _refBlue.HasValue ? $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}" : "";
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

            if (AskUser("Please adjust the three boxes so each is centered inside its target color area.\nClick OK when ready, or Cancel to abort.", "Adjust Boxes", MessageBoxButton.OKCancel) != MessageBoxResult.OK)
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
            var updateRefs = AskUser(
                "Choose Yes to update and save the stored reference values with these measurements and finish calibration.\n\n" +
                "Choose No to keep the existing stored references and attempt an automatic tune of camera controls (brightness/contrast/exposure) to match the stored references.\n\n" +
                "Press Cancel to abort.",
                "Update References or Auto-tune?",
                MessageBoxButton.YesNoCancel);

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
                                                            : $"{_refBlue.Value.R},{_refBlue.Value.G},{_refBlue.Value.B}";
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
                // Auto-tune brightness/contrast/exposure to try to reach stored references within 2% tolerance.
                const double tolerancePercent = 2.0;
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

                    // Check whether tuning is required (any measured box outside tolerance)
                    bool needTune = false;
                    for (int i = 0; i < 3; i++)
                    {
                        var measured = sampled[i]!.Value;
                        var reference = i == 0 ? _refRed.Value : i == 1 ? _refGreen.Value : _refBlue.Value;
                        double pct = PercentDifference(measured, reference);
                        if (pct > tolerancePercent) { needTune = true; break; }
                    }

                    if (!needTune)
                    {
                        SetCalibStatus("Auto-tune not required: measurements already within tolerance.");
                        return;
                    }

                    SetCalibStatus("Auto-tuning camera controls...");
                    bool tuned = await AutoTuneToRefsAsync(rois, tolerance: 2);
                    if (!tuned)
                    {
                        SetCalibStatus("Auto-tune failed.");
                        return;
                    }

                    // Give camera a short moment to settle and re-sample
                    await Task.Delay(250);
                    var postSamples = await SampleRoisAsync(rois);

                    bool success = true;
                    var diffs = new double[3];
                    for (int i = 0; i < 3; i++)
                    {
                        if (postSamples[i] == null)
                        {
                            success = false;
                            diffs[i] = double.PositiveInfinity;
                            continue;
                        }

                        var measured = postSamples[i]!.Value;
                        var reference = i == 0 ? _refRed.Value : i == 1 ? _refGreen.Value : _refBlue.Value;
                        diffs[i] = PercentDifference(measured, reference);
                        if (diffs[i] > tolerancePercent) success = false;
                    }

                    if (success)
                    {
                        SetCalibStatus("Auto-tune successful: measurements within tolerance.");
                        // Update overlay labels with new measured values
                        if (_overlayLabelTexts != null)
                        {
                            for (int i = 0; i < _overlayLabelTexts.Length && i < 3; i++)
                            {
                                var meas = postSamples[i] != null ? $"{postSamples[i]!.Value.R},{postSamples[i]!.Value.G},{postSamples[i]!.Value.B}" : "Meas:---";
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
                        SetCalibStatus("Auto-tune completed but targets not reached.");
                        string details = $"Auto-tune finished; values outside {tolerancePercent}%:\n";
                        for (int i = 0; i < 3; i++) details += $"Box {i + 1}: {(double.IsFinite(diffs[i]) ? diffs[i].ToString("F2") + "%" : "N/A")}\n";
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

            double brightness = BrightnessSlider?.Value ?? _appSettings.Brightness;
            double contrast = ContrastSlider?.Value ?? _appSettings.Contrast;
            double exposure = ExposureSlider?.Value ?? _appSettings.Exposure;

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

            _appSettings.Brightness = brightness;
            _appSettings.Contrast = contrast;
            _appSettings.Exposure = exposure;
            try { SettingsManager.Save(_appSettings); } catch { }

            try
            {
                if (lblSaveStatus != null)
                {
                    lblSaveStatus.Text = "Saved";
                    await Task.Delay(1500);
                    lblSaveStatus.Text = "--";
                }
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

            SetCalibStatus("Auto-calibrate: sampled and annotated.");
        }

        // Toggle the moved Image Preparation GroupBox
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
            catch { /* ignore UI toggle errors */ }
        }
    }
}