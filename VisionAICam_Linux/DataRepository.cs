using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;

namespace VisionAICam_Linux
{
    public class DataRecord
    {
        public string Filename { get; set; } = "";
        public string ClassName { get; set; } = "";
        public double Confidence { get; set; }
        public string FilePath { get; set; } = "";
        public DateTime Timestamp { get; set; }
    }

    // Simple thread-safe singleton repository for captured/detection records.
    public sealed class DataRepository
    {
        private static readonly Lazy<DataRepository> _lazy = new(() => new DataRepository());
        public static DataRepository Instance => _lazy.Value;

        public ObservableCollection<DataRecord> Records { get; } = new ObservableCollection<DataRecord>();

        private DataRepository() { }

        public void AddRecord(string filename, string className, double confidence, string filePath, DateTime timestamp)
        {
            try
            {
                // Ensure additions happen on UI thread so UI bindings are safe.
                var record = new DataRecord
                {
                    Filename = filename,
                    ClassName = className,
                    Confidence = confidence,
                    FilePath = filePath,
                    Timestamp = timestamp
                };

                if (Application.Current?.Dispatcher == null || Application.Current.Dispatcher.CheckAccess())
                {
                    Records.Add(record);
                }
                else
                {
                    Application.Current.Dispatcher.Invoke(() => Records.Add(record));
                }
            }
            catch
            {
                // best-effort: swallow any error so production can't crash the camera loop
            }
        }
    }
}