using System;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Modbus.Device; // NModbus4
using VisionAICam.Modbus;

namespace VisionAICam.Services
{
    /// <summary>
    /// Modbus TCP wrapper for MG400 control.
    /// Implements Modbus helpers and float register helpers used by higher-level motion helpers.
    /// </summary>

    public enum PulseEdge
    {
        Rising,   // 0 → 1 → 0
        Falling   // 1 → 0 → 1
    }
    public class RobotService : IDisposable
    {
        private TcpClient? _tcp;
        private ModbusIpMaster? _master;

        // serialize access to the master because NModbus masters are not thread-safe
        private readonly SemaphoreSlim _lock = new(1, 1);

        /// <summary>
        /// When true, the two 16-bit words that make a float are swapped (low-word first).
        /// Default: false (high-word first).
        /// </summary>
        public bool SwapFloatWords { get; set; }

        /// <summary>
        /// When true, each 16-bit word's bytes are swapped (endianness inside each word).
        /// Default: false.
        /// </summary>
        public bool SwapBytesInWord { get; set; }

        public event Action<bool>? ConnectionChanged;

        public bool IsConnected => _tcp?.Connected == true && _master != null;

        public async Task<bool> ConnectTcpAsync(string host, int port = 502, int timeoutMs = 2000)
        {
            Disconnect();
            try
            {
                _tcp = new TcpClient();
                var connectTask = _tcp.ConnectAsync(host, port);
                var completed = await Task.WhenAny(connectTask, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != connectTask || !_tcp.Connected)
                {
                    Disconnect();
                    ConnectionChanged?.Invoke(false);
                    return false;
                }

                _tcp.ReceiveTimeout = timeoutMs;
                _tcp.SendTimeout = timeoutMs;
                _master = ModbusIpMaster.CreateIp(_tcp);
                _master.Transport.Retries = 0;
                ConnectionChanged?.Invoke(true);
                return true;
            }
            catch
            {
                Disconnect();
                ConnectionChanged?.Invoke(false);
                return false;
            }
        }

        public void Disconnect()
        {
            try { _master?.Dispose(); } catch { }
            _master = null;
            try { _tcp?.Close(); } catch { }
            _tcp = null;
            ConnectionChanged?.Invoke(false);
        }

        // Basic Modbus helpers (thread-safe wrappers around synchronous NModbus calls)

        public async Task<ushort[]> ReadHoldingRegistersAsync(byte slaveId, ushort startAddress, ushort count)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => _master.ReadHoldingRegisters(slaveId, startAddress, count)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        // read input registers (function code 4)
        public async Task<ushort[]> ReadInputRegistersAsync(byte slaveId, ushort startAddress, ushort count)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => _master.ReadInputRegisters(slaveId, startAddress, count)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task WriteSingleRegisterAsync(byte slaveId, ushort address, ushort value)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => _master.WriteSingleRegister(slaveId, address, value)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task WriteMultipleRegistersAsync(byte slaveId, ushort startAddress, ushort[] values)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => _master.WriteMultipleRegisters(slaveId, startAddress, values)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task WriteSingleCoilAsync(byte slaveId, ushort coilAddress, bool value)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                await Task.Run(() => _master.WriteSingleCoil(slaveId, coilAddress, value)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<bool[]> ReadCoilsAsync(byte slaveId, ushort startAddress, ushort count)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            await _lock.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => _master.ReadCoils(slaveId, startAddress, count)).ConfigureAwait(false);
            }
            finally
            {
                _lock.Release();
            }
        }
        public async Task<ushort> ReadSingleRegisterAsync(byte slaveId, ushort registerAddress)
        {
            // Read exactly 1 holding register
            ushort[] regs = await ReadHoldingRegistersAsync(slaveId, registerAddress, 1)
                                    .ConfigureAwait(false);

            return regs[0];
        }


        // Read floats from holding registers (each float stored as two consecutive registers)
        public async Task<float[]> ReadFloatHoldingRegistersAsync(byte slaveId, ushort startAddress, ushort floatCount)
        {
            ushort regCount = (ushort)(floatCount * 2);
            var regs = await ReadHoldingRegistersAsync(slaveId, startAddress, regCount).ConfigureAwait(false);
            return ConvertRegsToFloats(regs, floatCount);
        }

        // Read floats from input registers (each float stored as two consecutive registers)
        public async Task<float[]> ReadFloatInputRegistersAsync(byte slaveId, ushort startAddress, ushort floatCount)
        {
            ushort regCount = (ushort)(floatCount * 2);
            var regs = await ReadInputRegistersAsync(slaveId, startAddress, regCount).ConfigureAwait(false);
            return ConvertRegsToFloats(regs, floatCount);
        }

        // Lower-level helper using F32Converter so we handle swapBytes and swapWords consistently.
        private float[] ConvertRegsToFloats(ushort[] regs, int floatCount)
        {
            if (regs == null) throw new ArgumentNullException(nameof(regs));
            if (regs.Length < floatCount * 2) throw new ArgumentException("Not enough registers for requested floats.");

            var result = new float[floatCount];

            for (int i = 0; i < floatCount; i++)
            {
                ushort high = regs[i * 2];
                ushort low = regs[i * 2 + 1];

                // FIX: MG400 needs word swap because your library reverses them
                result[i] = F32Converter.FromRegisters(
                    high,
                    low,
                    swapWords: true,
                    swapBytes: false
                );
            }

            return result;
        }

        // Write floats (each float becomes two registers) using F32Converter to respect swap flags.
        public async Task WriteFloatRegistersAsync(byte slaveId, ushort startAddress, float[] values)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            if (values == null) throw new ArgumentNullException(nameof(values));

            var regs = new ushort[values.Length * 2];
            for (int i = 0; i < values.Length; i++)
            {
                var pair = F32Converter.ToRegisters(values[i], SwapFloatWords, SwapBytesInWord);
                regs[i * 2] = pair[0];
                regs[i * 2 + 1] = pair[1];
            }

            await WriteMultipleRegistersAsync(slaveId, startAddress, regs).ConfigureAwait(false);
        }

        // Helper: write a register as a short pulse (1 then 0)


        public async Task WriteRegisterPulseAsync(
      byte slaveId,
      ushort registerAddress,
      PulseEdge edge = PulseEdge.Rising,
      int pulseMs = 40)
        {
            // Read actual current value
            ushort current = await ReadSingleRegisterAsync(slaveId, registerAddress)
                                    .ConfigureAwait(false);

            // Perform the pulse once
            await GeneratePulse(slaveId, registerAddress, current, edge, pulseMs);

            // Read again to verify
            ushort after = await ReadSingleRegisterAsync(slaveId, registerAddress)
                                    .ConfigureAwait(false);

            // If the register did not return to expected state, retry once
            ushort expectedEnd = (edge == PulseEdge.Rising) ? (ushort)0 : (ushort)1;

            if (after != expectedEnd)
            {
                // Retry pulse
                await GeneratePulse(slaveId, registerAddress, after, edge, pulseMs);
            }
        }

        private async Task GeneratePulse(
            byte slaveId,
            ushort registerAddress,
            ushort current,
            PulseEdge edge,
            int pulseMs)
        {
            if (edge == PulseEdge.Rising)
            {
                // Rising edge: 0 → 1 → 0
                if (current != 0)
                    await WriteSingleRegisterAsync(slaveId, registerAddress, 0).ConfigureAwait(false);

                await WriteSingleRegisterAsync(slaveId, registerAddress, 1).ConfigureAwait(false);
                await Task.Delay(pulseMs).ConfigureAwait(false);
                await WriteSingleRegisterAsync(slaveId, registerAddress, 0).ConfigureAwait(false);
            }
            else
            {
                // Falling edge: 1 → 0 → 1
                if (current != 1)
                    await WriteSingleRegisterAsync(slaveId, registerAddress, 1).ConfigureAwait(false);

                await WriteSingleRegisterAsync(slaveId, registerAddress, 0).ConfigureAwait(false);
                await Task.Delay(pulseMs).ConfigureAwait(false);
                await WriteSingleRegisterAsync(slaveId, registerAddress, 1).ConfigureAwait(false);
            }
        }



        // Convenience higher-level helpers that use the RegisterMap enums

        public Task<float[]> ReadCartesianAsync(byte slaveId)
        {
            // Input registers store cartesian X,Y,Z,A starting at RegisterMap.Input.CartX
            return ReadFloatInputRegistersAsync(slaveId, (ushort)RegisterMap.Input.CartX, 4);
        }

        public Task<float[]> ReadJointsAsync(byte slaveId, int jointCount = 4)
        {
            // Joint input registers start at RegisterMap.Input.Joint1 (F32 per joint)
            return ReadFloatInputRegistersAsync(slaveId, (ushort)RegisterMap.Input.Joint1, (ushort)jointCount);
        }

        public Task WriteMotionTargetAsync(byte slaveId, float x, float y, float z, float rz)
        {
            return WriteFloatRegistersAsync(slaveId, (ushort)RegisterMap.Holding.MotionTarget_StartX, new[] { x, y, z, rz });
        }

        public Task ExecuteMotionPulseAsync(byte slaveId)
        {
            return WriteRegisterPulseAsync(slaveId, (ushort)RegisterMap.Holding.MotionExecutePulse);
        }

        public async Task OffsetMoveAsync(
            float x,
            float y,
            float z,
            float rz,
            float offsetX = 0f,
            float offsetY = 0f,
            float offsetZ = 0f,
            bool execute = false,
            CancellationToken ct = default,
            byte slaveId = 1)
        {
            if (_master == null) throw new InvalidOperationException("Not connected");
            ct.ThrowIfCancellationRequested();

            // Compute offset target
            float tx = x + offsetX;
            float ty = y + offsetY;
            float tz = z + offsetZ;

            // Write target registers (uses RegisterMap.Holding.MotionTarget_StartX)
            await WriteMotionTargetAsync(slaveId, tx, ty, tz, rz).ConfigureAwait(false);

            if (execute)
            {
                // short delay to let controller sample registers before execute pulse
                await Task.Delay(50, ct).ConfigureAwait(false);
                await ExecuteMotionPulseAsync(slaveId).ConfigureAwait(false);
            }
        }

        public void Dispose()
        {
            Disconnect();
            _lock.Dispose();
        }
    }
}