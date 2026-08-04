using System.IO.Ports;

namespace SSHTester
{
    public class RelayController
    {
        public static void SendRelayCommand(string port, int baudRate, byte[] payload)
        {
            using SerialPort serialPort = new SerialPort(port, baudRate, Parity.None, 8, StopBits.One);
            serialPort.Open();
            serialPort.Write(payload, 0, payload.Length);
            serialPort.Close();
        }
    }
}