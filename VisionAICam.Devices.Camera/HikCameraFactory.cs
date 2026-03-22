using System;
using System.Collections.Generic;
using ClearEngine.Devices.Camera.Hikvision.Native;

namespace ClearEngine.Devices.Camera.Hikvision
{
    public static class HikCameraFactory
    {
        // Enumerate all Hikvision devices
        public static IReadOnlyList<MV_CC_DEVICE_INFO> EnumerateDevices()
            => HikCamera.Enumerate();

        // Open a device by index
        public static HikCamera Open(int index, int width = 1920, int height = 1080, uint pixelType = 0)
        {
            var devices = HikCamera.Enumerate();

            if (devices.Count == 0)
                throw new InvalidOperationException("No Hikvision devices found.");

            if (index < 0 || index >= devices.Count)
                throw new ArgumentOutOfRangeException(nameof(index));

            return HikCamera.Open(devices[index], width, height, pixelType);
        }
    }
}