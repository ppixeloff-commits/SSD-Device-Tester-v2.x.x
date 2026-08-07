using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Threading;

namespace SSHTester
{
    // Helper for parsing relay mask strings like "0,1" or "RELE0,RELE1" into a bitmask,
    // mirrors RelayUtils.parse_mask() from the Python predecessor (relay.py).
    public static class RelayUtils
    {
        public static int ParseMask(string maskStr)
        {
            int mask = 0;
            if (string.IsNullOrWhiteSpace(maskStr)) return mask;

            foreach (var part in maskStr.ToUpperInvariant().Replace("RELE", "").Split(','))
            {
                string trimmed = part.Trim();
                if (int.TryParse(trimmed, out int bit))
                    mask |= (1 << bit);
            }
            return mask;
        }
    }

    // Byte-level frame builder for the Arion relay protocol.
    // Direct port of ArionFrame from the Python predecessor (relay.py).
    internal class ArionFrame
    {
        private const int ARION_HEAD_FLAG = 0x80;
        private const int ARION_HEAD_RESP_FLAG = 64;
        private const int ARION_TAIL_FLAG = 128;
        private const int ARION_MAX_ADDR = 63;
        private const int ARION_MAX_FUNC = 127;
        private const int ARION_MAX_BUFFER = 255;

        private readonly byte[] _buffer = new byte[ARION_MAX_BUFFER];
        private int _index = 0;
        private int _sum = 0;

        public void Write(int data)
        {
            if (_index >= ARION_MAX_BUFFER)
                throw new OverflowException("Arion frame buffer overflow!");
            _buffer[_index] = (byte)(data & 0xFF);
            _index++;
        }

        public void Add(int data)
        {
            data &= 0xFF;
            if (data > ARION_TAIL_FLAG) { _index = 0; _sum = 0; }
            Write(data);
            if (data != ARION_TAIL_FLAG) _sum = (_sum - data) & 0x7F;
        }

        public void AddHead(int address, int function, bool response = false)
        {
            if (address > ARION_MAX_ADDR) throw new ArgumentOutOfRangeException(nameof(address), "Arion address is out of range!");
            if (function > ARION_MAX_FUNC) throw new ArgumentOutOfRangeException(nameof(function), "Arion function code data is out of range!");
            int headByte = ARION_HEAD_FLAG | address | (response ? ARION_HEAD_RESP_FLAG : 0);
            Add(headByte);
            Add(function);
        }

        public byte[] Close()
        {
            Write(_sum);
            Write(ARION_TAIL_FLAG);
            byte[] result = new byte[_index];
            Array.Copy(_buffer, result, _index);
            return result;
        }
    }

    // Direct port of ArionReleController from the Python predecessor (relay.py).
    // Sends the two-frame Arion sequence (SET_MODE then SET_RELE) over serial,
    // with parity=Even and DTR/RTS asserted - this is what the physical relay
    // board actually expects. The old C# RelayController just wrote a single raw
    // 0xFF/0x00 byte with parity=None, which the board silently ignores.
    public class ArionRelayController
    {
        private const int ARION_CMD_SET_RELE = 1;
        private const int ARION_CMD_SET_MODE = 36;
        private const int ARION_MODE_3 = 3;
        public const int DEFAULT_BAUDRATE = 38400;
        private const int DEFAULT_TIMEOUT_MS = 1000;

        // Shared across all instances/tabs, like the Python class-level _port_states,
        // so ON/OFF only ever flips the requested bits instead of clobbering others.
        private static readonly Dictionary<string, int> _portStates = new Dictionary<string, int>();
        private static readonly object _lock = new object();

        public string Port { get; }
        public int Baudrate { get; }
        public int Address { get; }

        public ArionRelayController(string port, int baudrate = DEFAULT_BAUDRATE, int address = 1)
        {
            port ??= "";
            Port = port.ToUpperInvariant().StartsWith("COM") ? port : $"COM{port}";
            Baudrate = baudrate;
            Address = address;
        }

        public void SetRelayOn(int mask)
        {
            lock (_lock)
            {
                int current = _portStates.TryGetValue(Port, out int v) ? v : 0;
                int newState = current | mask;
                _portStates[Port] = newState;
                SendState(newState);
            }
        }

        public void SetRelayOff(int mask)
        {
            lock (_lock)
            {
                int current = _portStates.TryGetValue(Port, out int v) ? v : 0;
                int newState = current & ~mask;
                _portStates[Port] = newState;
                SendState(newState);
            }
        }

        private void SendState(int u16Rele)
        {
            try
            {
                using var ser = new SerialPort(Port, Baudrate, Parity.Even, 8, StopBits.One)
                {
                    ReadTimeout = DEFAULT_TIMEOUT_MS,
                    WriteTimeout = DEFAULT_TIMEOUT_MS
                };
                ser.Open();
                ser.DtrEnable = true;
                ser.RtsEnable = true;
                Thread.Sleep(100);

                var fInit = new ArionFrame();
                fInit.AddHead(Address, ARION_CMD_SET_MODE, false);
                fInit.Add(ARION_MODE_3);
                fInit.Add(0);
                byte[] initFrame = fInit.Close();
                ser.Write(initFrame, 0, initFrame.Length);
                ser.BaseStream.Flush();

                Thread.Sleep(200);

                var fCmd = new ArionFrame();
                fCmd.AddHead(Address, ARION_CMD_SET_RELE, false);
                fCmd.Add(u16Rele & 0x7F);
                fCmd.Add((u16Rele >> 7) & 0x7F);
                fCmd.Add((u16Rele >> 14) & 0x7F);
                byte[] cmdFrame = fCmd.Close();
                ser.Write(cmdFrame, 0, cmdFrame.Length);
                ser.BaseStream.Flush();
            }
            catch (Exception e)
            {
                throw new Exception($"Serial port communication failed on {Port}: {e.Message}", e);
            }
        }
    }

    // Thin static facade so the rest of the app (TestEngine, DeviceTabControl) doesn't
    // need to construct ArionRelayController instances directly.
    public static class RelayController
    {
        public static void TurnOn(string port, int baudrate, int address, int mask) =>
            new ArionRelayController(port, baudrate, address).SetRelayOn(mask);

        public static void TurnOff(string port, int baudrate, int address, int mask) =>
            new ArionRelayController(port, baudrate, address).SetRelayOff(mask);

        public static int ParseMask(string maskStr) => RelayUtils.ParseMask(maskStr);
    }
}