using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClearEngine.Devices.Camera
{
    public enum CameraBackend { OpenCv }

    public static class CameraFactory
    {
        public static ICamera Create(CameraBackend backend = CameraBackend.OpenCv)
        {
            return backend switch
            {
                CameraBackend.OpenCv => new OpenCvCamera(),
                _ => throw new NotSupportedException($"Camera backend '{backend}' not supported.")
            };
        }
    }
}
