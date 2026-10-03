using System;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using Modbus.Device;

namespace TestAppModbus
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private async void ReadButton_Click(object sender, RoutedEventArgs e)
        {
            ResultBox.Text = string.Empty;
            StatusText.Text = "Reading...";
            try
            {
                string host = HostText.Text.Trim();
                int port = int.TryParse(PortText.Text.Trim(), out var p) ? p : 502;
                byte slave = byte.TryParse(SlaveText.Text.Trim(), out var s) ? s : (byte)1;
                ushort start = ushort.TryParse(StartText.Text.Trim(), out var st) ? st : (ushort)1000;
                // adjust 1-based addressing if requested
                try
                {
                    if (OneBasedCheck.IsChecked == true)
                    {
                        if (start > 0) start = (ushort)(start - 1);
                    }
                }
                catch { }
                ushort count = ushort.TryParse(CountText.Text.Trim(), out var c) ? c : (ushort)10;

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                using var master = ModbusIpMaster.CreateIp(tcp);
                master.Transport.ReadTimeout = 3000;

                ushort[] regs = null;
                try
                {
                    regs = master.ReadHoldingRegisters(start, count);
                }
                catch
                {
                    regs = master.ReadHoldingRegisters(slave, start, count);
                }

                var sb = new StringBuilder();
                // display addresses in 1-based form if requested
                bool oneBased = OneBasedCheck.IsChecked == true;
                sb.AppendLine($"Read {regs.Length} registers starting at {(oneBased ? start + 1 : start)}:");
                for (int i = 0; i < regs.Length; i++) sb.AppendLine($"{(oneBased ? start + 1 + i : start + i)}: {regs[i]} (0x{regs[i]:X4})");
                ResultBox.Text = sb.ToString();
                StatusText.Text = "Read OK";

                tcp.Close();
            }
            catch (Exception ex)
            {
                ResultBox.Text = ex.ToString();
                StatusText.Text = "Error";
            }
        }

        private async void WriteButton_Click(object sender, RoutedEventArgs e)
        {
            ResultBox.Text = string.Empty;
            StatusText.Text = "Writing...";
            try
            {
                string host = HostText.Text.Trim();
                int port = int.TryParse(PortText.Text.Trim(), out var p) ? p : 502;
                byte slave = byte.TryParse(SlaveText.Text.Trim(), out var s) ? s : (byte)1;
                ushort start = ushort.TryParse(StartText.Text.Trim(), out var st) ? st : (ushort)1000;
                // adjust 1-based addressing if requested
                try
                {
                    if (OneBasedCheck.IsChecked == true)
                    {
                        if (start > 0) start = (ushort)(start - 1);
                    }
                }
                catch { }

                using var tcp = new TcpClient();
                await tcp.ConnectAsync(host, port);
                using var master = ModbusIpMaster.CreateIp(tcp);
                master.Transport.ReadTimeout = 3000;

                // write 1 to start
                master.WriteSingleRegister(slave, start, 1);

                ResultBox.Text = $"Wrote register {start} = 1";
                StatusText.Text = "Write OK";
                tcp.Close();
            }
            catch (Exception ex)
            {
                ResultBox.Text = ex.ToString();
                StatusText.Text = "Error";
            }
        }
    }
}
