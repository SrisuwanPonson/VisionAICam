using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Collections.Specialized;
using System.Collections.ObjectModel;
using VisionAICam.Core;
using VisionAICam.Services;

namespace VisionAICam.Pages
{
    public partial class DataPage : Page
    {
        // track whether ResultsGrid is currently showing live shared results
        private bool _showingLive = true;

        public DataPage()
        {
            InitializeComponent();
            Loaded += DataPage_Loaded;
            Unloaded += DataPage_Unloaded;
        }

        private void DataPage_Loaded(object? sender, RoutedEventArgs e)
        {
            // Bind results grid to shared collection from MasterController (live mode)
            BindToLiveResults();

            // Update stats initially and on collection change
            UpdateStatistics();
            if (MasterController.Instance.SharedResults is INotifyCollectionChanged nc)
                nc.CollectionChanged += SharedResults_CollectionChanged;

            // Hook selection changed to show details
            ResultsGrid.SelectionChanged += ResultsGrid_SelectionChanged;

            // Subscribe to DB-changed notifications so the UI can reload if the user is viewing DB snapshot
            var store = MasterController.Instance.GetService<PredictionStore>() ?? PredictionStore.Instance;
            store.DatabaseChanged += PredictionStore_DatabaseChanged;
        }

        private void DataPage_Unloaded(object? sender, RoutedEventArgs e)
        {
            var shared = MasterController.Instance.SharedResults;
            if (shared is INotifyCollectionChanged nc)
                nc.CollectionChanged -= SharedResults_CollectionChanged;

            ResultsGrid.SelectionChanged -= ResultsGrid_SelectionChanged;

            // Unsubscribe DB change notifications
            var store = MasterController.Instance.GetService<PredictionStore>() ?? PredictionStore.Instance;
            store.DatabaseChanged -= PredictionStore_DatabaseChanged;
        }

        private void PredictionStore_DatabaseChanged(object? sender, EventArgs e)
        {
            // Only reload when currently showing the DB snapshot (avoid interrupting live view).
            if (_showingLive) return;

            // UI work must run on UI thread
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    // Re-load DB snapshot into the grid
                    LoadDbButton_Click(this, null!);
                }
                catch { /* non-fatal */ }
            }));
        }

        private void SharedResults_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            // UI work must run on UI thread
            Dispatcher.BeginInvoke(new Action(UpdateStatistics));
        }

        private void UpdateStatistics()
        {
            try
            {
                // If showing live, use SharedResults; otherwise use whatever ItemsSource contains
                if (_showingLive)
                {
                    var items = MasterController.Instance.SharedResults;
                    int total = items.Count;
                    TotalRecordsText.Text = $"Total Records: {total}";

                    if (total == 0)
                    {
                        AverageConfidenceText.Text = "Average Confidence: N/A";
                        ClassBreakdownText.Text = "Detected Classes: None";
                        return;
                    }

                    var avg = items.Average(i => i.Confidence);
                    AverageConfidenceText.Text = $"Average Confidence: {avg:P1}";

                    var groups = items
                        .GroupBy(i => string.IsNullOrWhiteSpace(i.ClassName) ? "(unknown)" : i.ClassName)
                        .Select(g => $"{g.Key}({g.Count()})")
                        .ToArray();

                    ClassBreakdownText.Text = $"Detected Classes: {string.Join(", ", groups)}";
                }
                else
                {
                    // ItemsSource is a snapshot (PredictionEntity). Compute stats from that collection.
                    var view = ResultsGrid.ItemsSource as System.Collections.IEnumerable;
                    if (view == null)
                    {
                        TotalRecordsText.Text = "Total Records: 0";
                        AverageConfidenceText.Text = "Average Confidence: N/A";
                        ClassBreakdownText.Text = "Detected Classes: None";
                        return;
                    }

                    var list = view.Cast<object>().ToList();
                    TotalRecordsText.Text = $"Total Records: {list.Count}";

                    var confidences = list.Select(it =>
                    {
                        if (it is PredictionEntity pe) return (double?)pe.Confidence;
                        if (it is DetectionResult dr) return (double?)dr.Confidence;
                        return null;
                    }).Where(d => d.HasValue).Select(d => d!.Value).ToList();

                    if (confidences.Count == 0)
                    {
                        AverageConfidenceText.Text = "Average Confidence: N/A";
                    }
                    else
                    {
                        AverageConfidenceText.Text = $"Average Confidence: {confidences.Average():P1}";
                    }

                    var classes = list.Select(it =>
                    {
                        if (it is PredictionEntity pe) return pe.ClassName;
                        if (it is DetectionResult dr) return dr.ClassName;
                        return "(unknown)";
                    }).Where(s => !string.IsNullOrWhiteSpace(s))
                      .GroupBy(s => s)
                      .Select(g => $"{g.Key}({g.Count()})")
                      .ToArray();

                    ClassBreakdownText.Text = classes.Length == 0 ? "Detected Classes: None" : $"Detected Classes: {string.Join(", ", classes)}";
                }
            }
            catch { /* non-fatal */ }
        }

        private void ResultsGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowSelectedDetails();
        }

        private void ShowSelectedDetails()
        {
            var selObj = ResultsGrid.SelectedItem;
            if (selObj == null)
            {
                DetailsTextBlock.Text = "Select a row to see details.";
                PreviewImage.Source = null;
                return;
            }

            string timestamp = "";
            string className = "";
            double confidence = 0;
            string box = "";
            string task = "";

            if (selObj is DetectionResult dr)
            {
                timestamp = dr.Timestamp.ToString("o");
                className = dr.ClassName;
                confidence = dr.Confidence;
                box = dr.Box;
                task = dr.Task;
            }
            else if (selObj is PredictionEntity pe)
            {
                timestamp = pe.Timestamp.ToString("o");
                className = pe.ClassName;
                confidence = pe.Confidence;
                box = pe.Box;
                task = pe.Task;
            }

            var sb = new StringBuilder();
            sb.AppendLine($"Timestamp: {timestamp}");
            sb.AppendLine($"Class: {className}");
            sb.AppendLine($"Confidence: {confidence:P2}");
            sb.AppendLine($"Box: {box}");
            sb.AppendLine($"Task: {task}");

            DetailsTextBlock.Text = sb.ToString();
            PreviewImage.Source = null; // no image persisted by default
        }

        private void FilterButton_Click(object sender, RoutedEventArgs e)
        {
            var view = CollectionViewSource.GetDefaultView(ResultsGrid.ItemsSource);
            if (view == null) return;

            string query = (SearchTextBox?.Text ?? "").Trim();
            if (string.IsNullOrEmpty(query))
            {
                view.Filter = null;
            }
            else
            {
                view.Filter = obj =>
                {
                    if (obj is DetectionResult dr)
                    {
                        return (dr.ClassName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                               || (dr.Box ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    if (obj is PredictionEntity pe)
                    {
                        return (pe.ClassName ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                               || (pe.Box ?? "").IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                    return false;
                };
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // export whatever collection is currently shown
                var items = (ResultsGrid.ItemsSource as System.Collections.IEnumerable)?.Cast<object>().ToArray() ?? Array.Empty<object>();
                if (items.Length == 0)
                {
                    MessageBox.Show("No records to export.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
                    FileName = $"predictions_{DateTime.Now:yyyyMMdd_HHmmss}.csv"
                };

                if (dlg.ShowDialog() != true) return;

                using var sw = new StreamWriter(dlg.FileName, false, Encoding.UTF8);
                sw.WriteLine("Timestamp,ClassName,Confidence,Box,Task");
                foreach (var it in items)
                {
                    if (it is DetectionResult dr)
                        sw.WriteLine($"\"{dr.Timestamp:o}\",\"{Escape(dr.ClassName)}\",{dr.Confidence},\"{Escape(dr.Box)}\",\"{Escape(dr.Task)}\"");
                    else if (it is PredictionEntity pe)
                        sw.WriteLine($"\"{pe.Timestamp:o}\",\"{Escape(pe.ClassName)}\",{pe.Confidence},\"{Escape(pe.Box)}\",\"{Escape(pe.Task)}\"");
                }

                MessageBox.Show($"Exported {items.Length} records to {dlg.FileName}.", "Export", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Export failed: {ex.Message}", "Export", MessageBoxButton.OK, MessageBoxImage.Error);
            }

            static string Escape(string? s) => (s ?? "").Replace("\"", "\"\"");
        }

        private async void DeleteButton_Click(object sender, RoutedEventArgs e)
        {
            var selObj = ResultsGrid.SelectedItem;
            if (selObj == null)
            {
                MessageBox.Show("Select a row to delete.", "Delete", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (MessageBox.Show("Delete selected prediction?", "Confirm", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                return;

            try
            {
                // If live view, remove from shared collection
                if (_showingLive && selObj is DetectionResult dr)
                {
                    MasterController.Instance.SharedResults.Remove(dr);
                    var storeLive = MasterController.Instance.GetService<PredictionStore>() ?? PredictionStore.Instance;
                    await storeLive.ReplaceDetectionResultsDeltaAsync(MasterController.Instance.SharedResults);
                }
                else if (!_showingLive && selObj is PredictionEntity pe)
                {
                    // remove from DB and refresh grid
                    var store = MasterController.Instance.GetService<PredictionStore>() ?? PredictionStore.Instance;
                    var remaining = ((ResultsGrid.ItemsSource as System.Collections.IEnumerable)?
                                     .Cast<PredictionEntity>()
                                     .Where(x => x.Id != pe.Id)
                                     .ToList()) ?? new System.Collections.Generic.List<PredictionEntity>();
                    await store.ReplaceWithLatestAsync(remaining);
                    // reload DB view
                    LoadDbButton_Click(this, null!);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Delete failed: {ex.Message}", "Delete", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Load persisted records from DB and show them in the grid (snapshot)
        private void LoadDbButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var store = MasterController.Instance.GetService<PredictionStore>() ?? PredictionStore.Instance;
                var list = store.QueryRecent(1000).ToList();
                ResultsGrid.ItemsSource = new ObservableCollection<PredictionEntity>(list);
                _showingLive = false;
                if (ModeText != null) ModeText.Text = "Mode: DB";
                UpdateStatistics();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Load DB failed: {ex.Message}", "Load DB", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // Return to live in-memory stream view
        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            BindToLiveResults();
            UpdateStatistics();
        }

        private void BindToLiveResults()
        {
            ResultsGrid.ItemsSource = MasterController.Instance.SharedResults;
            _showingLive = true;
            if (ModeText != null) ModeText.Text = "Mode: Live";
        }

        // Context menu "Show Details"
        private void ShowDetailsContext_Click(object sender, RoutedEventArgs e)
        {
            ShowSelectedDetails();
        }
    }
}
