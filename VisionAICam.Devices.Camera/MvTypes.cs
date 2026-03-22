using System;
using System.Runtime.InteropServices;

namespace ClearEngine.Devices.Camera.Hikvision.Native
{
    // ============================
    // Device List
    // ============================
    [StructLayout(LayoutKind.Sequential)]
    internal struct MV_CC_DEVICE_INFO_LIST
    {
        public uint nDeviceNum;
        public IntPtr pDeviceInfo; // pointer to MV_CC_DEVICE_INFO array
    }

    // ============================
    // Device Info
    // ============================
    [StructLayout(LayoutKind.Sequential)]
    public struct MV_CC_DEVICE_INFO
    {
        public ushort nMajorVer;
        public ushort nMinorVer;

        public uint nMacAddrHigh;
        public uint nMacAddrLow;

        public uint nTLayerType;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public uint[] Reserved;
    }

    // ============================
    // Frame Info
    // ============================
    [StructLayout(LayoutKind.Sequential)]
    internal struct MV_FRAME_OUT_INFO_EX
    {
        public uint nWidth;
        public uint nHeight;

        public uint enPixelType;

        public uint nFrameNum;

        public uint nDevTimeStampHigh;
        public uint nDevTimeStampLow;

        public uint nReserved0;
        public uint nReserved1;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public uint[] Reserved;
    }

    // ============================
    // Transport Layer Flags
    // ============================
    [Flags]
    internal enum MV_TRANSPORT_LAYER : uint
    {
        MV_GIGE_DEVICE = 0x00000001,
        MV_USB_DEVICE = 0x00000002
    }

    // ============================
    // Constants
    // ============================
    internal static class MvConstants
    {
        public const int MV_OK = 0;
        public const uint MV_ACCESS_Exclusive = 1;
        public const int DefaultTimeoutMs = 1000;
    }
}