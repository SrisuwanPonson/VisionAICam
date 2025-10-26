namespace ClearEngine.Logging
{
    /// <summary>
    /// Minimal logging contract for libraries. Host application should provide an implementation.
    /// </summary>
    public interface ILogger
    {
        void Info(string message);
        void Error(string message);

        // Compatibility helpers used by existing code
        void LogInfo(string message);
        void LogWarning(string message);
        void LogError(string message);

        // Return a directory path suitable for writing python / inference logs.
        string GetLogDirectory();

        // Serialize/inspect an object for logging (optional helper)
        void LogInfo(object export, string tag);
    }
}