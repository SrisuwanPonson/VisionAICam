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
        public string DefaultModelPath { get; set; } = "";
        public string Theme { get; set; } = "Light";
    }
}
