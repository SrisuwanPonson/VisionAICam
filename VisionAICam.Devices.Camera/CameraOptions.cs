using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClearEngine.Devices.Camera
{
    // Simple set of camera options so the library does not depend on the application AppSettings type.
    public class CameraOptions
    {
        public double Brightness { get; set; }
        public double Contrast { get; set; }
        public double Exposure { get; set; }
    }
}
