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
        private Brush _chartColor = Brushes.SlateGray;

        public string TabName { get => _tabName; set { _tabName = value; OnPropertyChanged(nameof(TabName)); } }
        public string IpAddress { get => _ipAddress; set { _ipAddress = value; OnPropertyChanged(nameof(IpAddress)); } }
        public string DevState { get => _devState; set { _devState = value; OnPropertyChanged(nameof(DevState)); } }
        public Brush DevStateColor { get => _devStateColor; set { _devStateColor = value; OnPropertyChanged(nameof(DevStateColor)); } }
        public string Status { get => _status; set { _status = value; OnPropertyChanged(nameof(Status)); } }
        public string Cycles { get => _cycles; set { _cycles = value; OnPropertyChanged(nameof(Cycles)); } }
        public Brush ChartColor { get => _chartColor; set { _chartColor = value; OnPropertyChanged(nameof(ChartColor)); } }

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged(string propertyName) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}