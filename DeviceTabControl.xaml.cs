using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private DeviceState? _dashboardState;
        private TestConfig? _activeConfig;
        
        private System.Windows.Threading.DispatcherTimer _uiTimer;

        public static readonly string[] CsvKeys = new[] { "TabName", "Ip", "User", "Password", "SshKey", "Cycles", "Timeout", "WaitOn", "WaitOff", "PingCnt", "PingInt", "RelayEnable", "RelayAutoRecover", "AutoRecoverSec", "RelayPort", "RelayBaud", "RelayAddr", "RelayMask", "RelayOff", "RelayOn", "StressMount", "StressDir", "StressSize", "ModemImei", "ModemCount", "CustomFile", "Mode" };

        public DeviceTabControl()
        {
            InitializeComponent();
            UpdatePieChart(0, 0);

            _uiTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _uiTimer.Tick += (s, e) =>
            {
                if (_engine != null) 
                    TxtAvgTime.Text = $"TIME: {_engine.ActiveTime:hh\\:mm\\:ss}";
            };

            BtnStart.Click += (s, e) => StartTest();
            BtnStop.Click += (s, e) => StopTest();
            BtnPause.Click += BtnPause_Click;
            BtnApplyChanges.Click += BtnApplyChanges_Click;
            BtnClearLog.Click += BtnClearLog_Click;
            BtnSaveProfile.Click += BtnSaveProfile_Click;
            BtnLoadProfile.Click += BtnLoadProfile_Click;
            BtnExportLog.Click += BtnExportLog_Click;
            BtnExportCsv.Click += BtnExportCsv_Click;
            BtnBrowse.Click += BtnBrowse_Click;
            
            BtnRelayOn.Click += async (s, e) => await SendManualRelayAsync(true);
            BtnRelayOff.Click += async (s, e) => await SendManualRelayAsync(false);
            BtnRelayCycle.Click += BtnRelayCycle_Click;
        }

        private (string port, int baud, int address, int mask) CaptureRelayUiValues()
        {
            string port = TxtRelayPort.Text;
            int baud = int.TryParse(TxtRelayBaud.Text, out int rb) ? rb : 38400;
            int address = int.TryParse(TxtRelayAddr.Text, out int radr) ? radr : 1;
            int mask = RelayController.ParseMask(TxtRelayMask.Text);
            return (port, baud, address, mask);
        }

        private async Task SendManualRelayAsync(bool turnOn)
        {
            var (port, baud, address, mask) = CaptureRelayUiValues();
            try
            {
                await Task.Run(() =>
                {
                    if (turnOn) RelayController.TurnOn(port, baud, address, mask);
                    else RelayController.TurnOff(port, baud, address, mask);
                });
                SetRelayIndicator(turnOn);
                LogMessage($"Manual Relay Command: {(turnOn ? "ON" : "OFF")} (mask {mask}) on {port}");
            }
            catch (Exception ex) { LogMessage($"Relay Error: {ex.Message}"); }
        }

        private async void BtnRelayCycle_Click(object sender, RoutedEventArgs e)
        {
            BtnRelayCycle.IsEnabled = false;
            int offMs = int.TryParse(TxtRelayOff.Text, out int roff) ? roff : 10000;
            
            await SendManualRelayAsync(false);
            await Task.Delay(offMs);
            await SendManualRelayAsync(true);
            
            BtnRelayCycle.IsEnabled = true;
        }

        public void InitializeDashboard(DeviceState state)
        {
            _dashboardState = state;
            TxtDeviceName.Text = state.TabName;
            TxtDeviceName.TextChanged += (s, e) =>
            {
                if (_dashboardState != null && !string.IsNullOrWhiteSpace(TxtDeviceName.Text))
                    _dashboardState.TabName = TxtDeviceName.Text;
            };
            
            state.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(DeviceState.TabName) && TxtDeviceName.Text != state.TabName)
                    TxtDeviceName.Text = state.TabName;
            };

            TxtIpAddress.TextChanged += (s, e) => 
            {
                if (_dashboardState != null)
                    _dashboardState.IpAddress = string.IsNullOrWhiteSpace(TxtIpAddress.Text) ? "Unknown" : TxtIpAddress.Text;
            };
        }

        public DeviceState? GetDashboardState() => _dashboardState;

        public void SetCredentials(string ip, string user)
        {
            TxtIpAddress.Text = ip;
            TxtUser.Text = user;
        }

        public async void StartTest()
        {
            if (string.IsNullOrWhiteSpace(TxtIpAddress.Text) || !BtnStart.IsEnabled) return;

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
                RelayAddress = int.TryParse(TxtRelayAddr.Text, out int radr) ? radr : 1,
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
            else if (RbSsdContinuous.IsChecked == true) config.Mode = "continuous";
            else config.Mode = "stress";

            BtnStart.IsEnabled = false;
            BtnStop.IsEnabled = true;
            BtnPause.IsEnabled = true;
            BtnApplyChanges.IsEnabled = true;
            if (_dashboardState != null) _dashboardState.IsPaused = false;
            BtnPause.Content = "Pause";

            _activeConfig = config;
            _engine = new TestEngine(config, LogMessage, UpdateStatus, UpdateStats, UpdateDevStatus, SetRelayIndicator, () => _dashboardState?.IsPaused ?? false);
            
            _uiTimer.Start();

            await _engine.StartAsync();

            _uiTimer.Stop();
            BtnStart.IsEnabled = true;
            BtnStop.IsEnabled = false;
            BtnPause.IsEnabled = false;
            BtnApplyChanges.IsEnabled = false;
            _activeConfig = null;
            if (_dashboardState != null) _dashboardState.IsPaused = false;
            BtnPause.Content = "Pause";
        }

        public void StopTest()
        {
            if (_dashboardState != null) _dashboardState.IsPaused = false;
            BtnPause.Content = "Pause";
            _engine?.Stop();
            BtnStop.IsEnabled = false;
        }

        private void BtnPause_Click(object sender, RoutedEventArgs e)
        {
            if (_dashboardState == null) return;
            _dashboardState.IsPaused = !_dashboardState.IsPaused;
            BtnPause.Content = _dashboardState.IsPaused ? "Resume" : "Pause";
            LogMessage(_dashboardState.IsPaused ? "Pause requested - will pause once the current step finishes." : "Resume requested.");
        }

        private void BtnClearLog_Click(object sender, RoutedEventArgs e)
        {
            TxtConsole.Clear();
            ResetStats();
        }

        public void ResetStats()
        {
            _succCount = 0;
            _failCount = 0;
            _totalCount = 0;
            
            if (_engine != null) _engine.ResetTime();
            
            TxtSucc.Text = $"SUCCESS: 0";
            TxtFail.Text = $"FAIL: 0";
            TxtTotal.Text = $"TOTAL: 0";
            TxtAvgTime.Text = $"TIME: 00:00:00";
            
            UpdatePieChart(0, 0);

            if (_dashboardState != null)
            {
                string targetCycles = TxtCycles.Text == "0" ? "∞" : TxtCycles.Text;
                _dashboardState.Cycles = $"Cycles: 0 / {targetCycles}";
                _dashboardState.UpdateChart(0, 0);
            }
        }

        private void BtnApplyChanges_Click(object sender, RoutedEventArgs e)
        {
            if (_activeConfig == null)
            {
                LogMessage("Apply Changes: no test is currently running.");
                return;
            }

            _activeConfig.Cycles = int.TryParse(TxtCycles.Text, out int c) ? c : _activeConfig.Cycles;
            _activeConfig.SshTimeout = int.TryParse(TxtTimeout.Text, out int t) ? t : _activeConfig.SshTimeout;
            _activeConfig.WaitOnline = int.TryParse(TxtWaitOn.Text, out int wo) ? wo : _activeConfig.WaitOnline;
            _activeConfig.WaitOffline = int.TryParse(TxtWaitOff.Text, out int wf) ? wf : _activeConfig.WaitOffline;
            _activeConfig.PingCount = int.TryParse(TxtPingCnt.Text, out int pc) ? pc : _activeConfig.PingCount;
            _activeConfig.PingInterval = double.TryParse(TxtPingInt.Text, out double pi) ? pi : _activeConfig.PingInterval;

            _activeConfig.RelayEnable = ChkRelayEnable.IsChecked ?? _activeConfig.RelayEnable;
            _activeConfig.RelayAutoRecover = ChkRelayAutoRecover.IsChecked ?? _activeConfig.RelayAutoRecover;
            _activeConfig.RelayAutoRecoverSeconds = int.TryParse(TxtAutoRecoverSec.Text, out int ars) ? ars : _activeConfig.RelayAutoRecoverSeconds;
            _activeConfig.RelayPort = TxtRelayPort.Text;
            _activeConfig.RelayBaudrate = int.TryParse(TxtRelayBaud.Text, out int rb) ? rb : _activeConfig.RelayBaudrate;
            _activeConfig.RelayAddress = int.TryParse(TxtRelayAddr.Text, out int radr) ? radr : _activeConfig.RelayAddress;
            _activeConfig.RelayMask = TxtRelayMask.Text;
            _activeConfig.RelayOffMs = int.TryParse(TxtRelayOff.Text, out int roff) ? roff : _activeConfig.RelayOffMs;
            _activeConfig.RelayOnMs = int.TryParse(TxtRelayOn.Text, out int ron) ? ron : _activeConfig.RelayOnMs;

            _activeConfig.MountDevice = TxtStressMount.Text;
            _activeConfig.TargetDirectory = TxtStressDir.Text;
            _activeConfig.DiskSizeGb = TxtStressSize.Text.Replace(',', '.');
            _activeConfig.ExpectedImeis = TxtModemImei.Text;
            _activeConfig.ModemCount = TxtModemCount.Text;
            _activeConfig.CustomFile = TxtCustomFile.Text;

            if (RbModem.IsChecked == true) _activeConfig.Mode = "modem";
            else if (RbSsd1Gb.IsChecked == true) _activeConfig.Mode = "ssd1gb";
            else if (RbFsck.IsChecked == true) _activeConfig.Mode = "fsck";
            else if (RbCustom.IsChecked == true) _activeConfig.Mode = "custom";
            else if (RbSsdContinuous.IsChecked == true) _activeConfig.Mode = "continuous";
            else _activeConfig.Mode = "stress";

            LogMessage("Configuration applied to the running test.");
        }

        private void ToggleParams(object sender, RoutedEventArgs e)
        {
            if (PanelStress == null || PanelModem == null || PanelCustom == null) return;
            PanelStress.Visibility = Visibility.Collapsed;
            PanelModem.Visibility = Visibility.Collapsed;
            PanelCustom.Visibility = Visibility.Collapsed;
            PanelStressSize.Visibility = Visibility.Collapsed;

            if (RbSsdStress?.IsChecked == true || RbSsdContinuous?.IsChecked == true) { PanelStress.Visibility = Visibility.Visible; PanelStressSize.Visibility = Visibility.Visible; }
            else if (RbSsd1Gb?.IsChecked == true || RbFsck?.IsChecked == true) { PanelStress.Visibility = Visibility.Visible; }
            else if (RbModem?.IsChecked == true) { PanelModem.Visibility = Visibility.Visible; }
            else if (RbCustom?.IsChecked == true) { PanelCustom.Visibility = Visibility.Visible; }
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
                if (isOn == true) { TxtRelayStatusTxt.Text = "RELAY: ON"; TxtRelayStatusTxt.Foreground = Brushes.MediumSeaGreen; }
                else if (isOn == false) { TxtRelayStatusTxt.Text = "RELAY: OFF"; TxtRelayStatusTxt.Foreground = Brushes.Crimson; }
                else { TxtRelayStatusTxt.Text = "RELAY: UNK"; TxtRelayStatusTxt.Foreground = Brushes.SlateGray; }
            });
        }

        public Dictionary<string, string> ExportConfigDict() => BuildProfileDict();
        public void ImportConfigDict(Dictionary<string, string> dict) => ApplyProfileDict(dict);

        private Dictionary<string, string> BuildProfileDict()
        {
            string mode = RbModem.IsChecked == true ? "modem"
                : RbSsd1Gb.IsChecked == true ? "ssd1gb"
                : RbFsck.IsChecked == true ? "fsck"
                : RbCustom.IsChecked == true ? "custom"
                : RbSsdContinuous.IsChecked == true ? "continuous"
                : "stress";

            string pass = ChkShowPass.IsChecked == true ? TxtPasswordVisible.Text : TxtPassword.Password;

            return new Dictionary<string, string>
            {
                { "TabName", TxtDeviceName.Text },
                { "Ip", TxtIpAddress.Text },
                { "User", TxtUser.Text },
                { "Password", pass },
                { "SshKey", TxtSshKey.Text },
                { "Cycles", TxtCycles.Text },
                { "Timeout", TxtTimeout.Text },
                { "WaitOn", TxtWaitOn.Text },
                { "WaitOff", TxtWaitOff.Text },
                { "PingCnt", TxtPingCnt.Text },
                { "PingInt", TxtPingInt.Text },
                { "RelayEnable", (ChkRelayEnable.IsChecked ?? false).ToString() },
                { "RelayAutoRecover", (ChkRelayAutoRecover.IsChecked ?? false).ToString() },
                { "AutoRecoverSec", TxtAutoRecoverSec.Text },
                { "RelayPort", TxtRelayPort.Text },
                { "RelayBaud", TxtRelayBaud.Text },
                { "RelayAddr", TxtRelayAddr.Text },
                { "RelayMask", TxtRelayMask.Text },
                { "RelayOff", TxtRelayOff.Text },
                { "RelayOn", TxtRelayOn.Text },
                { "StressMount", TxtStressMount.Text },
                { "StressDir", TxtStressDir.Text },
                { "StressSize", TxtStressSize.Text },
                { "ModemImei", TxtModemImei.Text },
                { "ModemCount", TxtModemCount.Text },
                { "CustomFile", TxtCustomFile.Text },
                { "Mode", mode }
            };
        }

        private void ApplyProfileDict(Dictionary<string, string> dict)
        {
            void Set(string key, Action<string> setter) { if (dict.TryGetValue(key, out var v)) setter(v); }

            Set("TabName", v => TxtDeviceName.Text = v);
            Set("Ip", v => TxtIpAddress.Text = v);
            Set("User", v => TxtUser.Text = v);
            Set("Password", v => { TxtPassword.Password = v; TxtPasswordVisible.Text = v; });
            Set("SshKey", v => TxtSshKey.Text = v);
            Set("Cycles", v => TxtCycles.Text = v);
            Set("Timeout", v => TxtTimeout.Text = v);
            Set("WaitOn", v => TxtWaitOn.Text = v);
            Set("WaitOff", v => TxtWaitOff.Text = v);
            Set("PingCnt", v => TxtPingCnt.Text = v);
            Set("PingInt", v => TxtPingInt.Text = v);
            Set("RelayEnable", v => ChkRelayEnable.IsChecked = v == "True");
            Set("RelayAutoRecover", v => ChkRelayAutoRecover.IsChecked = v == "True");
            Set("AutoRecoverSec", v => TxtAutoRecoverSec.Text = v);
            Set("RelayPort", v => TxtRelayPort.Text = v);
            Set("RelayBaud", v => TxtRelayBaud.Text = v);
            Set("RelayAddr", v => TxtRelayAddr.Text = v);
            Set("RelayMask", v => TxtRelayMask.Text = v);
            Set("RelayOff", v => TxtRelayOff.Text = v);
            Set("RelayOn", v => TxtRelayOn.Text = v);
            Set("StressMount", v => TxtStressMount.Text = v);
            Set("StressDir", v => TxtStressDir.Text = v);
            Set("StressSize", v => TxtStressSize.Text = v);
            Set("ModemImei", v => TxtModemImei.Text = v);
            Set("ModemCount", v => TxtModemCount.Text = v);
            Set("CustomFile", v => TxtCustomFile.Text = v);

            if (dict.TryGetValue("Mode", out var mode))
            {
                switch (mode)
                {
                    case "modem": RbModem.IsChecked = true; break;
                    case "ssd1gb": RbSsd1Gb.IsChecked = true; break;
                    case "fsck": RbFsck.IsChecked = true; break;
                    case "custom": RbCustom.IsChecked = true; break;
                    case "continuous": RbSsdContinuous.IsChecked = true; break;
                    default: RbSsdStress.IsChecked = true; break;
                }
                ToggleParams(this, new RoutedEventArgs());
            }
        }

        private void BtnSaveProfile_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog
            {
                Filter = "JSON Profile (*.json)|*.json",
                FileName = $"{(string.IsNullOrWhiteSpace(TxtDeviceName.Text) ? "device" : TxtDeviceName.Text)}_profile.json"
            };
            if (sfd.ShowDialog() == true)
            {
                var dict = BuildProfileDict();
                File.WriteAllText(sfd.FileName, JsonSerializer.Serialize(dict, new JsonSerializerOptions { WriteIndented = true }));
                LogMessage($"Profile saved to {sfd.FileName}.");
            }
        }

        private void BtnLoadProfile_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "JSON Profile (*.json)|*.json" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ofd.FileName));
                    if (dict != null) { ApplyProfileDict(dict); LogMessage($"Profile loaded from {ofd.FileName}."); }
                }
                catch (Exception ex) { LogMessage($"Failed to load profile: {ex.Message}"); }
            }
        }

        private void BtnExportLog_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "Log File (*.txt)|*.txt", FileName = "ConsoleLog.txt" };
            if (sfd.ShowDialog() == true) File.WriteAllText(sfd.FileName, TxtConsole.Text);
        }

        private void BtnExportCsv_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV files (*.csv)|*.csv", FileName = "report.csv" };
            if (sfd.ShowDialog() == true)
            {
                var sb = new StringBuilder();
                sb.AppendLine("TabName;IP;Status;DevState;Success;Fail;Total");
                sb.AppendLine($"{TxtDeviceName.Text};{TxtIpAddress.Text};{TxtStatus.Text};{_dashboardState?.DevState};{_succCount};{_failCount};{_totalCount}");
                File.WriteAllText(sfd.FileName, sb.ToString());
                LogMessage($"CSV exported to {sfd.FileName}.");
            }
        }

        private void LogMessage(string message)
        {
            Dispatcher.Invoke(() =>
            {
                TxtConsole.AppendText($"{DateTime.Now:yyyy-MM-dd HH:mm:ss,fff}: {message}\n");
                TxtConsole.ScrollToEnd();

                if (message.Contains("Error", StringComparison.OrdinalIgnoreCase) || message.Contains("Fail", StringComparison.OrdinalIgnoreCase))
                {
                    if (Window.GetWindow(this) is MainWindow mw) mw.AddWarning($"{_dashboardState?.TabName}: {message}");
                }
            });
        }

        private void UpdateStatus(string message, double progress)
        {
            Dispatcher.Invoke(() => { TxtStatus.Text = message; PbStatus.Value = progress; if (_dashboardState != null) _dashboardState.Status = message; });
        }

        private void UpdatePieChart(int success, int fail)
        {
            int total = success + fail;
            if (total == 0)
            {
                LocalChartBg.Stroke = Brushes.SlateGray;
                LocalChartFg.Stroke = Brushes.Transparent;
            }
            else
            {
                LocalChartBg.Stroke = Brushes.Crimson;
                LocalChartFg.Stroke = Brushes.MediumSeaGreen;
            }

            LocalChartFg.StrokeDashArray = ChartUtils.CalculateDashArray(success, total, 48, 8);
        }

        private void UpdateStats(bool success, double timeSeconds, int cycle)
        {
            Dispatcher.Invoke(() =>
            {
                _totalCount++;
                if (success) _succCount++; else _failCount++;

                TxtSucc.Text = $"SUCCESS: {_succCount}";
                TxtFail.Text = $"FAIL: {_failCount}";
                TxtTotal.Text = $"TOTAL: {_totalCount}";
                UpdatePieChart(_succCount, _failCount);

                if (!success && Window.GetWindow(this) is MainWindow mw) mw.AddWarning($"{_dashboardState?.TabName}: Test Cycle {cycle} Failed.");

                if (_dashboardState != null)
                {
                    string targetCycles = TxtCycles.Text == "0" ? "∞" : TxtCycles.Text;
                    _dashboardState.Cycles = $"Cycles: {_totalCount} / {targetCycles}";
                    _dashboardState.UpdateChart(_succCount, _failCount);
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