using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace VisionAICam.Pages
{
    public partial class DataSetPage
    {
        // Non-destructive helper: compute per-class counts and update UI indicator.
        // Threshold fixed at 5 as requested.
        private void CheckAutoLabelReady()
        {
            try
            {
                var labels = LabelComboBox?.Items.Cast<object>()
                    .Select(i => i?.ToString() ?? "")
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToList() ?? new List<string>();

                // If no classes defined, hide indicator
                if (labels.Count == 0)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (AutoLabelReadyTextBlock != null)
                        {
                            AutoLabelReadyTextBlock.Text = string.Empty;
                            AutoLabelReadyTextBlock.Visibility = Visibility.Collapsed;
                        }
                    });
                    return;
                }

                // Count annotations per class (case-insensitive)
                var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var l in labels) counts[l] = 0;

                foreach (var ann in Annotations)
                {
                    if (string.IsNullOrWhiteSpace(ann.Label)) continue;
                    if (counts.ContainsKey(ann.Label))
                        counts[ann.Label]++;
                }

                const int readyThreshold = 5;
                bool allReached = labels.All(l => counts.TryGetValue(l, out var c) && c >= readyThreshold);

                Dispatcher.Invoke(() =>
                {
                    if (AutoLabelReadyTextBlock == null) return;

                    if (allReached)
                    {
                        AutoLabelReadyTextBlock.Text = $"Auto label ready (≥{readyThreshold})";
                        AutoLabelReadyTextBlock.Foreground = new SolidColorBrush(Color.FromRgb(56, 142, 60));
                        AutoLabelReadyTextBlock.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        AutoLabelReadyTextBlock.Text = string.Empty;
                        AutoLabelReadyTextBlock.Visibility = Visibility.Collapsed;
                    }
                });
            }
            catch
            {
                // non-critical: swallow exceptions to avoid breaking UI
            }
        }
    }
}