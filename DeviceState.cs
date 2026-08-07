using System.ComponentModel;
using System.Windows.Media;

namespace SSHTester
{
    public class DeviceState : INotifyPropertyChanged
    {
        private string _tabName = "";
        private string _ipAddress = "Unknown";
        private string _devState = "Unknown";
        private Brush _devStateColor = Brushes.SlateGray;
        private string _status = "Ready...";
        private string _cycles = "Cycles: 0 / ∞";
        
        private Brush _chartFailColor = Brushes.SlateGray;
        private Brush _chartSuccessColor = Brushes.Transparent;
        private DoubleCollection _successDashArray = new DoubleCollection { 0, 100.53 };
        private bool _isPaused = false;
        
        private int _successCount = 0;
        private int _failCount = 0;

        public string TabName { get => _tabName; set { _tabName = value; OnPropertyChanged(nameof(TabName)); } }
        public string IpAddress { get => _ipAddress; set { _ipAddress = value; OnPropertyChanged(nameof(IpAddress)); } }
        public string DevState { get => _devState; set { _devState = value; OnPropertyChanged(nameof(DevState)); } }
        public Brush DevStateColor { get => _devStateColor; set { _devStateColor = value; OnPropertyChanged(nameof(DevStateColor)); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
        public string Cycles { get => _cycles; set { _cycles = value; OnPropertyChanged(nameof(Cycles)); } }
        
        public Brush ChartFailColor { get => _chartFailColor; set { _chartFailColor = value; OnPropertyChanged(nameof(ChartFailColor)); } }
        public Brush ChartSuccessColor { get => _chartSuccessColor; set { _chartSuccessColor = value; OnPropertyChanged(nameof(ChartSuccessColor)); } }
        public DoubleCollection SuccessDashArray { get => _successDashArray; set { _successDashArray = value; OnPropertyChanged(nameof(SuccessDashArray)); } }
        
        public bool IsPaused { get => _isPaused; set { _isPaused = value; OnPropertyChanged(nameof(IsPaused)); } }

        public int SuccessCount { get => _successCount; set { _successCount = value; OnPropertyChanged(nameof(SuccessCount)); } }
        public int FailCount { get => _failCount; set { _failCount = value; OnPropertyChanged(nameof(FailCount)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

        public void UpdateChart(int success, int fail)
        {
            SuccessCount = success;
            FailCount = fail;
            int total = success + fail;
            if (total == 0)
            {
                ChartFailColor = Brushes.SlateGray;
                ChartSuccessColor = Brushes.Transparent;
                SuccessDashArray = new DoubleCollection { 0, 100.53 };
            }
            else
            {
                ChartFailColor = Brushes.Crimson;
                ChartSuccessColor = Brushes.MediumSeaGreen;
                
                double circumference = 100.53;
                double successDash = ((double)success / total) * circumference;
                SuccessDashArray = new DoubleCollection { successDash, circumference };
            }
        }
    }
}