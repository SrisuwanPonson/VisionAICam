using System;
using System.IO;

namespace VisionAICam
{
    // Singleton Logger design pattern
    internal sealed class Logger
    {
        private static readonly Lazy<Logger> _instance = new(() => new Logger());
        private readonly string _logFilePath;
        private readonly string logDirectory;

        // Private constructor to prevent instantiation
        private Logger()
        {
            logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "log");
            Directory.CreateDirectory(logDirectory);
            var logFileName = $"log_{DateTime.Now:yyyyMMdd_HHmmss}.txt";
            _logFilePath = Path.Combine(logDirectory, logFileName);
        }

        public static Logger Instance => _instance.Value;


        public void LogInfo(string message)
        {
            Log("INFO", message);
        }

        public void LogWarning(string message)
        {
            Log("WARNING", message);
        }

        public void LogError(string message)
        {
            Log("ERROR", message);
        }

        private void Log(string level, string message)
        {
            var logEntry = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{level}] {message}";
            try
            {
                File.AppendAllText(_logFilePath, logEntry + Environment.NewLine);
            }
            catch
            {
                // Optionally handle logging errors (e.g., write to event log)
            }
        }

        internal void LogInfo(object export, string v)
        {
            throw new NotImplementedException();
        }

        internal string GetLogDirectory()
        {
            return logDirectory;
        }
    }
}
