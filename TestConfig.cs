namespace SSHTester
{
    public class TestConfig
    {
        public string Ip { get; set; } = "";
        public string User { get; set; } = "root";
        public string Password { get; set; } = "";
        public string SshKey { get; set; } = "";
        public int Cycles { get; set; } = 0;
        public int SshTimeout { get; set; } = 60;
        public int WaitOnline { get; set; } = 10;
        public int WaitOffline { get; set; } = 3;
        public int PingCount { get; set; } = 5;
        public double PingInterval { get; set; } = 1.0;

        public bool RelayEnable { get; set; } = false;
        public bool RelayAutoRecover { get; set; } = false;
        public int RelayAutoRecoverSeconds { get; set; } = 300;
        public string RelayPort { get; set; } = "COM3";
        public int RelayBaudrate { get; set; } = 38400;
        public int RelayAddress { get; set; } = 1;
        public string RelayMask { get; set; } = "1";
        public int RelayOffMs { get; set; } = 10000;
        public int RelayOnMs { get; set; } = 1000;

        public string Mode { get; set; } = "stress";
        
        public string MountDevice { get; set; } = "";
        public string TargetDirectory { get; set; } = "/mnt";
        public string DiskSizeGb { get; set; } = "3.8";
        
        public string ExpectedImeis { get; set; } = "";
        public string ModemCount { get; set; } = "";
        
        public string CustomFile { get; set; } = "";
    }
}