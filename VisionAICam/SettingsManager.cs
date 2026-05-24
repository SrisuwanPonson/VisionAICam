using System;
using System.IO;
using System.Xml.Serialization;

namespace VisionAICam
{
    public static class SettingsManager
    {
        // Persist settings under: C:\ClearEngine\VisionAICam\Setup\settings.xml
        private static readonly string SettingsFolder = @"C:\ClearEngine\VisionAICam\Setup";
        private static readonly string SettingsFile = Path.Combine(SettingsFolder, "settings.xml");

        static SettingsManager()
        {
            try
            {
                if (!Directory.Exists(SettingsFolder))
                    Directory.CreateDirectory(SettingsFolder);
            }
            catch
            {
                // tolerate creation failures; callers may surface errors if needed
            }
        }

        public static AppSettings Load()
        {
            try
            {
                if (!File.Exists(SettingsFile))
                    return new AppSettings();

                using var stream = File.OpenRead(SettingsFile);
                var serializer = new XmlSerializer(typeof(AppSettings));
                return (AppSettings)serializer.Deserialize(stream)!;
            }
            catch
            {
                // If anything goes wrong, return defaults instead of throwing to avoid breaking UI on startup
                return new AppSettings();
            }
        }

        public static void Save(AppSettings settings)
        {
            try
            {
                var dir = Path.GetDirectoryName(SettingsFile);
                if (!string.IsNullOrWhiteSpace(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);

                using var stream = File.Create(SettingsFile);
                var serializer = new XmlSerializer(typeof(AppSettings));
                serializer.Serialize(stream, settings);
            }
            catch
            {
                // swallow to avoid crashing UI; surface errors elsewhere if you want prompt/notification
            }
        }
    }
}
