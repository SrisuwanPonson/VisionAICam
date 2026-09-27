using System;
using System.Net.Sockets;
using Modbus.Device;
using VisionAICam;
using VisionAICam.Modbus;

namespace TestModbusClient
{
    internal static class Program
    {
        private static bool _cancelRequested = false;

        static int Main(string[] args)
        {
            Console.CancelKeyPress += (s, e) =>
            {
                Console.WriteLine("Cancel requested... stopping polling.");
                _cancelRequested = true;
                e.Cancel = true; // prevent immediate process kill
            };

            string host = args.Length > 0 ? args[0] : "127.0.0.1";
            int port = args.Length > 1 && int.TryParse(args[1], out var p) ? p : 502;

            Console.WriteLine($"Connecting to Modbus server {host}:{port}");

            try
            {
                var appSettings = SettingsManager.Load();
                bool swapWords = appSettings?.SwapFloatWords ?? false;

                using var tcp = new TcpClient();
                tcp.Connect(host, port);
                using var master = ModbusIpMaster.CreateIp(tcp);

                byte slaveId = 1;

                int maxObjects = Math.Min(appSettings?.ModbusMaxObjects ?? 100, 200);
                int perObjectRegs = 6;
                int regsToPoll = 1 + maxObjects * perObjectRegs;

                Console.WriteLine($"Polling {regsToPoll} registers starting at 1000 every 1s.");
                Console.WriteLine("Press Ctrl+C to stop.\n");

                ushort[] prev = null;

                while (!_cancelRequested)
                {
                    try
                    {
                        // Simple sequential read: read each register one-by-one and loop continuously
                        ushort[] all = new ushort[regsToPoll];
                        try
                        {
                            for (int off = 0; off < regsToPoll; off++)
                            {
                                try
                                {
                                    ushort addr = (ushort)(1000 + off);
                                    ushort[] r = master.ReadHoldingRegisters(slaveId, addr, 1);
                                    all[off] = (r != null && r.Length > 0) ? r[0] : (ushort)0;
                                }
                                catch (Exception exReg)
                                {
                                    Console.WriteLine($"Poll read failed at register {1000 + off}: {exReg.Message}");
                                    all[off] = 0;
                                }
                            }
                        }
                        catch (Exception exAll)
                        {
                            Console.WriteLine($"Poll read failed: {exAll.Message}");
                            all = null;
                        }

                        if (all == null || all.Length == 0)
                        {
                            Console.WriteLine("Read returned null or empty");
                        }
                        else
                        {
                            if (prev == null)
                            {
                                Console.WriteLine("Initial snapshot:");
                                for (int i = 0; i < Math.Min(all.Length, 64); i += 8)
                                {
                                    Console.Write($"{1000 + i,6}: ");
                                    for (int j = 0; j < 8 && i + j < all.Length; j++)
                                        Console.Write($"0x{all[i + j]:X4} ");
                                    Console.WriteLine();
                                }
                            }
                            else
                            {
                                for (int i = 0; i < all.Length && i < prev.Length; i++)
                                {
                                    if (all[i] != prev[i])
                                    {
                                        Console.WriteLine($"Reg {1000 + i}: 0x{prev[i]:X4} -> 0x{all[i]:X4}");
                                    }
                                }
                            }

                            prev = all;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Poll read failed: {ex.Message}");
                    }

                    System.Threading.Thread.Sleep(1000);
                }

                Console.WriteLine("Polling stopped.");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error: {ex}");
                return 2;
            }
        }
    }
}
