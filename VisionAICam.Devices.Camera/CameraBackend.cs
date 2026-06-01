namespace ClearEngine.Devices.Camera
{
    public enum CameraBackend
    {
        OpenCv,
        Hikvision
    }

    public static class CameraFactory
    {
        public static ICamera Create(CameraBackend backend = CameraBackend.OpenCv)
        {
            return backend switch
            {
                CameraBackend.OpenCv    => new OpenCvCamera(),
                CameraBackend.Hikvision => new HikCamera(),
                _ => throw new NotSupportedException($"Camera backend '{backend}' not supported.")
            };
        }
    }
}
