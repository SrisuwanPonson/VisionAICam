using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using VisionAICam.Services;
using VisionAICam.Modbus;

namespace VisionAICam.Pages
{
    public partial class RobotPage : Page
    {
        private RobotService? _robot;
        private CancellationTokenSource? _pollCts;
        private float[] _cartValues = new float[4]; // X,Y,Z,R
        private float[] _jointValues = new float[4]; // J1..J4 (best-effort read)

        // Use Input register map for joint reads; writing back is controller-specific and still user-adjustable.
        private static readonly ushort JointInputStart = (ushort)RegisterMap.Input.Joint1;
        // If your controller accepts joint targets at a holding-register block, adjust this value.
        private const ushort JointWriteStartDefault = 2200;

        private const byte SlaveId = 1;

        public RobotPage()
        {
            InitializeComponent();
            Loaded += RobotPage_Loaded;
            Unloaded += RobotPage_Unloaded;
        }

        private void RobotPage_Loaded(object? sender, RoutedEventArgs e)
        {
            _robot = new RobotService();
            _robot.ConnectionChanged += Robot_ConnectionChanged;
        }

        private void RobotPage_Unloaded(object? sender, RoutedEventArgs e)
        {
            StopPolling();
            if (_robot != null)
            {
                _robot.ConnectionChanged -= Robot_ConnectionChanged;
                _robot.Dispose();
                _robot = null;
            }
        }

        private void Robot_ConnectionChanged(bool connected)
        {
            // Marshal to UI thread (RobotService may invoke from thread-pool)
            Dispatcher.BeginInvoke(() =>
            {
                StatusTextBlock.Text = connected ? "Connected" : "Disconnected";
                ConnectButton.Content = connected ? "Disconnect" : "Connect";
                ConnectButton.IsEnabled = true;

                if (connected)
                {
                    StartPolling();
                }
                else
                {
                    StopPolling();
                    ResetDisplays();
                }
            });
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            if (_robot == null) return;

            try
            {
                ConnectButton.IsEnabled = false;

                if (_robot.IsConnected)
                {
                    _robot.Disconnect();
                    return;
                }

                string host = HostTextBox.Text?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(host))
                {
                    MessageBox.Show("Please enter a host.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ConnectButton.IsEnabled = true;
                    return;
                }

                if (!int.TryParse(PortTextBox.Text, out int port))
                {
                    MessageBox.Show("Port must be a number.", "Validation", MessageBoxButton.OK, MessageBoxImage.Warning);
                    ConnectButton.IsEnabled = true;
                    return;
                }

                _robot.SwapFloatWords = SwapWordsCheckBox.IsChecked == true;

                bool ok = await _robot.ConnectTcpAsync(host, port).ConfigureAwait(false);

                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (!ok)
                    {
                        StatusTextBlock.Text = "Disconnected";
                        ConnectButton.IsEnabled = true;
                        MessageBox.Show("Failed to connect to robot.", "Connection", MessageBoxButton.OK, MessageBoxImage.Error);
                    }
                }));
            }
            catch (Exception ex)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    StatusTextBlock.Text = "Error";
                    ConnectButton.IsEnabled = true;
                    MessageBox.Show($"Connection error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }));
            }
        }

        private void StepToggle_Click(object sender, RoutedEventArgs e)
        {
            // ensure single selection: uncheck others
            if (sender != Step0_1Button) Step0_1Button.IsChecked = false;
            if (sender != Step1Button) Step1Button.IsChecked = false;
            if (sender != Step5Button) Step5Button.IsChecked = false;
            if (sender != Step10Button) Step10Button.IsChecked = false;
            ((ToggleButton)sender).IsChecked = true;
        }

        private float GetSelectedStep()
        {
            // Make thread-safe: if called from a background thread, marshal the read to the UI thread.
            if (!Dispatcher.CheckAccess())
            {
                return (float)Dispatcher.Invoke(new Func<float>(GetSelectedStep));
            }

            if (Step0_1Button.IsChecked == true) return 0.1f;
            if (Step1Button.IsChecked == true) return 1f;
            if (Step5Button.IsChecked == true) return 5f;
            if (Step10Button.IsChecked == true) return 10f;
            return 1f;
        }
        private void StartPolling()
        {
            StopPolling();
            _pollCts = new CancellationTokenSource();
            _ = PollLoopAsync(_pollCts.Token);
        }

        private void StopPolling()
        {
            try
            {
                _pollCts?.Cancel();
                _pollCts?.Dispose();
                _pollCts = null;
            }
            catch { }
        }

        private void ResetDisplays()
        {
            XValRun.Text = "--";
            YValRun.Text = "--";
            ZValRun.Text = "--";
            RValRun.Text = "--";
            J1ValRun.Text = "--";
            J2ValRun.Text = "--";
            J3ValRun.Text = "--";
            J4ValRun.Text = "--";
        }

        private async Task PollLoopAsync(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    if (_robot != null && _robot.IsConnected)
                    {
                        try
                        {
                            // Read cartesian from input registers (CartX..CartR)
                            var cart = await _robot.ReadFloatInputRegistersAsync(SlaveId, (ushort)RegisterMap.Input.CartX, 4).ConfigureAwait(false);
                            if (cart != null && cart.Length >= 4)
                            {
                                _cartValues = cart.ToArray();
                            }
                        }
                        catch { /* ignore read errors */ }

                        try
                        {
                            // Read joint angles from input registers (Joint1..Joint4)
                            var joints = await _robot.ReadFloatInputRegistersAsync(SlaveId, JointInputStart, 4).ConfigureAwait(false);
                            if (joints != null && joints.Length >= 4)
                            {
                                _jointValues = joints.ToArray();
                            }
                        }
                        catch { /* ignore joint read errors; mapping may differ */ }

                        // update UI
                        await Dispatcher.BeginInvoke(new Action(UpdateDisplays));
                    }
                }
                catch { /* swallow polling errors */ }

                try
                {
                    await Task.Delay(500, ct).ConfigureAwait(false);
                }
                catch (TaskCanceledException) { break; }
            }
        }

        private void UpdateDisplays()
        {
            XValRun.Text = _cartValues.Length > 0 ? _cartValues[0].ToString("F2") : "--";
            YValRun.Text = _cartValues.Length > 1 ? _cartValues[1].ToString("F2") : "--";
            ZValRun.Text = _cartValues.Length > 2 ? _cartValues[2].ToString("F2") : "--";
            RValRun.Text = _cartValues.Length > 3 ? _cartValues[3].ToString("F2") : "--";

            J1ValRun.Text = _jointValues.Length > 0 ? _jointValues[0].ToString("F2") : "--";
            J2ValRun.Text = _jointValues.Length > 1 ? _jointValues[1].ToString("F2") : "--";
            J3ValRun.Text = _jointValues.Length > 2 ? _jointValues[2].ToString("F2") : "--";
            J4ValRun.Text = _jointValues.Length > 3 ? _jointValues[3].ToString("F2") : "--";
        }

                // Cartesian direction handlers (offsets expressed in meters / radians as required)
                // Revised: when in "Jog" mode we send short pulses to the controller's jog pulse registers.
                // When in "Step" mode we compute a target and use the existing OffsetMoveAsync path.
                private async void XPlus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "X", positive: true);
                private async void XMinus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "X", positive: false);
                private async void YPlus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "Y", positive: true);
                private async void YMinus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "Y", positive: false);
                private async void ZPlus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "Z", positive: true);
                private async void ZMinus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "Z", positive: false);
                private async void RPlus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "R", positive: true);
                private async void RMinus_Click(object sender, RoutedEventArgs e) => await DoCartesianMoveAsync(axis: "R", positive: false);

                private async Task DoCartesianMoveAsync(string axis, bool positive)
                {
                    if (_robot == null || !_robot.IsConnected)
                    {
                        MessageBox.Show("Not connected to robot.", "Manual Move", MessageBoxButton.OK, MessageBoxImage.Warning);
                        return;
                    }

            // If Jog mode is selected, prepare HMI jog using function 06 for U16 registers and write
            // StepDistance_mm as two registers (written with two function 06 calls) before pulsing StartJog.
            if (JogModeRadio.IsChecked == true)
            {
                // 1) Select correct jog register (shared for joint/cartesian)
                ushort axisReg;
                switch (axis)
                {
                    case "X":
                        axisReg = positive
                            ? (ushort)RegisterMap.Holding.Jog_J1_Plus   // 1314 (X+ when 1302=1)
                            : (ushort)RegisterMap.Holding.Jog_J1_Minus; // 1315 (X-)
                        break;

                    case "Y":
                        axisReg = positive
                            ? (ushort)RegisterMap.Holding.Jog_J2_Plus   // 1316 (Y+)
                            : (ushort)RegisterMap.Holding.Jog_J2_Minus; // 1317 (Y-)
                        break;

                    case "Z":
                        axisReg = positive
                            ? (ushort)RegisterMap.Holding.Jog_J3_Plus   // 1318 (Z+)
                            : (ushort)RegisterMap.Holding.Jog_J3_Minus; // 1319 (Z-)
                        break;

                    case "R":
                        axisReg = positive
                            ? (ushort)RegisterMap.Holding.Jog_J4_Plus   // 1320 (R+)
                            : (ushort)RegisterMap.Holding.Jog_J4_Minus; // 1321 (R-)
                        break;

                    default:
                        return;
                }

                try
                {
                    // 2) MG400 jog mode setup
                    
                    //await _robot.WriteSingleRegisterAsync(SlaveId, (ushort)RegisterMap.Holding.HmiReadyToSwitch, 1);//set 1 to enable
                    await _robot.WriteSingleRegisterAsync(SlaveId, (ushort)RegisterMap.Holding.HmiSwitchToJogMode, 1);//set 1 to switch mode
                    await _robot.WriteSingleRegisterAsync(SlaveId, (ushort)RegisterMap.Holding.GlobalSpeedPercent, 50);
                    await _robot.WriteRegisterPulseAsync(SlaveId, (ushort)RegisterMap.Holding.JogOrStepMode, PulseEdge.Falling);

                    
                    //await _robot.WriteSingleRegisterAsync(SlaveId, (ushort)RegisterMap.Holding.JogStepSelection, stepMode);
                    

                    // 4) Start jog pulse → then axis pulse

                    await _robot.WriteRegisterPulseAsync(SlaveId, axisReg,PulseEdge.Rising,100);
                    while(true)
                    {
                        await Task.Delay(100);
                    }
                    

                   
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Jog failed: {ex.Message}", "Manual Move", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                return;
            }

            // Step mode: compute offset and call existing OffsetMoveAsync.
            float step = GetSelectedStep();
                    float dx = 0f, dy = 0f, dz = 0f, dr = 0f;
                    if (axis == "X") dx = positive ? step : -step;
                    if (axis == "Y") dy = positive ? step : -step;
                    if (axis == "Z") dz = positive ? step : -step;
                    if (axis == "R") dr = positive ? step : -step;

                    // Use the last-read current cartesian pose as the base.
                    float curX = _cartValues.Length > 0 ? _cartValues[0] : 0f;
                    float curY = _cartValues.Length > 1 ? _cartValues[1] : 0f;
                    float curZ = _cartValues.Length > 2 ? _cartValues[2] : 0f;
                    float curR = _cartValues.Length > 3 ? _cartValues[3] : 0f;

                    try
                    {
                        // Heuristic: if controller reports large numbers, assume mm units and scale step (UI steps are meters).
                        bool controllerIsMillimeters = Math.Abs(curX) > 50f || Math.Abs(curY) > 50f || Math.Abs(curZ) > 50f;
                        float scale = controllerIsMillimeters ? 1000f : 1f;

                        await _robot.OffsetMoveAsync(
                            curX, curY, curZ, curR,
                            offsetX: dx * scale,
                            offsetY: dy * scale,
                            offsetZ: dz * scale,
                            execute: true,
                            ct: CancellationToken.None,
                            slaveId: SlaveId
                        ).ConfigureAwait(false);

                        // Update local cache & UI to reflect requested target (immediate feedback).
                        _cartValues = new[] { curX + dx * scale, curY + dy * scale, curZ + dz * scale, curR + dr };
                        await Dispatcher.BeginInvoke(new Action(UpdateDisplays));
                    }
                    catch (Exception ex)
                    {
                        await Dispatcher.BeginInvoke(new Action(() =>
                        {
                            MessageBox.Show($"Failed to move robot: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
                        }));
                    }
                }

                // Joint direction handlers (best-effort: reads joint registers, writes offsets back).
                // Warning: controller register mapping may differ. Adjust JointWriteStartDefault if needed.
                private async void J1Plus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(0, GetSelectedStep());
                private async void J1Minus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(0, -GetSelectedStep());
                private async void J2Plus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(1, GetSelectedStep());
                private async void J2Minus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(1, -GetSelectedStep());
                private async void J3Plus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(2, GetSelectedStep());
                private async void J3Minus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(2, -GetSelectedStep());
                private async void J4Plus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(3, GetSelectedStep());
                private async void J4Minus_Click(object sender, RoutedEventArgs e) => await DoJointOffsetAsync(3, -GetSelectedStep());

        private async Task DoJointOffsetAsync(int jointIndex, float offset)
        {
            if (_robot == null || !_robot.IsConnected)
            {
                MessageBox.Show("Not connected to robot.", "Manual Move", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                // Read current joint angles from input registers (best-effort)
                float[] cur = await _robot.ReadFloatInputRegistersAsync(SlaveId, JointInputStart, 4).ConfigureAwait(false);
                if (cur == null || cur.Length < 4)
                {
                    await Dispatcher.BeginInvoke(new Action(() =>
                    {
                        MessageBox.Show("Unable to read joint positions. Adjust joint register mapping.", "Manual Move", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }));
                    return;
                }

                var next = cur.ToArray();
                next[jointIndex] = next[jointIndex] + offset;

                // Write new joint targets back (best-effort) then pulse execute.
                // NOTE: Many controllers do not accept writes to input registers; adjust JointWriteStartDefault to your controller's holding area for joint targets.
                await _robot.WriteFloatRegistersAsync(SlaveId, JointWriteStartDefault, next).ConfigureAwait(false);
                // Pulse execute register to trigger move (controller common MotionExecutePulse used elsewhere).
                await _robot.WriteRegisterPulseAsync(SlaveId, (ushort)RegisterMap.Holding.MotionExecutePulse).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await Dispatcher.BeginInvoke(new Action(() =>
                {
                    MessageBox.Show($"Joint move failed: {ex.Message}", "Manual Move", MessageBoxButton.OK, MessageBoxImage.Error);
                }));
            }
        }
    }
}