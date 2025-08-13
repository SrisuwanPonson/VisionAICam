using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.IO;
using System.Xml.Serialization;

namespace VisionAICam
{
    public static class SettingsManager
    {
        private static readonly string SettingsFile = "settings.xml";

        public static AppSettings Load()
        {
            if (!File.Exists(SettingsFile))
                return new AppSettings();

            using var stream = File.OpenRead(SettingsFile);
            var serializer = new XmlSerializer(typeof(AppSettings));
            return (AppSettings)serializer.Deserialize(stream)!;
        }

        public static void Save(AppSettings settings)
        {
            using var stream = File.Create(SettingsFile);
            var serializer = new XmlSerializer(typeof(AppSettings));
            serializer.Serialize(stream, settings);
        }
    }
}
