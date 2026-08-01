using System;
using System.IO;
using System.Reflection;

namespace VisionAICam.Core
{
    /// <summary>
    /// Application version and build information
    /// </summary>
    public static class AppInfo
    {
        /// <summary>
        /// Application version (e.g., "1.0.0")
        /// </summary>
        public static readonly string Version = GetVersion();

        /// <summary>
        /// Build revision number (e.g., "0001" or Git hash)
        /// </summary>
        public static readonly string Revision = GetRevision();

        /// <summary>
        /// Build date (e.g., "2024-08-01")
        /// </summary>
        public static readonly string BuildDate = GetBuildDate();

        /// <summary>
        /// Full version string
        /// </summary>
        public static readonly string FullVersion = $"v{Version} | Rev: {Revision} | {BuildDate}";

        private static string GetVersion()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var version = assembly.GetName().Version;
                return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "1.0.0";
            }
            catch
            {
                return "1.0.0";
            }
        }

        private static string GetRevision()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();

                // Try InformationalVersion (supports Git hash)
                var infoVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
                if (infoVersion != null && !string.IsNullOrEmpty(infoVersion.InformationalVersion))
                {
                    return infoVersion.InformationalVersion;
                }

                // Fallback to build number
                var version = assembly.GetName().Version;
                if (version != null && version.Build > 0)
                {
                    return $"{version.Build:D4}";
                }

                return "0000";
            }
            catch
            {
                return "0000";
            }
        }

        private static string GetBuildDate()
        {
            try
            {
                var assembly = Assembly.GetExecutingAssembly();
                var fileInfo = new FileInfo(assembly.Location);
                return fileInfo.CreationTime.ToString("yyyy-MM-dd");
            }
            catch
            {
                return DateTime.Now.ToString("yyyy-MM-dd");
            }
        }
    }
}