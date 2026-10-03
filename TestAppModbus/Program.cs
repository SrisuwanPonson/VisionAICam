using System;
using System.Net.Sockets;
using System.Threading.Tasks;
using Modbus.Device;

class Program
{
    static async Task<int> Main(string[] args)
    {
        if (args.Length < 1)
        {
            Console.WriteLine("Usage: ModbusTest <host> [port=502] [slaveId=1] [start=1000] [count=10]");
            return 1;
        }

        string host = args[0];
        int port = args.Length > 1 ? int.Parse(args[1]) : 502;
        byte slaveId = args.Length > 2 ? byte.Parse(args[2]) : (byte)1;
        ushort start = args.Length > 3 ? ushort.Parse(args[3]) : (ushort)1000;
        ushort count = args.Length > 4 ? ushort.Parse(args[4]) : (ushort)10;

        try
        {
            using var tcp = new TcpClient();
            Console.WriteLine($"Connecting to {host}:{port}...");
            await tcp.ConnectAsync(host, port);
            using var master = ModbusIpMaster.CreateIp(tcp);
            master.Transport.ReadTimeout = 3000;

            ushort[] regs = null;
            try
            {
                // try typical signature
                regs = master.ReadHoldingRegisters(start, count);
            }
            catch
            {
                // fallback to explicit unit id overload if available
                regs = master.ReadHoldingRegisters(slaveId, start, count);
            }

            Console.WriteLine($"Read {regs.Length} registers starting at {start}:");
            for (int i = 0; i < regs.Length; i++)
            {
                Console.WriteLine($"{start + i}: {regs[i]} (0x{regs[i]:X4})");
            }

            // Optional: write a single register (uncomment to test write)
            //Console.WriteLine($"Writing register {start} = 1");
            //master.WriteSingleRegister(slaveId, start, 1);

            tcp.Close();
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error: {ex.Message}");
            Console.WriteLine(ex);
            return 2;
        }
    }
}