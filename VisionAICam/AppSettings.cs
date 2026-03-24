using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;

namespace VisionAICam
{
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
        public int SamplingInterval { get; set; }=40; // in milliseconds
        public double PolygonAutoCloseThreshold { get; set; } = 12.0;

        // Master controller / robot connection settings
        public string MasterControllerIp { get; set; } = "192.168.1.6";
        public int MasterControllerPort { get; set; } = 502;
        public bool SwapFloatWords { get; set; } = false;
    }
}
