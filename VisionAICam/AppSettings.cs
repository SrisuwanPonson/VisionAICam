using System;
using System.Collections.Generic;
using System.Xml.Serialization;
using ClearEngine.Devices.Camera;

namespace VisionAICam
{
    public class ClassIdEntry
    {
        public string Name { get; set; } = "";
        public ushort Id { get; set; } = 0;
    }

    public class AppSettings
    {
        // -----------------------------
        // OpenCV camera settings
        // -----------------------------
        public int CameraIndex { get; set; } = 0;
        public double Brightness { get; set; } = 128;
        public double Contrast { get; set; } = 128;
        public double Exposure { get; set; } = -6;

        // -----------------------------
        // ⭐ Camera backend selection
        // -----------------------------
        public CameraBackend CameraBackend { get; set; } = CameraBackend.OpenCv;

        // -----------------------------
        // ⭐ Hikvision camera settings
        // -----------------------------
        public int HikCameraIndex { get; set; } = 0;
        public double HikExposureTime { get; set; } = 25000;
        public double HikGain { get; set; } = 10;
        public double HikGamma { get; set; } = 1.5;
        public double HikBlackLevel { get; set; } = 2;

        // -----------------------------
        // Model
        // -----------------------------
        public string DefaultModelPath { get; set; } = "";

        // -----------------------------
        // Theme / UI
        // -----------------------------
        public string Theme { get; set; } = "Light";
        public string DefaultImagePath { get; set; } = "";

        // -----------------------------
        // Inference settings
        // -----------------------------
        // ✅ FIX: Set default Python DLL path to your actual location
        public string PythonDllPath { get; set; } = @"C:\ClearEngine\VisionAICam\PythonEnv\Python313\python313.dll";

        public string InferenceRuntime { get; set; } = "pythonnet";
        public bool InferenceEnableCaching { get; set; } = true;
        public bool InferencePrewarm { get; set; } = false;
        public int SamplingInterval { get; set; } = 40;
        public double PolygonAutoCloseThreshold { get; set; } = 12.0;

        // -----------------------------
        // Robot settings
        // -----------------------------
        public string MasterControllerIp { get; set; } = "192.168.1.6";
        public int MasterControllerPort { get; set; } = 502;
        public bool SwapFloatWords { get; set; } = false;
        public ushort RobotRegisterAddress { get; set; } = 10;

        // -----------------------------
        // Reference colors
        // -----------------------------
        public byte RefRedR { get; set; } = 121;
        public byte RefRedG { get; set; } = 51;
        public byte RefRedB { get; set; } = 55;

        public byte RefGreenR { get; set; } = 77;
        public byte RefGreenG { get; set; } = 93;
        public byte RefGreenB { get; set; } = 85;

        public byte RefBlueR { get; set; } = 43;
        public byte RefBlueG { get; set; } = 66;
        public byte RefBlueB { get; set; } = 103;

        // -----------------------------
        // Class ID mapping
        // -----------------------------
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

        [XmlIgnore]
        public (byte R, byte G, byte B) RefRed
        {
            get => (RefRedR, RefRedG, RefRedB);
            set { RefRedR = value.R; RefRedG = value.G; RefRedB = value.B; }
        }

        [XmlIgnore]
        public (byte R, byte G, byte B) RefGreen
        {
            get => (RefGreenR, RefGreenG, RefGreenB);
            set { RefGreenR = value.R; RefGreenG = value.G; RefGreenB = value.B; }
        }

        [XmlIgnore]
        public (byte R, byte G, byte B) RefBlue
        {
            get => (RefBlueR, RefBlueG, RefBlueB);
            set { RefBlueR = value.R; RefBlueG = value.G; RefBlueB = value.B; }
        }

        // -----------------------------
        // Script path
        // -----------------------------
        public string ScriptPath { get; set; } =
            System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "Script");

        // -----------------------------
        // Tolerances
        // -----------------------------
        public double TolerancePercentR { get; set; } = 15.0;
        public double TolerancePercentG { get; set; } = 15.0;
        public double TolerancePercentB { get; set; } = 15.0;

        // ✅ Changed from 'internal set' to 'set' so it can be saved to XML
        public bool EnableAutoSnapshot { get; set; } = false;
    }
}