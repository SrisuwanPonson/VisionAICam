using System;

namespace VisionAICam.Modbus
{
    /// <summary>
    /// Script-address-based Modbus register map (grouped by function).
    /// Values are the script addresses from the robot documentation.
    /// Note: F32 entries occupy two consecutive 16-bit registers starting at the listed address.
    /// </summary>
    public static class RegisterMap
    {
        public enum Coil : ushort
        {
            // Control bits (Get/SetCoils) - script addresses
            Start = 0,                 // PLC 00001
            Pause = 1,                 // PLC 00002
            Continue = 2,              // PLC 00003
            Stop = 3,                  // PLC 00004
            EmergencyStop = 4,         // PLC 00005
            ClearAlarm = 5,            // PLC 00006
            Reset = 6,                 // PLC 00007

            // Base DO (DO1..DO16)
            BaseIo0 = 50,              // DO1
            BaseIo1 = 51,
            BaseIo2 = 52,
            BaseIo3 = 53,
            BaseIo4 = 54,
            BaseIo5 = 55,
            BaseIo6 = 56,
            BaseIo7 = 57,
            BaseIo8 = 58,
            BaseIo9 = 59,
            BaseIo10 = 60,
            BaseIo11 = 61,
            BaseIo12 = 62,
            BaseIo13 = 63,
            BaseIo14 = 64,
            BaseIo15 = 65,             // DO16

            // Tool DO (DO17..DO20)
            ToolIo0 = 66,              // DO17
            ToolIo1 = 67,
            ToolIo2 = 68,
            ToolIo3 = 69,              // DO20

            // User-defined coil range (script addresses)
            UserDefinedStart = 3095,
            UserDefinedEnd = 4095
        }

        public enum Contact : ushort
        {
            // Status bits (GetInBits) - script addresses
            StopStatus = 1,            // PLC 10002
            PauseStatus = 2,           // PLC 10003
            RunningStatus = 3,         // PLC 10004
            AlarmStatus = 4,           // PLC 10005
            CollisionStatus = 5,       // PLC 10006
            ManualAutoMode = 6,        // PLC 10007

            // Base DI (DI1..DI16)
            BaseDi0 = 50,
            BaseDi1 = 51,
            BaseDi2 = 52,
            BaseDi3 = 53,
            BaseDi4 = 54,
            BaseDi5 = 55,
            BaseDi6 = 56,
            BaseDi7 = 57,
            BaseDi8 = 58,
            BaseDi9 = 59,
            BaseDi10 = 60,
            BaseDi11 = 61,
            BaseDi12 = 62,
            BaseDi13 = 63,
            BaseDi14 = 64,
            BaseDi15 = 65,             // DI16

            // Tool DI (DI17..DI20)
            ToolDi0 = 66,
            ToolDi1 = 67,
            ToolDi2 = 68,
            ToolDi3 = 69
        }

        public enum Input : ushort
        {
            // Read-only input registers (GetInRegs) - F32 values (each uses two registers)
            Joint1 = 202,              // PLC 30203 script 202 (F32)
            Joint2 = 204,              // PLC 30205 script 204 (F32)
            Joint3 = 206,              // PLC 30207 script 206 (F32)
            Joint4 = 208,              // PLC 30209 script 208 (F32)
            Joint5 = 210,              // PLC 30211 script 210 (F32)
            Joint6 = 212,              // PLC 30213 script 212 (F32)

            CartX = 242,               // PLC 30243 script 242 (F32)
            CartY = 244,               // PLC 30245 script 244 (F32)
            CartZ = 246,               // PLC 30247 script 246 (F32)
            CartA = 248,               // PLC 30249 script 248 (F32)
            CartB = 250,               // PLC 30251 script 250 (F32)
            CartC = 252                // PLC 30253 script 252 (F32)
        }

        public enum Holding : ushort
        {
            // Interaction registers (Get/SetHoldRegs) - script addresses
            // HMI / Jog controls
            HmiSwitchToJogMode = 1300,     // PLC 41301
            HmiReadyToSwitch = 1301,
            CartesianOrJoint = 1302,
            JogStepSelection = 1303,
            GlobalSpeedPercent = 1304,
            StepDistance_mm = 1305,        // F32 at 1305..1306
            StepAngle_deg = 1307,          // F32 at 1307..1308

            ToolCoordinateIndex = 1309,
            UserCoordinateIndex = 1310,
            HandCoordinate = 1311,
            NotificationModifyParams = 1312,
            StartJog = 1313,

            // Jog control pulses (per-joint/cartesian)
            Jog_J1_Plus = 1314,
            Jog_J1_Minus = 1315,
            Jog_J2_Plus = 1316,
            Jog_J2_Minus = 1317,
            Jog_J3_Plus = 1318,
            Jog_J3_Minus = 1319,
            Jog_J4_Plus = 1320,
            Jog_J4_Minus = 1321,
            Jog_J5_Plus = 1322,
            Jog_J5_Minus = 1323,
            Jog_J6_Plus = 1324,
            Jog_J6_Minus = 1325,

            // Point P1..P16 example (each P* X/Y/Z/R/... — F32 entries)
            P1_X = 1326,                   // F32 1326..1327
            P1_Y = 1328,                   // F32 1328..1329
            P1_Z = 1330,                   // F32 1330..1331
            P1_R = 1332,                   // F32 1332..1333
            P1_B = 1334,                   // F32 1334..1335
            P1_C = 1336,                   // F32 1336..1337

            // Run/save and controls
            P1_ARM = 1338,
            P1_User = 1339,
            P1_Tool = 1340,

            // Extended points and point ranges are in 1341..1550 (P2..P15), P16 begins at 1551 etc.
            PointsRangeStart = 1341,
            PointsRangeEnd = 1550,

            // Common motion target registers used in examples/app (controller-specific)
            // These addresses are commonly used by some robot controllers for target write/execute.
            MotionTarget_StartX = 3001,    // typically X (F32 at 3001..3002), Y/Z/R following
            MotionExecutePulse = 3009,     // pulse register to execute PTP

            // Multi-PC blocks (examples)
            MultiPC1_X = 2009,             // F32 at script 2009..
            MultiPC1_Y = 2011,
            MultiPC1_R = 2013,

            // User-defined holding register range
            UserDefinedStart = 3095,
            UserDefinedEnd = 4095,

            // Global speed setting (some controllers expose an alternate address)
            GlobalSpeedAlternate = 4096
        }
    }
}