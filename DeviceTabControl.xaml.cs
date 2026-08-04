using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;

namespace SSHTester
{
    public partial class DeviceTabControl : UserControl
    {
        private TestEngine? _engine;
        private int _succCount = 0;
        private int _failCount = 0;
        private int _totalCount = 0;
        private double _totalSeconds = 0;
        private DeviceState? _dashboardState;

        public DeviceTabControl()
        {
            InitializeComponent();
            UpdatePieChart(0, 0);
            BtnStart.Click += BtnStart_Click;
            BtnStop.Click += BtnStop_Click;
            BtnClearLog.Click += (s, e) => TxtConsole.Clear();
            BtnSaveProfile.Click += BtnSaveProfile_Click;
            BtnLoadProfile.Click += BtnLoadProfile_Click;
            BtnExportLog.Click += BtnExportLog_Click;
            BtnBrowse.Click += BtnBrowse_Click;
            
            BtnRelayOn.Click += (s, e) => SendManualRelay(new byte[] { 0xFF });
            BtnRelayOff.Click += (s, e) => SendManualRelay(new byte[] { 0x00 });
            BtnRelayCycle.Click += BtnRelayCycle_Click;
        }

        public void InitializeDashboard(DeviceState state)
        {
            _dashboardState = state;
            TxtIpAddress.TextChanged += (s, e) => 
            {
                if (_dashboardState != null)
                    _dashboardState.IpAddress = string.IsNullOrWhiteSpace(TxtIpAddress.Text) ? "Unknown" : TxtIpAddress.Text;
            };
        }

        private void ToggleParams(object sender, RoutedEventArgs e)
        {
            if (PanelStress == null || PanelModem == null || PanelCustom == null) return;

            PanelStress.Visibility = Visibility.Collapsed;
            PanelModem.Visibility = Visibility.Collapsed;
            PanelCustom.Visibility = Visibility.Collapsed;
            PanelStressSize.Visibility = Visibility.Collapsed;

            if (RbSsdStress?.IsChecked == true)
            {
                PanelStress.Visibility = Visibility.Visible;
                PanelStressSize.Visibility = Visibility.Visible;
            }
            else if (RbSsd1Gb?.IsChecked == true || RbFsck?.IsChecked == true)
            {
                PanelStress.Visibility = Visibility.Visible;
            }
            else if (RbModem?.IsChecked == true)
            {
                PanelModem.Visibility = Visibility.Visible;
            }
            else if (RbCustom?.IsChecked == true)
            {
                PanelCustom.Visibility = Visibility.Visible;
            }
        }

        private void BtnBrowse_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*" };
            if (ofd.ShowDialog() == true) TxtCustomFile.Text = ofd.FileName;
        }

        private void BtnBrowseKey_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "Key files (*.pem;*.ppk;*.key)|*.pem;*.ppk;*.key|All files (*.*)|*.*" };
            if (ofd.ShowDialog() == true) TxtSshKey.Text = ofd.FileName;
        }

        private void ChkShowPass_Click(object sender, RoutedEventArgs e)
        {
            if (ChkShowPass.IsChecked == true)
            {
                TxtPasswordVisible.Text = TxtPassword.Password;
                TxtPassword.Visibility = Visibility.Collapsed;
                TxtPasswordVisible.Visibility = Visibility.Visible;
            }
            else
            {
                TxtPassword.Password = TxtPasswordVisible.Text;
                TxtPasswordVisible.Visibility = Visibility.Collapsed;
                TxtPassword.Visibility = Visibility.Visible;
            }
        }

        private void SetRelayIndicator(bool? isOn)
        {
            Dispatcher.Invoke(() => 
            {
                if (isOn == true)
                {
                    TxtRelayStatusTxt.Text = "RELAY: ON";
                    TxtRelayStatusTxt.Foreground = Brushes.MediumSeaGreen;
                }
                else if (isOn == false)
                {
                    TxtRelayStatusTxt.Text = "RELAY: OFF";
                    TxtRelayStatusTxt.Foreground = Brushes.Crimson;
                }
                else
                {
                    TxtRelayStatusTxt.Text = "RELAY: UNK";
                    TxtRelayStatusTxt.Foreground = Brushes.SlateGray;
                }
            });
        }

        private void SendManualRelay(byte[] payload)
        {
            try
            {
                int baud = int.TryParse(TxtRelayBaud.Text, out int rb) ? rb : 38400;
                RelayController.SendRelayCommand(TxtRelayPort.Text, baud, payload);
                SetRelayIndicator(payload[0] == 0xFF);
                LogMessage($"Manual Relay Command: {(payload[0] == 0xFF ? "ON" : "OFF")} on {TxtRelayPort.Text}");
            }
            catch (Exception ex)
            {
                LogMessage($"Relay Error: {ex.Message}");
            }
        }

        private async void BtnRelayCycle_Click(object sender, RoutedEventArgs e)
        {
            BtnRelayCycle.IsEnabled = false;
            int offMs = int.TryParse(TxtRelayOff.Text, out int roff) ? roff : 10000;
            
            await Task.Run(() => 
            {
                SendManualRelay(new byte[] { 0x00 });
                System.Threading.Thread.Sleep(offMs);
                SendManualRelay(new byte[] { 0xFF });
            });
            
            BtnRelayCycle.IsEnabled = true;
        }

        private async void BtnStart_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TxtIpAddress.Text)) return;

            TestConfig config = new TestConfig
            {
                Ip = TxtIpAddress.Text,
                User = TxtUser.Text,
                Password = ChkShowPass.IsChecked == true ? TxtPasswordVisible.Text : TxtPassword.Password,
                SshKey = TxtSshKey.Text,
                Cycles = int.TryParse(TxtCycles.Text, out int c) ? c : 0,
                SshTimeout = int.TryParse(TxtTimeout.Text, out int t) ? t : 60,
                WaitOnline = int.TryParse(TxtWaitOn.Text, out int wo) ? wo : 10,
                WaitOffline = int.TryParse(TxtWaitOff.Text, out int wf) ? wf : 3,
                PingCount = int.TryParse(TxtPingCnt.Text, out int pc) ? pc : 5,
                PingInterval = double.TryParse(TxtPingInt.Text, out double pi) ? pi : 1.0,
                RelayEnable = ChkRelayEnable.IsChecked ?? false,
                RelayAutoRecover = ChkRelayAutoRecover.IsChecked ?? false,
                RelayAutoRecoverSeconds = int.TryParse(TxtAutoRecoverSec.Text, out int ars) ? ars : 300,
                RelayPort = TxtRelayPort.Text,
                RelayBaudrate = int.TryParse(TxtRelayBaud.Text, out int rb) ? rb : 38400,
                RelayMask = TxtRelayMask.Text,
                RelayOffMs = int.TryParse(TxtRelayOff.Text, out int roff) ? roff : 10000,
                RelayOnMs = int.TryParse(TxtRelayOn.Text, out int ron) ? ron : 1000,
                MountDevice = TxtStressMount.Text,
                TargetDirectory = TxtStressDir.Text,
                DiskSizeGb = TxtStressSize.Text.Replace(',', '.'),
                ExpectedImeis = TxtModemImei.Text,
                ModemCount = TxtModemCount.Text,
                CustomFile = TxtCustomFile.Text
            };

            if (RbModem.IsChecked == true) config.Mode = "modem";
            else if (RbSsd1Gb.IsChecked == true) config.Mode = "ssd1gb";
            else if (RbFsck.IsChecked == true) config.Mode = "fsck";
            else if (RbCustom.IsChecked == true) config.Mode = "custom";
            else config.Mode = "stress";

            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;

            _engine = new TestEngine(config, LogMessage, UpdateStatus, UpdateStats, UpdateDevStatus, SetRelayIndicator);
            await _engine.StartAsync();

            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
        }

        private void BtnStop_Click(object sender, RoutedEventArgs e)
        {
            _engine?.Stop();
            BtnStop.IsEnabled = false;
        }

        private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "JSON Profile (*.json)|*.json" };
            if (sfd.ShowDialog() == true)
            {
                var dict = new Dictionary<string, string>
                {
                    { "Ip", TxtIpAddress.Text }, { "User", TxtUser.Text }, { "Cycles", TxtCycles.Text },
                    { "WaitOn", TxtWaitOn.Text }, { "WaitOff", TxtWaitOff.Text }
                };
                File.WriteAllText(sfd.FileName, JsonSerializer.Serialize(dict));
            }
        }

        private void BtnLoadProfile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "JSON Profile (*.json)|*.json" };
            if (ofd.ShowDialog() == true)
            {
                var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ofd.FileName));
                if (dict != null)
                {
                    if (dict.ContainsKey("Ip")) TxtIpAddress.Text = dict["Ip"];
                    if (dict.ContainsKey("User")) TxtUser.Text = dict["User"];
                    if (dict.ContainsKey("Cycles")) TxtCycles.Text = dict["Cycles"];
                }
            }
        }

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "Log File (*.txt)|*.txt", FileName = "ConsoleLog.txt" };
            if (sfd.ShowDialog() == true) File.WriteAllText(sfd.FileName, TxtConsole.Text);
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtConsole.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff}: {message}\n");
                TxtConsole.ScrollToEnd();
            });
        }

        private void UpdateStatus(string message, double progress)
        {
            Dispatcher.Invoke(() => 
            { 
                TxtStatus.Text = message; 
                PbStatus.Value = progress; 
                
                if (_dashboardState != null) _dashboardState.Status = message;
            });
        }

        private void UpdatePieChart(int success, int fail)
        {
            PieChartCanvas.Children.Clear();
            double center = 22.5;
            double radius = 22.5;
            int total = success + fail;

            // Pokud testy ještě nezačaly (total je 0), vykreslí se šedý kruh
            if (total == 0)
            {
                var el = new System.Windows.Shapes.Ellipse { Width = radius * 2, Height = radius * 2, Fill = Brushes.SlateGray };
                Canvas.SetLeft(el, 0); Canvas.SetTop(el, 0);
                PieChartCanvas.Children.Add(el);
                return;
            }

            // Pokud proběhly testy, ale je 0 chyb (100% úspěšnost), vykreslí se plný zelený kruh
            if (fail == 0)
            {
                var el = new System.Windows.Shapes.Ellipse { Width = radius * 2, Height = radius * 2, Fill = Brushes.MediumSeaGreen };
                Canvas.SetLeft(el, 0); Canvas.SetTop(el, 0);
                PieChartCanvas.Children.Add(el);
                return;
            }

            double successAngle = (double)success / total * 360;

            if (success > 0)
            {
                DrawPieSlice(center, center, radius, 0, successAngle, Brushes.MediumSeaGreen);
            }
            if (fail > 0)
            {
                DrawPieSlice(center, center, radius, successAngle, 360, Brushes.Crimson);
            }
        }

        private void DrawPieSlice(double cx, double cy, double r, double startAngleDeg, double endAngleDeg, Brush fill)
        {
            if (Math.Abs(endAngleDeg - startAngleDeg - 360) < 0.01)
            {
                var el = new System.Windows.Shapes.Ellipse { Width = r * 2, Height = r * 2, Fill = fill };
                Canvas.SetLeft(el, cx - r); Canvas.SetTop(el, cy - r);
                PieChartCanvas.Children.Add(el);
                return;
            }

            double startRad = (startAngleDeg - 90) * Math.PI / 180;
            double endRad = (endAngleDeg - 90) * Math.PI / 180;

            Point startPoint = new Point(cx + r * Math.Cos(startRad), cy + r * Math.Sin(startRad));
            Point endPoint = new Point(cx + r * Math.Cos(endRad), cy + r * Math.Sin(endRad));

            bool isLargeArc = (endAngleDeg - startAngleDeg) > 180;

            PathFigure fig = new PathFigure { StartPoint = new Point(cx, cy), IsClosed = true };
            fig.Segments.Add(new LineSegment(startPoint, true));
            fig.Segments.Add(new ArcSegment(endPoint, new Size(r, r), 0, isLargeArc, SweepDirection.Clockwise, true));

            PathGeometry geom = new PathGeometry();
            geom.Figures.Add(fig);

            System.Windows.Shapes.Path path = new System.Windows.Shapes.Path { Fill = fill, Data = geom };
            PieChartCanvas.Children.Add(path);
        }

        private void UpdateStats(bool success, double timeSeconds, int cycle)
        {
            Dispatcher.Invoke(() =>
            {
                _totalCount++;
                if (success) _succCount++; else _failCount++;
                _totalSeconds += timeSeconds;

                TxtSucc.Text = $"SUCCESS: {_succCount}";
                TxtFail.Text = $"FAIL: {_failCount}";
                TxtTotal.Text = $"TOTAL: {_totalCount}";
                UpdatePieChart(_succCount, _failCount);

                TimeSpan t = TimeSpan.FromSeconds(_totalSeconds / _totalCount);
                TxtAvgTime.Text = $"AVG TIME: {t:hh\\:mm\\:ss}";

                if (_dashboardState != null)
                {
                    string targetCycles = TxtCycles.Text == "0" ? "∞" : TxtCycles.Text;
                    _dashboardState.Cycles = $"Cycles: {_totalCount} / {targetCycles}";
                    _dashboardState.ChartColor = success ? Brushes.MediumSeaGreen : Brushes.Crimson;
                }
            });
        }
        private void UpdateDevStatus(string status)
        {
            Dispatcher.Invoke(() =>
            {
                TxtDevStatus.Text = $"DEV: {status}";
                TxtDevStatus.Foreground = status == "Online" ? Brushes.MediumSeaGreen : Brushes.Crimson;

                if (_dashboardState != null)
                {
                    _dashboardState.DevState = status;
                    _dashboardState.DevStateColor = status == "Online" ? Brushes.MediumSeaGreen : (status == "Offline" ? Brushes.Crimson : Brushes.SlateGray);
                }
            });
        }
    }
}