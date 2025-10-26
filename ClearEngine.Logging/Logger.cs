using System;
using System.IO;
using System.Text.Json;

namespace ClearEngine.Logging
{
    /// <summary>
    /// Simple file-backed singleton logger implementing <see cref="ILogger"/>.
    /// Designed to be used by libraries and the host application.
    /// </summary>
    public sealed class Logger : ILogger
    {
        private static readonly Lazy<Logger> _instance = new(() => new Logger());
        private readonly string _logDirectory;
        private readonly string _logFilePath;
        private readonly object _sync = new();

        private Logger()
        {
            _logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "log");
            try
            {
                Directory.CreateDirectory(_logDirectory);
            }
            catch
            {
                // best-effort, swallow to avoid throwing from logger ctor
            }

            var fileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            _logFilePath = Path.Combine(_logDirectory, fileName);
        }

        public static Logger Instance => _instance.Value;

        // Friendly aliases
        public void Info(string message) => LogInfo(message);
        public void Error(string message) => LogError(message);

        public void LogInfo(string message) => Write("INFO", message);
        public void LogWarning(string message) => Write("WARNING", message);
        public void LogError(string message) => Write("ERROR", message);

        public string GetLogDirectory() => _logDirectory;

        public void LogInfo(object export, string tag)
        {
            try
            {
                string payload;
                if (export == null)
                {
                    payload = "<null>";
                }
                else
                {
                    var options = new JsonSerializerOptions
                    {
                        WriteIndented = false,
                        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
                    };
                    payload = JsonSerializer.Serialize(export, options);
                }

                LogInfo($"{tag}: {payload}");
            }
            catch
            {
                try { LogInfo($"{tag}: {export?.ToString() ?? "<null>"}"); } catch { }
            }
        }

        private void Write(string level, string message)
        {
            var entry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            lock (_sync)
            {
                try
                {
                    File.AppendAllText(_logFilePath, entry + Environment.NewLine);
                }
                catch
                {
                    // swallow - logging must not throw
                }
            }
        }
    }
}