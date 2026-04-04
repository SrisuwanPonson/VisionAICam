using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace VisionAICam
{
    // Serializable entry for XML-friendly class->id mapping
    public class ClassIdEntry
    {
        public string Name { get; set; } = "";
        public ushort Id { get; set; } = 0;
    }

    public class AppSettings
    {
        public int CameraIndex { get; set; } = 0;
        public double Brightness { get; set; } = 128;
        public double Contrast { get; set; } = 128;
        public double Exposure { get; set; } = -6;

        // keep this if you still use Model tab elsewhere
        public string DefaultModelPath { get; set; } = "";

        public string Theme { get; set; } = "Light";
        public string DefaultImagePath { get; set; } = "";

        // New inference settings
        public string PythonDllPath { get; set; } = "";
        public string InferenceRuntime { get; set; } = "pythonnet"; // or "onnx" etc.
        public bool InferenceEnableCaching { get; set; } = true;
        public bool InferencePrewarm { get; set; } = false;
        public int SamplingInterval { get; set; } = 40; // in milliseconds
        public double PolygonAutoCloseThreshold { get; set; } = 12.0;

        // Master controller / robot connection settings
        public string MasterControllerIp { get; set; } = "192.168.1.6";
        public int MasterControllerPort { get; set; } = 502;
        public bool SwapFloatWords { get; set; } = false;

        // NEW: Robot register address to write the mapped class id into
        public ushort RobotRegisterAddress { get; set; } = 10;

        // XML-friendly list persisted by existing XmlSerializer.
        // Use ClassIdMap (non-serialized) at runtime for convenient lookups.
        public List<ClassIdEntry> ClassIdEntries { get; set; } = new List<ClassIdEntry>
        {
            new ClassIdEntry { Name = "person", Id = 1 },
            new ClassIdEntry { Name = "car", Id = 2 },
            new ClassIdEntry { Name = "truck", Id = 3 },
            new ClassIdEntry { Name = "bicycle", Id = 4 },
            new ClassIdEntry { Name = "motorbike", Id = 5 },
            new ClassIdEntry { Name = "cat", Id = 6 },
            new ClassIdEntry { Name = "dog", Id = 7 }
        };

        // Runtime dictionary built from ClassIdEntries. Not serialized directly.
        [XmlIgnore]
        public Dictionary<string, ushort> ClassIdMap
        {
            get
            {
                var map = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase);
                if (ClassIdEntries != null)
                {
                    foreach (var e in ClassIdEntries)
                    {
                        if (!string.IsNullOrWhiteSpace(e?.Name))
                        {
                            try { map[e.Name!] = e.Id; } catch { }
                        }
                    }
                }
                return map;
            }
            set
            {
                ClassIdEntries = new List<ClassIdEntry>();
                if (value != null)
                {
                    foreach (var kv in value)
                        ClassIdEntries.Add(new ClassIdEntry { Name = kv.Key, Id = kv.Value });
                }
            }
        }
    }
}
