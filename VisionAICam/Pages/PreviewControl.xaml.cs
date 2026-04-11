using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VisionAICam.Pages
{
    public partial class PreviewControl : UserControl
    {
        private System.Windows.Shapes.Rectangle[]? _overlayRects;
        private TextBlock[]? _overlayLabels;

        public PreviewControl()
        {
            InitializeComponent();
            SizeChanged += (s, e) => { /* overlays reposition on ShowOverlays call */ };
            LayoutUpdated += (s, e) => { /* no-op; caller will re-show overlays when needed */ };
        }

        public void SetImage(BitmapSource bmp)
        {
            try
            {
                PreviewImage.Source = bmp;
            }
            catch { /* tolerate UI errors */ }
        }

        // sourceRects are in source/image pixel coordinates (same as before)
        public void ShowOverlays(List<System.Windows.Rect> sourceRects, string[]? overlayLabelTexts, BitmapSource? lastFrame)
        {
            if (lastFrame == null) return;
            if (PreviewImage == null || OverlayCanvas == null) return;

            try
            {
                if (PreviewImage.ActualWidth <= 0 || PreviewImage.ActualHeight <= 0)
                {
                    PreviewImage.UpdateLayout();
                    OverlayCanvas.UpdateLayout();
                }

                double srcW = lastFrame.PixelWidth;
                double srcH = lastFrame.PixelHeight;
                double ctrlW = PreviewImage.ActualWidth;
                double ctrlH = PreviewImage.ActualHeight;
                if (srcW <= 0 || srcH <= 0 || ctrlW <= 0 || ctrlH <= 0) return;

                double scale = Math.Min(ctrlW / srcW, ctrlH / srcH);
                double dispW = srcW * scale;
                double dispH = srcH * scale;
                double offsetX = (ctrlW - dispW) / 2.0;
                double offsetY = (ctrlH - dispH) / 2.0;

                OverlayCanvas.Width = Math.Max(OverlayCanvas.Width, PreviewImage.ActualWidth);
                OverlayCanvas.Height = Math.Max(OverlayCanvas.Height, PreviewImage.ActualHeight);

                if (_overlayRects == null)
                {
                    _overlayRects = new System.Windows.Shapes.Rectangle[3];
                    _overlayLabels = new TextBlock[3];
                    for (int i = 0; i < 3; i++)
                    {
                        var rect = new System.Windows.Shapes.Rectangle
                        {
                            Fill = Brushes.Transparent,
                            StrokeThickness = 3,
                            Visibility = Visibility.Collapsed
                        };
                        OverlayCanvas.Children.Add(rect);
                        _overlayRects[i] = rect;

                        var tb = new TextBlock
                        {
                            FontSize = 12,
                            Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)),
                            Foreground = Brushes.White,
                            Padding = new Thickness(4, 2, 4, 2),
                            TextWrapping = TextWrapping.Wrap,
                            Visibility = Visibility.Collapsed
                        };
                        OverlayCanvas.Children.Add(tb);
                        _overlayLabels[i] = tb;
                    }
                }

                Brush[] strokeBrushes = new Brush[3];
                try
                {
                    strokeBrushes[0] = new SolidColorBrush(Color.FromRgb(236, 10, 10));
                    strokeBrushes[1] = new SolidColorBrush(Color.FromRgb(10, 236, 10));
                    strokeBrushes[2] = new SolidColorBrush(Color.FromRgb(10, 10, 236));
                    foreach (var b in strokeBrushes) if (b is SolidColorBrush sb) sb.Freeze();
                }
                catch
                {
                    strokeBrushes[0] = Brushes.Red;
                    strokeBrushes[1] = Brushes.Lime;
                    strokeBrushes[2] = Brushes.DodgerBlue;
                }

                GeneralTransform imageToCanvas;
                try { imageToCanvas = PreviewImage.TransformToVisual(OverlayCanvas); }
                catch { imageToCanvas = null!; }

                for (int i = 0; i < 3; i++)
                {
                    if (i >= sourceRects.Count)
                    {
                        if (_overlayRects[i] != null) _overlayRects[i].Visibility = Visibility.Collapsed;
                        if (_overlayLabels[i] != null) _overlayLabels[i].Visibility = Visibility.Collapsed;
                        continue;
                    }

                    var s = sourceRects[i];

                    var tlImg = new System.Windows.Point(offsetX + s.X * scale, offsetY + s.Y * scale);
                    var brImg = new System.Windows.Point(offsetX + (s.X + s.Width) * scale, offsetY + (s.Y + s.Height) * scale);

                    Point tlCanvas, brCanvas;
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

                    var rect = _overlayRects[i];
                    rect.Width = w;
                    rect.Height = h;
                    rect.Stroke = strokeBrushes[Math.Min(i, strokeBrushes.Length - 1)];
                    Canvas.SetLeft(rect, left);
                    Canvas.SetTop(rect, top);
                    rect.Visibility = Visibility.Visible;

                    var label = _overlayLabels[i];
                    string? text = (overlayLabelTexts != null && i < overlayLabelTexts.Length) ? overlayLabelTexts[i] : null;
                    if (!string.IsNullOrEmpty(text))
                    {
                        label.Text = text;
                        label.Measure(new Size(OverlayCanvas.ActualWidth, OverlayCanvas.ActualHeight));
                        double lblW = label.DesiredSize.Width;
                        double lblH = label.DesiredSize.Height;

                        double lblLeft = left;
                        double lblTop = top + h + 6;

                        if (lblLeft + lblW > OverlayCanvas.ActualWidth) lblLeft = Math.Max(2.0, OverlayCanvas.ActualWidth - lblW - 2.0);
                        if (lblTop + lblH > OverlayCanvas.ActualHeight) lblTop = Math.Max(2.0, top - lblH - 6.0);

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
    }
}