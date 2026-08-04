using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace SSHTester
{
    public partial class MainWindow : Window
    {
        private int _tabCounter = 0;
        public ObservableCollection<DeviceState> Devices { get; set; } = new ObservableCollection<DeviceState>();

        public MainWindow()
        {
            InitializeComponent();
            DashboardGrid.ItemsSource = Devices;
            AddNewTab();
        }

        private void AddDevice_Click(object sender, RoutedEventArgs e)
        {
            AddNewTab();
        }

        private void CloseTab_Click(object sender, RoutedEventArgs e)
        {
            if (MainTabControl.SelectedItem is TabItem selectedTab && selectedTab.Header.ToString() != "Global Dashboard")
            {
                MainTabControl.Items.Remove(selectedTab);
                
                // Odstranění ze seznamu v Dashboardu
                var deviceToRemove = Devices.FirstOrDefault(d => d.TabName == selectedTab.Header.ToString());
                if (deviceToRemove != null)
                {
                    Devices.Remove(deviceToRemove);
                }
            }
        }

        private void AddNewTab()
        {
            _tabCounter++;
            string tabName = $"Device {_tabCounter}";

            // Vytvoření nového datového řádku pro Dashboard
            var deviceState = new DeviceState { TabName = tabName };
            Devices.Add(deviceState);

            var newTab = new TabItem
            {
                Header = tabName,
                FontWeight = FontWeights.Bold
            };

            var deviceTabControl = new DeviceTabControl();
            deviceTabControl.InitializeDashboard(deviceState); // Propojení záložky s řádkem
            newTab.Content = deviceTabControl;

            MainTabControl.Items.Add(newTab);
            MainTabControl.SelectedItem = newTab;
        }
    }
}