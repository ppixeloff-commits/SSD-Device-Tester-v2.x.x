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
        private readonly Func<bool> _isPaused;

        private CancellationTokenSource? _cts;
        private bool _isOnline;
        
        private string _expectedSsd1GbMd5 = "";

        private Stopwatch _activeTime = new Stopwatch();
        public TimeSpan ActiveTime => _activeTime.Elapsed;

        public TestEngine(TestConfig config, Action<string> log, Action<string, double> status, Action<bool, double, int> statsUpdate, Action<string> devStatus, Action<bool?> relayUpdate, Func<bool>? isPaused = null)
        {
            _config = config;
            _log = log;
            _status = status;
            _statsUpdate = statsUpdate;
            _devStatus = devStatus;
            _relayUpdate = relayUpdate;
            _isPaused = isPaused ?? (() => false);
        }

        public async Task StartAsync()
        {
            _cts = new CancellationTokenSource();
            _isOnline = false;
            _relayUpdate(null);
            
            _activeTime.Restart();
            
            _ = Task.Run(() => PingLoopAsync(_cts.Token));
            
            try
            {
                await Task.Run(() => RunTestLoopAsync(_cts.Token));
            }
            finally
            {
                _cts.Cancel(); 
            }
        }

        public void Stop() 
        {
            _activeTime.Stop();
            _cts?.Cancel();
        }

        public void ResetTime()
        {
            if (_activeTime.IsRunning) _activeTime.Restart();
            else _activeTime.Reset();
        }

        private async Task WaitWhilePausedAsync(CancellationToken token)
        {
            if (!_isPaused()) return;
            _activeTime.Stop();
            _log("Test paused by user - will resume after you click Resume.");
            
            while (_isPaused()) { token.ThrowIfCancellationRequested(); await Task.Delay(300, token); }
            
            _log("Test resumed.");
            _activeTime.Start();
        }

        private async Task PingLoopAsync(CancellationToken token)
        {
            try
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
            catch (OperationCanceledException) { }
            catch (Exception ex) { _log($"\nPingLoop Error: {ex.Message}"); }
        }

        private async Task RunTestLoopAsync(CancellationToken token)
        {
            if (_config.Mode == "continuous") { await RunContinuousLoopAsync(token); return; }

            int currentCycle = 1;
            int totalRun = 0, totalSucc = 0, totalFail = 0;

            try
            {
                while (!token.IsCancellationRequested && (_config.Cycles == 0 || currentCycle <= _config.Cycles))
                {
                    await WaitWhilePausedAsync(token);

                    _log($"\n--- STARTING CYCLE {currentCycle} ---");
                    
                    if (_config.RelayEnable)
                    {
                        _status($"Cycle {currentCycle}: Turning relay ON...", 5);
                        TurnRelayOn();
                        await Task.Delay(_config.RelayOnMs, token);
                    }

                    _status($"Cycle {currentCycle}: Waiting for device...", 10);
                    
                    int waitSec = 0;
                    bool watchdogFailed = false;
                    while (!_isOnline) 
                    { 
                        token.ThrowIfCancellationRequested(); await Task.Delay(1000, token); waitSec++;
                        if (_config.RelayAutoRecover && waitSec >= _config.RelayAutoRecoverSeconds)
                        {
                            _log($"Watchdog: Router failed to boot within {_config.RelayAutoRecoverSeconds}s. Cycle marked as FAILED.");
                            watchdogFailed = true;
                            break;
                        }
                    }

                    Stopwatch sw = Stopwatch.StartNew();
                    bool success = false;

                    if (!watchdogFailed)
                    {
                        await WaitWhilePausedAsync(token);
                        _status($"Cycle {currentCycle}: Online. Waiting boot...", 20);
                        await Task.Delay(_config.WaitOnline * 1000, token);

                        success = ExecuteCycleMode();
                    }
                    sw.Stop();

                    totalRun++;
                    if (success) totalSucc++; else totalFail++;

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
            catch (OperationCanceledException) { _log("\nTest stopped by user."); }
            catch (Exception ex) { _log($"\nError: {ex.Message}"); }
            finally 
            {
                _activeTime.Stop();
                PrintSummary(totalRun, totalSucc, totalFail, _activeTime.Elapsed);
                _status("Finished.", 100); 
            }
        }

        private string ExecSsh(string command, string activityLabel)
        {
            using var stop = new System.Threading.ManualResetEventSlim(false);
            var heartbeat = Task.Run(() =>
            {
                int sec = 0;
                while (!stop.Wait(5000)) { sec += 5; _log($"{activityLabel}... still running ({sec}s elapsed)"); }
            });
            try { return NetworkEngine.ExecuteSshCommand(_config, command); }
            finally { stop.Set(); heartbeat.Wait(); }
        }

        private async Task RunContinuousLoopAsync(CancellationToken token)
        {
            int currentCycle = 1, totalRun = 0, totalSucc = 0, totalFail = 0;
            try
            {
                _log("\n--- STARTING CONTINUOUS READ/WRITE TEST (no restart / no relay cycling between passes) ---");
                
                if (_config.RelayEnable)
                {
                    _status("Turning relay ON...", 5);
                    TurnRelayOn();
                    await Task.Delay(_config.RelayOnMs, token);
                }

                _status("Waiting for device...", 10);
                
                int waitOnlineSec = 0;
                while (!_isOnline)
                {
                    token.ThrowIfCancellationRequested(); await Task.Delay(1000, token); waitOnlineSec++;
                    if (_config.RelayAutoRecover && waitOnlineSec >= _config.RelayAutoRecoverSeconds)
                    {
                        _log($"Watchdog: Initial boot failed within {_config.RelayAutoRecoverSeconds}s. Power-cycling...");
                        if (_config.RelayEnable) PowerCycleRelay();
                        waitOnlineSec = 0;
                    }
                    else if (waitOnlineSec % 10 == 0) 
                    {
                        _log($"Still waiting for device to come online... ({waitOnlineSec}s elapsed)");
                    }
                }

                _log("Device is online.");
                _status("Online. Starting continuous R/W loop...", 20);
                await Task.Delay(_config.WaitOnline * 1000, token);

                MountDeviceIfNeeded();

                while (!token.IsCancellationRequested && (_config.Cycles == 0 || currentCycle <= _config.Cycles))
                {
                    await WaitWhilePausedAsync(token);

                    bool watchdogFailed = false;
                    if (!_isOnline)
                    {
                        _status($"Pass {currentCycle}: device offline, waiting for reconnect...", 15);
                        int reconnectWait = 0;
                        while (!_isOnline) 
                        { 
                            token.ThrowIfCancellationRequested(); await Task.Delay(1000, token); reconnectWait++;
                            if (_config.RelayAutoRecover && reconnectWait >= _config.RelayAutoRecoverSeconds)
                            {
                                _log($"Watchdog: Device failed to reconnect within {_config.RelayAutoRecoverSeconds}s. Pass marked as FAILED.");
                                watchdogFailed = true;
                                break;
                            }
                        }
                    }

                    Stopwatch sw = Stopwatch.StartNew();
                    bool success = false;

                    if (!watchdogFailed)
                    {
                        _log($"\n--- CONTINUOUS PASS {currentCycle} ---");
                        _status($"Pass {currentCycle}: writing/reading...", 50);
                        try { success = ExecuteStress(); } catch (Exception ex) { _log($"\nSSH Error: {ex.Message}"); success = false; }
                    }
                    else
                    {
                        if (_config.RelayEnable) PowerCycleRelay();
                    }
                    sw.Stop();

                    totalRun++;
                    if (success) totalSucc++; else totalFail++;
                    _statsUpdate(success, sw.Elapsed.TotalSeconds, currentCycle);
                    currentCycle++;
                }
            }
            catch (OperationCanceledException) { _log("\nTest stopped by user."); }
            catch (Exception ex) { _log($"\nError: {ex.Message}"); }
            finally 
            {
                _activeTime.Stop();
                PrintSummary(totalRun, totalSucc, totalFail, _activeTime.Elapsed);
                _status("Finished.", 100); 
            }
        }

        private void PrintSummary(int total, int succ, int fail, TimeSpan elapsed)
        {
            _log("\n=========================================");
            _log("           TEST RUN SUMMARY              ");
            _log("=========================================");
            _log($"Total Cycles Run : {total}");
            _log($"Successful       : {succ}");
            _log($"Failed           : {fail}");
            _log($"Total Time       : {elapsed:hh\\:mm\\:ss}");
            if (total > 0) _log($"Success Rate     : {(succ * 100.0 / total):0.##}%");
            _log("=========================================\n");
        }

        private void MountDeviceIfNeeded()
        {
            if (!string.IsNullOrWhiteSpace(_config.MountDevice))
            {
                _log($"\n[Mount] Mounting {_config.MountDevice} at {_config.TargetDirectory}...");
                string mountOut = ExecSsh($"mkdir -p {_config.TargetDirectory} && mount {_config.MountDevice} {_config.TargetDirectory} 2>&1", "Mount");
                _log($"[Mount] Output: {(string.IsNullOrWhiteSpace(mountOut) ? "(ok, no output)" : mountOut.Trim())}");
            }
        }

        private bool ExecuteCycleMode()
        {
            try
            {
                switch (_config.Mode)
                {
                    case "stress": MountDeviceIfNeeded(); return ExecuteStress();
                    case "ssd1gb": MountDeviceIfNeeded(); return ExecuteSsd1Gb();
                    case "fsck": return ExecuteFsck();
                    case "modem": return ExecuteModem();
                    case "ping": return ExecuteRemotePing();
                    case "custom": return ExecuteCustom();
                    default: _log($"\nERROR: Neznámý typ testu: {_config.Mode}"); return false;
                }
            }
            catch (Exception ex) { _log($"\nSSH Error: {ex.Message}"); return false; }
        }

        private bool ExecuteRemotePing()
        {
            _log("\n=== REMOTE PING TEST STARTED ===");
            if (string.IsNullOrWhiteSpace(_config.PingTarget))
            {
                _log("\nERROR: Target address is empty.");
                _log("=== REMOTE PING TEST FAILED ===");
                return false;
            }
            
            _log($"\nExecuting: ping -c 1 -W {_config.PingTargetTimeout} {_config.PingTarget}");
            string outStr = ExecSsh($"ping -c 1 -W {_config.PingTargetTimeout} {_config.PingTarget} 2>&1", "Remote Ping");
            _log($"\nPing Output:\n{outStr.Trim()}");
            
            bool success = outStr.Contains(" 1 received") || outStr.Contains(", 0% packet loss");
            
            if (!success) _log($"\nERROR: Ping to {_config.PingTarget} failed or timed out.");
            
            _log(success ? "\n=== REMOTE PING TEST PASSED ===" : "\n=== REMOTE PING TEST FAILED ===");
            return success;
        }

        private bool ExecuteStress()
        {
            _log("\n=== STRESS TEST STARTED ===");
            if (!double.TryParse(_config.DiskSizeGb, out double diskGb)) diskGb = 3.8;
            int stressMb = (int)Math.Min((diskGb * 1024) * 0.8, 20000);
            string file = $"{_config.TargetDirectory}/stress_test.bin";
            
            _log($"\n[1/3] Executing Write: dd if=/dev/zero of={file} bs=1M count={stressMb} conv=fsync");
            string wOut = ExecSsh($"dd if=/dev/zero of={file} bs=1M count={stressMb} conv=fsync 2>&1", "Write");
            _log($"\nWrite Output:\n{wOut.Trim()}");
            
            _log($"\n[2/3] Executing Read: dd if={file} of=/dev/null bs=1M");
            string rOut = ExecSsh($"dd if={file} of=/dev/null bs=1M 2>&1", "Read");
            _log($"\nRead Output:\n{rOut.Trim()}");
            
            _log($"\n[3/3] Cleanup: rm -f {file}");
            ExecSsh($"rm -f {file} 2>&1", "Cleanup");
            
            bool wSuccess = wOut.Contains("copied");
            bool rSuccess = rOut.Contains("copied");
            if (!wSuccess) _log("\nERROR: Write step failed.");
            if (!rSuccess) _log("\nERROR: Read step failed.");

            bool overall = wSuccess && rSuccess;
            _log(overall ? "\n=== STRESS TEST PASSED ===" : "\n=== STRESS TEST FAILED ===");
            return overall;
        }

        private bool ExecuteSsd1Gb()
        {
            _log("\n=== SSD 1GB TEST STARTED ===");
            string file = $"{_config.TargetDirectory}/test1gb.bin";

            _log("\n[1/3] Checking if file exists and getting MD5...");
            string md5Out = ExecSsh($"md5sum {file} 2>&1", "MD5 check");
            string md5 = md5Out.Split(' ')[0].Trim();
            
            if (md5.Length < 32 || md5Out.Contains("No such file"))
            {
                _log("\nFile missing or invalid. Creating new 1GB file...");
                _log($"\n[2/3] Executing: dd if=/dev/urandom of={file} bs=1M count=1024 conv=fsync");
                string wOut = ExecSsh($"dd if=/dev/urandom of={file} bs=1M count=1024 conv=fsync 2>&1", "Write");
                
                if (!wOut.Contains("copied")) 
                {
                    _log("\nERROR: Failed to create 1GB file.");
                    _log("=== SSD 1GB TEST FAILED ===");
                    return false;
                }

                _log("\n[3/3] Calculating MD5 of newly created file...");
                md5Out = ExecSsh($"md5sum {file} 2>&1", "MD5 check");
                md5 = md5Out.Split(' ')[0].Trim();
                
                if (md5.Length == 32)
                {
                    _expectedSsd1GbMd5 = md5;
                    _log($"\nOK: Stored baseline MD5 for future cycles: {_expectedSsd1GbMd5}");
                    _log("=== SSD 1GB TEST PASSED (Baseline Created) ===");
                    return true;
                }
                _log("\nERROR: Failed to get MD5 of new file.");
                _log("=== SSD 1GB TEST FAILED ===");
                return false;
            }
            
            _log($"\nFound existing file with MD5: {md5}");
            if (string.IsNullOrEmpty(_expectedSsd1GbMd5))
            {
                _expectedSsd1GbMd5 = md5;
                _log($"\nOK: Stored baseline MD5 for future cycles: {_expectedSsd1GbMd5}");
                _log("=== SSD 1GB TEST PASSED (Baseline Established) ===");
                return true;
            }

            _log($"\n[2/3] Comparing MD5...");
            if (md5 == _expectedSsd1GbMd5)
            {
                _log($"\nOK: MD5 matches expected value ({_expectedSsd1GbMd5})");
                _log("=== SSD 1GB TEST PASSED ===");
                return true;
            }
            
            _log($"\nERROR: MD5 mismatch! Expected: {_expectedSsd1GbMd5}, Got: {md5}");
            _log("=== SSD 1GB TEST FAILED ===");
            return false;
        }

        private bool ExecuteModem()
        {
            _log("\n=== MODEM TEST STARTED ===");
            _log("\nExecuting: modemctl -i");
            string output = ExecSsh("modemctl -i 2>&1", "Modem query");
            _log($"\nModemctl Output:\n{output.Trim()}");
            
            bool hasExpectedImeis = !string.IsNullOrWhiteSpace(_config.ExpectedImeis);
            bool hasExpectedCount = int.TryParse(_config.ModemCount, out int expectedCount);

            if (!hasExpectedImeis && !hasExpectedCount)
            {
                bool hasOutput = !string.IsNullOrWhiteSpace(output) && !output.Contains("command not found");
                _log(hasOutput ? "\n=== MODEM TEST PASSED ===" : "\n=== MODEM TEST FAILED ===");
                return hasOutput;
            }

            bool success = true;

            if (hasExpectedCount)
            {
                var matches = Regex.Matches(output, @"(?<!\d)\d{15}(?!\d)");
                _log($"\n[Count Check] Found {matches.Count} IMEIs (15-digit numbers). Expected: {expectedCount}");
                if (matches.Count != expectedCount) 
                {
                    _log("\nERROR: Modem count mismatch.");
                    success = false;
                }
            }

            if (hasExpectedImeis)
            {
                _log($"\n[IMEI Check] Verifying expected IMEIs: {_config.ExpectedImeis}");
                string[] imeis = _config.ExpectedImeis.Split(',');
                foreach (var imei in imeis)
                {
                    string cleanImei = imei.Trim();
                    if (output.Contains(cleanImei)) _log($"\nOK: IMEI {cleanImei} found.");
                    else { _log($"\nERROR: IMEI {cleanImei} not found."); success = false; }
                }
            }
            
            _log(success ? "\n=== MODEM TEST PASSED ===" : "\n=== MODEM TEST FAILED ===");
            return success;
        }

        private bool ExecuteCustom()
        {
            _log("\n=== CUSTOM TEST STARTED ===");
            if (string.IsNullOrWhiteSpace(_config.CustomFile) || !File.Exists(_config.CustomFile))
            {
                _log($"\nERROR: Custom file not found or path empty: '{_config.CustomFile}'");
                _log("=== CUSTOM TEST FAILED ===");
                return false;
            }
            
            _log($"\nLoading commands from: {_config.CustomFile}");
            string[] lines = File.ReadAllLines(_config.CustomFile);
            int cmdIndex = 1;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');
                string cmd = parts[0];
                string expected = parts.Length > 1 ? parts[1].Trim() : "";
                
                _log($"\n[CMD {cmdIndex}] Executing: {cmd}");
                string outStr = ExecSsh($"{cmd} 2>&1", $"CMD '{cmd}'");
                _log($"\n[CMD {cmdIndex}] Output:\n{outStr.Trim()}");
                
                if (!string.IsNullOrWhiteSpace(expected) && !outStr.Contains(expected))
                {
                    _log($"\nERROR: Output did not contain expected string '{expected}'.");
                    _log("=== CUSTOM TEST FAILED ===");
                    return false;
                }
                cmdIndex++;
            }
            
            _log("\n=== CUSTOM TEST PASSED ===");
            return true;
        }

        private bool ExecuteFsck()
        {
            _log("\n=== FSCK TEST STARTED ===");
            if (string.IsNullOrWhiteSpace(_config.MountDevice))
            {
                _log("\nERROR: Mount device is empty (cannot run fsck).");
                _log("=== FSCK TEST FAILED ===");
                return false;
            }
            
            _log($"\n[1/2] Unmounting {_config.TargetDirectory} before FSCK...");
            string umountOut = ExecSsh($"umount {_config.TargetDirectory} 2>&1", "Unmount");
            if (!string.IsNullOrWhiteSpace(umountOut)) _log($"\nUnmount output: {umountOut.Trim()}");
            
            _log($"\n[2/2] Executing: fsck -y {_config.MountDevice}");
            string outStr = ExecSsh($"fsck -y {_config.MountDevice} 2>&1", "FSCK");
            _log($"\nFSCK Output:\n{outStr.Trim()}");
            
            bool success = !outStr.Contains("UNEXPECTED INCONSISTENCY") && !outStr.Contains("FAILED");
            if (!success) _log("\nERROR: FSCK reported failure or unexpected inconsistency.");
            
            _log(success ? "\n=== FSCK TEST PASSED ===" : "\n=== FSCK TEST FAILED ===");
            return success;
        }

        private void TurnRelayOn()
        {
            try
            {
                int mask = RelayController.ParseMask(_config.RelayMask);
                _log($"\nRelay ON via {_config.RelayPort} (addr {_config.RelayAddress}, mask {_config.RelayMask})");
                _relayUpdate(true);
                RelayController.TurnOn(_config.RelayPort, _config.RelayBaudrate, _config.RelayAddress, mask);
            }
            catch (Exception ex) { _log($"\nRelay Error: {ex.Message}"); }
        }

        private void PowerCycleRelay()
        {
            try
            {
                int mask = RelayController.ParseMask(_config.RelayMask);
                _log($"\nRelay OFF via {_config.RelayPort} (addr {_config.RelayAddress}, mask {_config.RelayMask})");
                _relayUpdate(false);
                RelayController.TurnOff(_config.RelayPort, _config.RelayBaudrate, _config.RelayAddress, mask);
                Thread.Sleep(_config.RelayOffMs);
                
                _log($"\nRelay ON");
                _relayUpdate(true);
                RelayController.TurnOn(_config.RelayPort, _config.RelayBaudrate, _config.RelayAddress, mask);
                Thread.Sleep(_config.RelayOnMs);
            }
            catch (Exception ex) { _log($"\nRelay Error: {ex.Message}"); }
        }
    }
}