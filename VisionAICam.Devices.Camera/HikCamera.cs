using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClearEngine.Devices.Camera.Hikvision.Native;

namespace ClearEngine.Devices.Camera.Hikvision
{
    public sealed class HikCamera : IDisposable
    {
        private readonly MvCameraHandle _handle;
        private readonly byte[] _buffer;
        private bool _disposed;

        public bool IsOpened { get; private set; }
        public int Width { get; }
        public int Height { get; }
        public uint PixelType { get; }

        private HikCamera(MvCameraHandle handle, int width, int height, uint pixelType)
        {
            _handle = handle;
            Width = width;
            Height = height;
            PixelType = pixelType;

            // Allocate BGR8 buffer
            _buffer = ArrayPool<byte>.Shared.Rent(width * height * 3);
            IsOpened = true;
        }

        // ============================
        // Enumerate Devices
        // ============================
        public static IReadOnlyList<MV_CC_DEVICE_INFO> Enumerate()
        {
            var list = new MV_CC_DEVICE_INFO_LIST();

            var ret = MvCameraNative.MV_CC_EnumDevices(
                (uint)(MV_TRANSPORT_LAYER.MV_GIGE_DEVICE | MV_TRANSPORT_LAYER.MV_USB_DEVICE),
                ref list);

            if (ret != MvConstants.MV_OK || list.nDeviceNum == 0)
                return Array.Empty<MV_CC_DEVICE_INFO>();

            var result = new List<MV_CC_DEVICE_INFO>((int)list.nDeviceNum);
            var size = Marshal.SizeOf<MV_CC_DEVICE_INFO>();

            for (int i = 0; i < list.nDeviceNum; i++)
            {
                var ptr = IntPtr.Add(list.pDeviceInfo, i * size);
                var info = Marshal.PtrToStructure<MV_CC_DEVICE_INFO>(ptr);
                result.Add(info);
            }

            return result;
        }

        // ============================
        // Open Camera
        // ============================
        public static HikCamera Open(ClearEngine.Devices.Camera.Hikvision.Native.MV_CC_DEVICE_INFO devInfo, int width, int height, uint pixelType)
        {
            var ret = MvCameraNative.MV_CC_CreateHandle(out var rawHandle, ref devInfo);
            if (ret != MvConstants.MV_OK)
                throw new InvalidOperationException($"MV_CC_CreateHandle failed: {ret}");

            var handle = new MvCameraHandle();
            typeof(SafeHandle)
                .GetMethod("SetHandle", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                ?.Invoke(handle, new object[] { rawHandle });

            ret = MvCameraNative.MV_CC_OpenDevice(handle.DangerousGetHandle(), MvConstants.MV_ACCESS_Exclusive, 0);
            if (ret != MvConstants.MV_OK)
                throw new InvalidOperationException($"MV_CC_OpenDevice failed: {ret}");

            ret = MvCameraNative.MV_CC_StartGrabbing(handle.DangerousGetHandle());
            if (ret != MvConstants.MV_OK)
                throw new InvalidOperationException($"MV_CC_StartGrabbing failed: {ret}");

            return new HikCamera(handle, width, height, pixelType);
        }

        // ============================
        // Get Frame
        // ============================
        public bool TryGetFrame(out HikFrame frame)
        {
            frame = default;

            if (!IsOpened || _disposed)
                return false;

            var info = new MV_FRAME_OUT_INFO_EX
            {
                Reserved = new uint[16]
            };

            unsafe
            {
                fixed (byte* p = _buffer)
                {
                    var ret = MvCameraNative.MV_CC_GetOneFrameTimeout(
                        _handle.DangerousGetHandle(),
                        (IntPtr)p,
                        (uint)_buffer.Length,
                        ref info,
                        MvConstants.DefaultTimeoutMs);

                    if (ret != MvConstants.MV_OK)
                        return false;

                    frame = new HikFrame(_buffer, (int)info.nWidth, (int)info.nHeight, info.enPixelType);
                    return true;
                }
            }
        }

        // ============================
        // Dispose
        // ============================
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (IsOpened)
                {
                    MvCameraNative.MV_CC_StopGrabbing(_handle.DangerousGetHandle());
                    MvCameraNative.MV_CC_CloseDevice(_handle.DangerousGetHandle());
                }
            }
            catch { }

            _handle.Dispose();
            ArrayPool<byte>.Shared.Return(_buffer, clearArray: false);
            IsOpened = false;
        }
    }

    // ============================
    // Frame Container
    // ============================
    public readonly struct HikFrame
    {
        public byte[] Buffer { get; }
        public int Width { get; }
        public int Height { get; }
        public uint PixelType { get; }

        public HikFrame(byte[] buffer, int width, int height, uint pixelType)
        {
            Buffer = buffer;
            Width = width;
            Height = height;
            PixelType = pixelType;
        }
    }
}