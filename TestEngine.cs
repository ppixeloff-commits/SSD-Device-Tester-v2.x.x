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
        
        // Globální časovač pro sledování živého času testu bez pauz
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
            
            _activeTime.Restart(); // Spuštění hlavního času testu
            
            _ = Task.Run(() => PingLoopAsync(_cts.Token));
            await Task.Run(() => RunTestLoopAsync(_cts.Token));
        }

        public void Stop() 
        {
            _activeTime.Stop(); // Zastavení času při ukončení
            _cts?.Cancel();
        }

        private async Task WaitWhilePausedAsync(CancellationToken token)
        {
            if (!_isPaused()) return;

            _activeTime.Stop(); // Pozastavení času
            _log("Test paused by user - will resume after you click Resume.");
            
            while (_isPaused())
            {
                token.ThrowIfCancellationRequested();
                await Task.Delay(300, token);
            }
            
            _log("Test resumed.");
            _activeTime.Start(); // Obnovení času
        }

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
            if (_config.Mode == "continuous")
            {
                await RunContinuousLoopAsync(token);
                return;
            }

            int currentCycle = 1;
            int totalRun = 0;
            int totalSucc = 0;
            int totalFail = 0;

            try
            {
                while (!token.IsCancellationRequested && (_config.Cycles == 0 || currentCycle <= _config.Cycles))
                {
                    await WaitWhilePausedAsync(token);

                    _log($"\n--- STARTING CYCLE {currentCycle} ---");
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

                    await WaitWhilePausedAsync(token);

                    _status($"Cycle {currentCycle}: Online. Waiting boot...", 20);
                    await Task.Delay(_config.WaitOnline * 1000, token);

                    Stopwatch sw = Stopwatch.StartNew();
                    bool success = ExecuteCycleMode();
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
                _activeTime.Stop(); // Ujistíme se, že čas po ukončení testu nepoběží
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
                while (!stop.Wait(5000))
                {
                    sec += 5;
                    _log($"{activityLabel}... still running ({sec}s elapsed)");
                }
            });
            try
            {
                return NetworkEngine.ExecuteSshCommand(_config, command);
            }
            finally
            {
                stop.Set();
                heartbeat.Wait();
            }
        }

        private async Task RunContinuousLoopAsync(CancellationToken token)
        {
            int currentCycle = 1;
            int totalRun = 0;
            int totalSucc = 0;
            int totalFail = 0;

            try
            {
                _log("\n--- STARTING CONTINUOUS READ/WRITE TEST (no restart / no relay cycling between passes) ---");
                _status("Waiting for device...", 10);
                _log($"Waiting for device to answer ping ({_config.PingCount} consecutive successes needed)...");

                int waitOnlineSec = 0;
                while (!_isOnline)
                {
                    token.ThrowIfCancellationRequested();
                    await Task.Delay(1000, token);
                    waitOnlineSec++;
                    if (waitOnlineSec % 10 == 0)
                        _log($"Still waiting for device to come online... ({waitOnlineSec}s elapsed, check IP/network/DEV status)");
                }

                _log("Device is online.");
                _status("Online. Starting continuous R/W loop...", 20);
                await Task.Delay(_config.WaitOnline * 1000, token);

                MountDeviceIfNeeded();

                while (!token.IsCancellationRequested && (_config.Cycles == 0 || currentCycle <= _config.Cycles))
                {
                    await WaitWhilePausedAsync(token);

                    if (!_isOnline)
                    {
                        _status($"Pass {currentCycle}: device offline, waiting for reconnect...", 15);
                        _log("Device dropped offline mid-test, waiting for it to come back (no restart triggered)...");
                        while (!_isOnline)
                        {
                            token.ThrowIfCancellationRequested();
                            await Task.Delay(1000, token);
                        }
                    }

                    _log($"\n--- CONTINUOUS PASS {currentCycle} ---");
                    _status($"Pass {currentCycle}: writing/reading...", 50);

                    Stopwatch sw = Stopwatch.StartNew();
                    bool success;
                    try { success = ExecuteStress(); }
                    catch (Exception ex) { _log($"SSH Error: {ex.Message}"); success = false; }
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
                _activeTime.Stop(); // Ujistíme se, že čas po ukončení testu nepoběží
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
            if (total > 0)
                _log($"Success Rate     : {(succ * 100.0 / total):0.##}%");
            _log("=========================================\n");
        }

        private void MountDeviceIfNeeded()
        {
            if (!string.IsNullOrWhiteSpace(_config.MountDevice))
            {
                _log($"[Mount] Mounting {_config.MountDevice} at {_config.TargetDirectory}...");
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
                    case "stress":
                        MountDeviceIfNeeded();
                        return ExecuteStress();
                    
                    case "ssd1gb":
                        MountDeviceIfNeeded();
                        return ExecuteSsd1Gb();
                    
                    case "fsck":
                        return ExecuteFsck();
                    
                    case "modem":
                        return ExecuteModem();
                    
                    case "custom":
                        return ExecuteCustom();
                    
                    default:
                        _log($"ERROR: Neznámý typ testu: {_config.Mode}");
                        return false;
                }
            }
            catch (Exception ex)
            {
                _log($"SSH Error: {ex.Message}");
                return false;
            }
        }

        private bool ExecuteStress()
        {
            _log("=== STRESS TEST STARTED ===");
            if (!double.TryParse(_config.DiskSizeGb, out double diskGb)) diskGb = 3.8;
            int stressMb = (int)Math.Min((diskGb * 1024) * 0.8, 20000);
            
            string file = $"{_config.TargetDirectory}/stress_test.bin";
            _log($"Target File: {file}");
            _log($"Calculated Stress Size: {stressMb} MB (based on disk size {diskGb} GB)");
            
            _log($"[1/3] Executing Write: dd if=/dev/zero of={file} bs=1M count={stressMb} conv=fsync");
            string wOut = ExecSsh($"dd if=/dev/zero of={file} bs=1M count={stressMb} conv=fsync 2>&1", "Write");
            _log($"Write Output:\n{wOut.Trim()}");
            
            _log($"[2/3] Executing Read: dd if={file} of=/dev/null bs=1M");
            string rOut = ExecSsh($"dd if={file} of=/dev/null bs=1M 2>&1", "Read");
            _log($"Read Output:\n{rOut.Trim()}");
            
            _log($"[3/3] Cleanup: rm -f {file}");
            ExecSsh($"rm -f {file} 2>&1", "Cleanup");
            
            bool wSuccess = wOut.Contains("copied");
            bool rSuccess = rOut.Contains("copied");
            
            if (!wSuccess) _log("ERROR: Write step failed (missing 'copied' in output).");
            if (!rSuccess) _log("ERROR: Read step failed (missing 'copied' in output).");

            bool overallSuccess = wSuccess && rSuccess;
            _log(overallSuccess ? "=== STRESS TEST PASSED ===" : "=== STRESS TEST FAILED ===");
            return overallSuccess;
        }

        private bool ExecuteSsd1Gb()
        {
            _log("=== SSD 1GB TEST STARTED ===");
            string file = $"{_config.TargetDirectory}/test1gb.bin";
            _log($"Target File: {file}");

            _log("[1/2] Checking if 1GB file exists via MD5...");
            string md5Out = ExecSsh($"md5sum {file} 2>&1", "MD5 check");
            string md5 = md5Out.Split(' ')[0];
            
            if (md5.Length < 32)
            {
                _log($"File missing or invalid. (Output: {md5Out.Trim()})");
                _log("[2/2] Creating 1GB test file: dd if=/dev/urandom of={file} bs=1M count=1024 conv=fsync");
                string wOut = ExecSsh($"dd if=/dev/urandom of={file} bs=1M count=1024 conv=fsync 2>&1", "Write");
                _log($"Write Output:\n{wOut.Trim()}");
                
                bool writeSuccess = wOut.Contains("copied");
                if (!writeSuccess) _log("ERROR: Failed to create 1GB file.");
                
                _log(writeSuccess ? "=== SSD 1GB TEST PASSED (File Created) ===" : "=== SSD 1GB TEST FAILED ===");
                return writeSuccess;
            }
            
            _log($"OK: MD5 sum is valid: {md5}");
            _log("=== SSD 1GB TEST PASSED ===");
            return true;
        }

        private bool ExecuteModem()
        {
            _log("=== MODEM TEST STARTED ===");
            _log("Executing: modemctl -i");
            string output = ExecSsh("modemctl -i 2>&1", "Modem query");
            _log($"Modemctl Output:\n{output.Trim()}");
            
            if (!string.IsNullOrWhiteSpace(_config.ExpectedImeis))
            {
                _log($"Verifying expected IMEIs: {_config.ExpectedImeis}");
                string[] imeis = _config.ExpectedImeis.Split(',');
                int foundCount = 0;

                foreach (var imei in imeis)
                {
                    string cleanImei = imei.Trim();
                    if (output.Contains(cleanImei))
                    {
                        _log($"OK: IMEI {cleanImei} found.");
                        foundCount++;
                    }
                    else
                    {
                        _log($"ERROR: IMEI {cleanImei} not found.");
                    }
                }
                
                bool success = foundCount == imeis.Length;
                _log(success ? "=== MODEM TEST PASSED ===" : "=== MODEM TEST FAILED ===");
                return success;
            }
            
            bool hasOutput = !string.IsNullOrWhiteSpace(output) && !output.Contains("command not found");
            if (!hasOutput) _log("ERROR: Output is empty or command was not found.");
            
            _log(hasOutput ? "=== MODEM TEST PASSED ===" : "=== MODEM TEST FAILED ===");
            return hasOutput;
        }

        private bool ExecuteCustom()
        {
            _log("=== CUSTOM TEST STARTED ===");
            if (string.IsNullOrWhiteSpace(_config.CustomFile) || !File.Exists(_config.CustomFile))
            {
                _log($"ERROR: Custom file not found or path empty: '{_config.CustomFile}'");
                _log("=== CUSTOM TEST FAILED ===");
                return false;
            }
            
            _log($"Loading commands from: {_config.CustomFile}");
            string[] lines = File.ReadAllLines(_config.CustomFile);
            int cmdIndex = 1;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split(';');
                string cmd = parts[0];
                string expected = parts.Length > 1 ? parts[1].Trim() : "";
                
                _log($"[CMD {cmdIndex}] Executing: {cmd}");
                if (!string.IsNullOrWhiteSpace(expected)) _log($"[CMD {cmdIndex}] Expected string: '{expected}'");
                
                string outStr = ExecSsh($"{cmd} 2>&1", $"CMD '{cmd}'");
                _log($"[CMD {cmdIndex}] Output:\n{outStr.Trim()}");
                
                if (!string.IsNullOrWhiteSpace(expected) && !outStr.Contains(expected))
                {
                    _log($"ERROR: Output did not contain expected string '{expected}'.");
                    _log("=== CUSTOM TEST FAILED ===");
                    return false;
                }
                cmdIndex++;
            }
            
            _log("=== CUSTOM TEST PASSED ===");
            return true;
        }

        private bool ExecuteFsck()
        {
            _log("=== FSCK TEST STARTED ===");
            if (string.IsNullOrWhiteSpace(_config.MountDevice))
            {
                _log("ERROR: Mount device is empty (cannot run fsck).");
                _log("=== FSCK TEST FAILED ===");
                return false;
            }
            
            _log($"[1/2] Unmounting {_config.TargetDirectory} before FSCK...");
            string umountOut = ExecSsh($"umount {_config.TargetDirectory} 2>&1", "Unmount");
            if (!string.IsNullOrWhiteSpace(umountOut)) _log($"Unmount output: {umountOut.Trim()}");
            
            _log($"[2/2] Executing: fsck -y {_config.MountDevice}");
            string outStr = ExecSsh($"fsck -y {_config.MountDevice} 2>&1", "FSCK");
            _log($"FSCK Output:\n{outStr.Trim()}");
            
            bool success = !outStr.Contains("UNEXPECTED INCONSISTENCY") && !outStr.Contains("FAILED");
            if (!success) _log("ERROR: FSCK reported failure or unexpected inconsistency.");
            
            _log(success ? "=== FSCK TEST PASSED ===" : "=== FSCK TEST FAILED ===");
            return success;
        }

        private void PowerCycleRelay()
        {
            try
            {
                int mask = RelayController.ParseMask(_config.RelayMask);

                _log($"Relay OFF via {_config.RelayPort} (addr {_config.RelayAddress}, mask {_config.RelayMask})");
                _relayUpdate(false);
                RelayController.TurnOff(_config.RelayPort, _config.RelayBaudrate, _config.RelayAddress, mask);
                Thread.Sleep(_config.RelayOffMs);
                
                _log($"Relay ON");
                _relayUpdate(true);
                RelayController.TurnOn(_config.RelayPort, _config.RelayBaudrate, _config.RelayAddress, mask);
                Thread.Sleep(_config.RelayOnMs);
            }
            catch (Exception ex) { _log($"Relay Error: {ex.Message}"); }
        }
    }
}