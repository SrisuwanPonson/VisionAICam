using System;
using System.Runtime.InteropServices;

namespace ClearEngine.Devices.Camera.Hikvision.Native
{
    internal sealed class MvCameraHandle : SafeHandle
    {
        public MvCameraHandle() : base(IntPtr.Zero, true) { }

        public override bool IsInvalid => handle == IntPtr.Zero;

        // Allow HikCamera.Open() to assign the native handle safely
        internal void Initialize(IntPtr h)
        {
            SetHandle(h);
        }

        protected override bool ReleaseHandle()
        {
            if (IsInvalid)
                return true;

            var h = handle;
            handle = IntPtr.Zero;

            var ret = MvCameraNative.MV_CC_DestroyHandle(h);
            return ret == MvConstants.MV_OK;
        }
    }
}