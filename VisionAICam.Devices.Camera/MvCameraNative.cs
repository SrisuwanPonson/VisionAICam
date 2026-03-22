using System;
using System.Runtime.InteropServices;

namespace ClearEngine.Devices.Camera.Hikvision.Native
{
    internal static class MvCameraNative
    {
        private const string DllName = "MvCameraControl.dll"; // ensure this is present in runtimes/win-x64/native

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint MV_CC_EnumDevices(
            uint nTLayerType,
            ref MV_CC_DEVICE_INFO_LIST pDeviceInfoList);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_CreateHandle(
            out IntPtr handle,
            ref MV_CC_DEVICE_INFO pDeviceInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_DestroyHandle(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_OpenDevice(
            IntPtr handle,
            uint nAccessMode,
            ushort nSwitchoverKey);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_CloseDevice(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_StartGrabbing(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_StopGrabbing(
            IntPtr handle);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int MV_CC_GetOneFrameTimeout(
            IntPtr handle,
            IntPtr pData,
            uint nDataSize,
            ref MV_FRAME_OUT_INFO_EX pFrameInfo,
            int nMsec);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern uint MV_CC_GetSDKVersion();
    }
}