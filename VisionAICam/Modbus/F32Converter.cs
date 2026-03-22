using System;

namespace VisionAICam.Modbus
{
    /// <summary>
    /// Helpers to convert IEEE-754 F32 stored across two Modbus 16-bit registers.
    /// Supports word-swap (low/high) and optional byte-swap inside each 16-bit word.
    /// </summary>
    public static class F32Converter
    {
        // Convert two 16-bit registers to a float.
        // Parameters:
        //  - high: first register as returned by Modbus for the high word (or low word if swapWords=true)
        //  - low: second register as returned by Modbus
        //  - swapWords: true if device stores low-word first
        //  - swapBytes: true if each 16-bit word needs byte reversal (endianness inside word)
        public static float FromRegisters(ushort high, ushort low, bool swapWords = false, bool swapBytes = false)
        {
            if (swapBytes)
            {
                high = SwapBytes(high);
                low = SwapBytes(low);
            }

            uint highWord = swapWords ? low : high;
            uint lowWord = swapWords ? high : low;

            uint bits = (highWord << 16) | lowWord;
            return BitConverter.Int32BitsToSingle((int)bits);
        }

        // Convert from a ushort register array at given index (index points to the first of two registers).
        public static float FromRegisters(ushort[] regs, int index, bool swapWords = false, bool swapBytes = false)
        {
            if (regs == null) throw new ArgumentNullException(nameof(regs));
            if (index < 0 || index + 1 >= regs.Length) throw new ArgumentOutOfRangeException(nameof(index));
            return FromRegisters(regs[index], regs[index + 1], swapWords, swapBytes);
        }

        // Pack a float into two 16-bit registers with the requested ordering.
        public static ushort[] ToRegisters(float value, bool swapWords = true, bool swapBytes = false)
        {
            int bits = BitConverter.SingleToInt32Bits(value);

            ushort high = (ushort)((bits >> 16) & 0xFFFF);
            ushort low = (ushort)(bits & 0xFFFF);

            // MG400 requires LOW first, HIGH second
            // Your read function expects swapWords=true, so we keep that consistent
            if (swapWords)
            {
                var tmp = high;
                high = low;
                low = tmp;
            }

            if (swapBytes)
            {
                high = SwapBytes(high);
                low = SwapBytes(low);
            }

            return new[] { high, low };
        }

        private static ushort SwapBytes(ushort value)
        {
            return (ushort)((value << 8) | (value >> 8));
        }

        // Quick heuristic to test which ordering yields a plausible float for a known register pair.
        // Returns 0 = indeterminate, 1 = (no swapWords) likely, 2 = (swapWords) likely.
        // Use against a register pair for a known human-readable value (e.g. 1.0f, 0.0f).
        public static int GuessWordOrder(ushort high, ushort low, bool swapBytes = false)
        {
            float v1 = FromRegisters(high, low, swapWords: false, swapBytes: swapBytes);
            float v2 = FromRegisters(high, low, swapWords: true, swapBytes: swapBytes);

            bool v1Ok = IsReasonable(v1);
            bool v2Ok = IsReasonable(v2);

            if (v1Ok && !v2Ok) return 1;
            if (!v1Ok && v2Ok) return 2;
            return 0;
        }

        private static bool IsReasonable(float v)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return false;
            // adjust thresholds to your expected robot ranges
            return Math.Abs(v) < 1e6f;
        }
    }
}