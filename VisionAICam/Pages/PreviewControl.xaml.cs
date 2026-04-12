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

                // Resolve themed brushes (fallback to sensible defaults)
                var accent = Application.Current.TryFindResource("AccentBrush") as Brush ?? new SolidColorBrush(Color.FromRgb(25, 118, 210));
                var textBrush = Application.Current.TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;

                if (_overlayRects == null)
                {
                    _overlayRects = new System.Windows.Shapes.Rectangle[3];
                    _overlayLabels = new TextBlock[3];
                    for (int i = 0; i < 3; i++)
                    {
                        var rect = new System.Windows.Shapes.Rectangle
                        {
                            Fill = Brushes.Transparent,
                            Stroke = accent,
                            StrokeThickness = 3,
                            Visibility = Visibility.Collapsed
                        };
                        OverlayCanvas.Children.Add(rect);
                        _overlayRects[i] = rect;

                        var tb = new TextBlock
                        {
                            FontSize = 12,
                            Foreground = textBrush,
                            Visibility = Visibility.Collapsed
                        };
                        OverlayCanvas.Children.Add(tb);
                        _overlayLabels[i] = tb;
                    }
                }

                // Position and show overlays
                for (int i = 0; i < _overlayRects.Length; i++)
                {
                    if (i < sourceRects.Count)
                    {
                        var sr = sourceRects[i];
                        double x = offsetX + sr.X * scale;
                        double y = offsetY + sr.Y * scale;
                        double w = sr.Width * scale;
                        double h = sr.Height * scale;

                        var r = _overlayRects[i];
                        Canvas.SetLeft(r, x);
                        Canvas.SetTop(r, y);
                        r.Width = Math.Max(0, w);
                        r.Height = Math.Max(0, h);
                        r.Visibility = Visibility.Visible;

                        var lb = _overlayLabels[i];
                        if (overlayLabelTexts != null && i < overlayLabelTexts.Length)
                            lb.Text = overlayLabelTexts[i];
                        else
                            lb.Text = string.Empty;

                        Canvas.SetLeft(lb, x + 4);
                        Canvas.SetTop(lb, y + 4);
                        lb.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _overlayRects[i].Visibility = Visibility.Collapsed;
                        _overlayLabels[i].Visibility = Visibility.Collapsed;
                    }
                }
            }
            catch
            {
                // tolerate overlay draw errors
            }
        }
    }
}