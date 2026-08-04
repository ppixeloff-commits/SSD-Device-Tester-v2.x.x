using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using Renci.SshNet;

namespace SSHTester
{
    public class TestEngine
    {
        private readonly TestConfig _config;
        private readonly Action<string> _log;
        private readonly Action<string, double> _status;
        private readonly Action<bool, double, int> _statsUpdate;
        private readonly Action<string> _devStatus;
        private readonly Action<bool?> _relayUpdate;
        
        private CancellationTokenSource? _cts;
        private bool _isOnline;

        public TestEngine(TestConfig config, Action<string> log, Action<string, double> status, Action<bool, double, int> statsUpdate, Action<string> devStatus, Action<bool?> relayUpdate)
        {
            _config = config;
            _log = log;
            _status = status;
            _statsUpdate = statsUpdate;
            _devStatus = devStatus;
            _relayUpdate = relayUpdate;
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _isOnline = false;
            _relayUpdate(null);
            _ = PingLoopAsync(_cts.Token);
            await RunTestLoopAsync(_cts.Token);
        }

        public void Stop() => _cts?.Cancel();

        private async Task PingLoopAsync(CancellationToken token)
        {
            int successStreak = 0, failStreak = 0;
            while (!token.IsCancellationRequested)
            {
                if (string.IsNullOrWhiteSpace(_config.Ip)) { await Task.Delay(1000, token); continue; }
                bool isUp = NetworkEngine.PingHost(_config.Ip);
                if (isUp)
                {
                    successStreak++; failStreak = 0;
                    if (successStreak >= _config.PingCount) { _isOnline = true; _devStatus("Online"); }
                    else if (!_isOnline) _devStatus("Checking...");
                }
                else
                {
                    failStreak++; successStreak = 0;
                    if (failStreak >= _config.PingCount) { _isOnline = false; _devStatus("Offline"); }
                    else if (_isOnline) _devStatus("Checking...");
                }
                await Task.Delay((int)(_config.PingInterval * 1000), token);
            }
        }

        private async Task RunTestLoopAsync(CancellationToken token)
        {
            int currentCycle = 1;
            try
            {
                while (!token.IsCancellationRequested && (_config.Cycles == 0 || currentCycle <= _config.Cycles))
                {
                    _log($"--- STARTING CYCLE {currentCycle} ---");
                    _status($"Cycle {currentCycle}: Waiting for device...", 10);
                    
                    int waitSec = 0;
                    while (!_isOnline) 
                    { 
                        token.ThrowIfCancellationRequested(); 
                        await Task.Delay(1000, token); 
                        waitSec++;

                        if (_config.RelayAutoRecover && waitSec >= _config.RelayAutoRecoverSeconds)
                        {
                            _log($"Auto-recover: Device offline for {_config.RelayAutoRecoverSeconds}s, power-cycling...");
                            PowerCycleRelay();
                            waitSec = 0;
                        }
                    }
                    
                    _status($"Cycle {currentCycle}: Online. Waiting boot...", 20);
                    await Task.Delay(_config.WaitOnline * 1000, token);

                    Stopwatch sw = Stopwatch.StartNew();
                    bool success = ExecuteCycleMode();
                    sw.Stop();

                    if (_config.RelayEnable)
                    {
                        _status($"Cycle {currentCycle}: Power-cycling relay...", 60);
                        PowerCycleRelay();
                    }

                    _statsUpdate(success, sw.Elapsed.TotalSeconds, currentCycle);
                    currentCycle++;

                    if (_config.Cycles == 0 || currentCycle <= _config.Cycles)
                    {
                        if (!_config.RelayEnable)
                        {
                            while (_isOnline) { token.ThrowIfCancellationRequested(); await Task.Delay(500, token); }
                        }
                        await Task.Delay(_config.WaitOffline * 1000, token);
                    }
                }
            }
            catch (OperationCanceledException) { _log("Test stopped."); }
            catch (Exception ex) { _log($"Error: {ex.Message}"); }
            finally { _status("Finished.", 100); }
        }

        private bool ExecuteCycleMode()
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(_config.MountDevice))
                {
                    NetworkEngine.ExecuteSshCommand(_config, $"mkdir -p {_config.TargetDirectory} && mount {_config.MountDevice} {_config.TargetDirectory}");
                }

                if (_config.Mode == "stress") return ExecuteStress();
                if (_config.Mode == "ssd1gb") return ExecuteSsd1Gb();
                if (_config.Mode == "modem") return ExecuteModem();
                if (_config.Mode == "custom") return ExecuteCustom();
                return false;
            }
            catch (Exception ex)
            {
                _log($"SSH Error: {ex.Message}");
                return false;
            }
        }

        private bool ExecuteStress()
        {
            if (!double.TryParse(_config.DiskSizeGb, out double diskGb)) diskGb = 3.8;
            int stressMb = (int)Math.Min((diskGb * 1024) * 0.8, 20000);
            _log($"Calculated Stress Size: {stressMb} MB");
            
            string file = $"{_config.TargetDirectory}/stress_test.bin";
            _log($"Executing Write: dd if=/dev/zero of={file} bs=1M count={stressMb} conv=fsync");
            string wOut = NetworkEngine.ExecuteSshCommand(_config, $"dd if=/dev/zero of={file} bs=1M count={stressMb} conv=fsync 2>&1");
            _log($"Write Output:\n{wOut}");
            
            _log($"Executing Read: dd if={file} of=/dev/null bs=1M");
            string rOut = NetworkEngine.ExecuteSshCommand(_config, $"dd if={file} of=/dev/null bs=1M 2>&1");
            _log($"Read Output:\n{rOut}");
            
            NetworkEngine.ExecuteSshCommand(_config, $"rm -f {file}");
            return wOut.Contains("copied") && rOut.Contains("copied");
        }

        private bool ExecuteSsd1Gb()
        {
            string file = $"{_config.TargetDirectory}/test1gb.bin";
            string md5 = NetworkEngine.ExecuteSshCommand(_config, $"md5sum {file}").Split(' ')[0];
            if (md5.Length < 32)
            {
                _log("File missing. Creating 1GB...");
                NetworkEngine.ExecuteSshCommand(_config, $"dd if=/dev/urandom of={file} bs=1M count=1024 conv=fsync");
                return true;
            }
            _log($"MD5 checked: {md5}");
            return true;
        }

        private bool ExecuteModem()
        {
            string output = NetworkEngine.ExecuteSshCommand(_config, "mmcli -L");
            _log(output);
            if (!string.IsNullOrWhiteSpace(_config.ExpectedImeis))
            {
                foreach (var imei in _config.ExpectedImeis.Split(','))
                {
                    if (!output.Contains(imei.Trim())) return false;
                }
            }
            return output.Contains("Modem");
        }

        private bool ExecuteCustom()
        {
            if (string.IsNullOrWhiteSpace(_config.CustomFile) || !File.Exists(_config.CustomFile)) return false;
            foreach (var line in File.ReadAllLines(_config.CustomFile))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');
                string cmd = parts[0];
                string expected = parts.Length > 1 ? parts[1] : "";
                
                string outStr = NetworkEngine.ExecuteSshCommand(_config, cmd);
                _log($"CMD: {cmd} -> {outStr.Trim()}");
                if (!string.IsNullOrWhiteSpace(expected) && !outStr.Contains(expected)) return false;
            }
            return true;
        }

        private void PowerCycleRelay()
        {
            try
            {
                _log($"Relay OFF via {_config.RelayPort}");
                _relayUpdate(false);
                RelayController.SendRelayCommand(_config.RelayPort, _config.RelayBaudrate, new byte[] { 0x00 });
                Thread.Sleep(_config.RelayOffMs);
                
                _log($"Relay ON");
                _relayUpdate(true);
                RelayController.SendRelayCommand(_config.RelayPort, _config.RelayBaudrate, new byte[] { 0xFF });
                Thread.Sleep(_config.RelayOnMs);
            }
            catch (Exception ex) { _log($"Relay Error: {ex.Message}"); }
        }
    }
}