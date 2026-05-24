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

        // Reference color defaults (will be persisted in settings XML)
        // Red reference (example default: R=121,G=51,B=55)
        public byte RefRedR { get; set; } = 121;
        public byte RefRedG { get; set; } = 51;
        public byte RefRedB { get; set; } = 55;

        // Green reference (example default: R=77,G=93,B=85)
        public byte RefGreenR { get; set; } = 77;
        public byte RefGreenG { get; set; } = 93;
        public byte RefGreenB { get; set; } = 85;

        // Blue reference (example default: R=43,G=66,B=103)
        public byte RefBlueR { get; set; } = 43;
        public byte RefBlueG { get; set; } = 66;
        public byte RefBlueB { get; set; } = 103;

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

        // Convenience tuple-like accessors for runtime use (not serialized directly)
        [XmlIgnore]
        public (byte R, byte G, byte B) RefRed
        {
            get => (RefRedR, RefRedG, RefRedB);
            set
            {
                RefRedR = value.R;
                RefRedG = value.G;
                RefRedB = value.B;
            }
        }

        [XmlIgnore]
        public (byte R, byte G, byte B) RefGreen
        {
            get => (RefGreenR, RefGreenG, RefGreenB);
            set
            {
                RefGreenR = value.R;
                RefGreenG = value.G;
                RefGreenB = value.B;
            }
        }

        [XmlIgnore]
        public (byte R, byte G, byte B) RefBlue
        {
            get => (RefBlueR, RefBlueG, RefBlueB);
            set
            {
                RefBlueR = value.R;
                RefBlueG = value.G;
                RefBlueB = value.B;
            }
        }

        // Add this property to your AppSettings class (insert among other persisted properties)
        // Path to project / training scripts root (used by training helpers, scripts, etc.)
        public string ScriptPath { get; set; } = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script");

        // Add these three persisted tolerance properties to your AppSettings class.
        public double TolerancePercentR { get; set; } = 15.0;
        public double TolerancePercentG { get; set; } = 15.0;
        public double TolerancePercentB { get; set; } = 15.0;
    }
}
