using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SSHTester
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private int _tabCounter = 0;
        public ObservableCollection<DeviceState> Devices { get; set; } = new ObservableCollection<DeviceState>();
        public ObservableCollection<string> GlobalWarnings { get; set; } = new ObservableCollection<string>();
        
        private ICollectionView _devicesView;

        public int TotalDevices => Devices.Count;
        public int OnlineDevices => Devices.Count(d => d.DevState == "Online");
        public string GlobalSuccessRate 
        {
            get
            {
                int totalSucc = Devices.Sum(d => d.SuccessCount);
                int totalFail = Devices.Sum(d => d.FailCount);
                int total = totalSucc + totalFail;
                return total == 0 ? "0%" : $"{(totalSucc * 100.0 / total):0.##}%";
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            
            _devicesView = CollectionViewSource.GetDefaultView(Devices);
            _devicesView.Filter = FilterDevices;
            
            Devices.CollectionChanged += (s, e) => UpdateAggregateStats();
            AddNewTab();
        }

        private void MainTabControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (e.OriginalSource != MainTabControl) return;

            if (MainTabControl.SelectedItem == AddTabButton)
            {
                Dispatcher.BeginInvoke(new Action(() => AddNewTab()));
            }
        }

        private void AddNewTab(string ip = "", string user = "root")
        {
            _tabCounter++;
            var deviceState = new DeviceState { TabName = $"Device {_tabCounter}", IpAddress = string.IsNullOrWhiteSpace(ip) ? "Unknown" : ip };
            deviceState.PropertyChanged += DeviceState_PropertyChanged;
            Devices.Add(deviceState);

            var newTab = new TabItem { FontWeight = FontWeights.SemiBold };
            
            var headerPanel = new StackPanel { Orientation = Orientation.Horizontal };
            var headerText = new TextBlock { Text = deviceState.TabName, VerticalAlignment = VerticalAlignment.Center };
            
            var closeButton = new TextBlock
            {
                Text = "✕",
                Margin = new Thickness(12, 0, -4, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184)),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                ToolTip = "Zavřít panel"
            };

            closeButton.MouseEnter += (s, ev) => closeButton.Foreground = Brushes.Crimson;
            closeButton.MouseLeave += (s, ev) => closeButton.Foreground = new SolidColorBrush(Color.FromRgb(148, 163, 184));

            closeButton.MouseLeftButtonUp += (s, ev) => 
            {
                ev.Handled = true;
                CloseDeviceTab(newTab, deviceState);
            };

            headerPanel.Children.Add(headerText);
            headerPanel.Children.Add(closeButton);
            newTab.Header = headerPanel;

            var deviceTabControl = new DeviceTabControl();
            deviceTabControl.InitializeDashboard(deviceState);
            deviceTabControl.Margin = new Thickness(0, 10, 0, 0);

            if (!string.IsNullOrWhiteSpace(ip))
            {
                deviceTabControl.SetCredentials(ip, user);
            }

            newTab.Content = deviceTabControl;
            
            int insertIndex = MainTabControl.Items.Count - 1;
            MainTabControl.Items.Insert(insertIndex, newTab);
            MainTabControl.SelectedItem = newTab;
        }

        private void CloseDeviceTab(TabItem tabItem, DeviceState deviceState)
        {
            if (MainTabControl.SelectedItem == tabItem) MainTabControl.SelectedIndex = 0;
            deviceState.PropertyChanged -= DeviceState_PropertyChanged;
            Devices.Remove(deviceState);
            MainTabControl.Items.Remove(tabItem);
        }

        private void DeviceState_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(DeviceState.TabName))
            {
                var state = sender as DeviceState;
                if (state == null) return;
                
                foreach (var item in MainTabControl.Items)
                {
                    if (item is TabItem tab && tab.Content is DeviceTabControl dtc)
                    {
                        var dashState = dtc.GetDashboardState();
                        if (dashState == state)
                        {
                            if (tab.Header is StackPanel sp && sp.Children.Count > 0 && sp.Children[0] is TextBlock tb) 
                            {
                                tb.Text = state.TabName;
                            }
                            break;
                        }
                    }
                }
            }
            if (e.PropertyName == nameof(DeviceState.DevState) || e.PropertyName == nameof(DeviceState.SuccessCount) || e.PropertyName == nameof(DeviceState.FailCount))
            {
                UpdateAggregateStats();
            }
        }

        private void UpdateAggregateStats()
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TotalDevices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(OnlineDevices)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(GlobalSuccessRate)));
            _devicesView.Refresh();
        }

        private void FilterChanged(object sender, RoutedEventArgs e) => _devicesView?.Refresh();

        private bool FilterDevices(object item)
        {
            if (item is DeviceState device)
            {
                bool matchSearch = string.IsNullOrWhiteSpace(TxtSearch?.Text) || 
                                   device.TabName.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase) || 
                                   device.IpAddress.Contains(TxtSearch.Text, StringComparison.OrdinalIgnoreCase);
                
                bool matchFilter = true;
                if (CmbFilter != null)
                {
                    if (CmbFilter.SelectedIndex == 1) matchFilter = device.DevState == "Online";
                    if (CmbFilter.SelectedIndex == 2) matchFilter = device.DevState == "Offline";
                    if (CmbFilter.SelectedIndex == 3) matchFilter = device.FailCount > 0;
                }
                
                return matchSearch && matchFilter;
            }
            return false;
        }

        private void Tile_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is DeviceState state)
            {
                foreach (var item in MainTabControl.Items)
                {
                    if (item is TabItem tab && tab.Content is DeviceTabControl dtc)
                    {
                        var dashState = dtc.GetDashboardState();
                        if (dashState == state)
                        {
                            MainTabControl.SelectedItem = tab;
                            break;
                        }
                    }
                }
            }
        }

        private void TilePause_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is DeviceState state) state.IsPaused = !state.IsPaused;
            e.Handled = true;
        }

        private void TileNameBox_MouseLeftButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = true;

        public void AddWarning(string message)
        {
            Dispatcher.Invoke(() => 
            {
                GlobalWarnings.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
                if (GlobalWarnings.Count > 50) GlobalWarnings.RemoveAt(GlobalWarnings.Count - 1);
            });
        }

        private void BtnClearWarnings_Click(object sender, RoutedEventArgs e) => GlobalWarnings.Clear();

        private void BtnStartAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in MainTabControl.Items) if (item is TabItem tab && tab.Content is DeviceTabControl dtc) dtc.StartTest();
        }

        private void BtnStopAll_Click(object sender, RoutedEventArgs e)
        {
            foreach (var item in MainTabControl.Items) if (item is TabItem tab && tab.Content is DeviceTabControl dtc) dtc.StopTest();
        }

        private void BtnImportDevices_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog ofd = new OpenFileDialog { Filter = "CSV Config Files (*.csv)|*.csv" };
            if (ofd.ShowDialog() == true)
            {
                try
                {
                    var lines = File.ReadAllLines(ofd.FileName);
                    if (lines.Length <= 1) return;
                    
                    var headers = lines[0].Split(';');
                    for (int i = 1; i < lines.Length; i++)
                    {
                        if (string.IsNullOrWhiteSpace(lines[i])) continue;
                        var vals = lines[i].Split(';');
                        
                        // První sloupec je vždy název záložky (Device Name)
                        string tabName = vals.Length > 0 ? vals[0] : $"Device {_tabCounter + 1}";
                        
                        var dict = new System.Collections.Generic.Dictionary<string, string>();
                        for (int j = 1; j < headers.Length && j < vals.Length; j++)
                        {
                            dict[headers[j]] = vals[j];
                        }
                        
                        AddNewTab(dict.ContainsKey("Ip") ? dict["Ip"] : "", dict.ContainsKey("User") ? dict["User"] : "root");
                        
                        var newTab = MainTabControl.Items[MainTabControl.Items.Count - 2] as TabItem;
                        if (newTab?.Content is DeviceTabControl dtc)
                        {
                            dtc.ImportConfigDict(dict);
                            
                            var state = dtc.GetDashboardState();
                            if (state != null)
                            {
                                state.TabName = tabName;
                            }
                        }
                    }
                    AddWarning($"CSV import successful. Loaded {lines.Length - 1} devices.");
                }
                catch (Exception ex)
                {
                    AddWarning($"CSV import failed: {ex.Message}");
                }
            }
        }

        private void BtnExportDevices_Click(object sender, RoutedEventArgs e)
        {
            SaveFileDialog sfd = new SaveFileDialog { Filter = "CSV Config Files (*.csv)|*.csv", FileName = "DevicesConfig.csv" };
            if (sfd.ShowDialog() == true)
            {
                try
                {
                    var sb = new StringBuilder();
                    var keys = DeviceTabControl.CsvKeys;
                    
                    // První sloupec je vždy název záložky, pak následují všechny klíče parametrů
                    sb.AppendLine("TabName;" + string.Join(";", keys));
                    
                    int exportedCount = 0;
                    foreach (var item in MainTabControl.Items)
                    {
                        if (item is TabItem tab && tab.Content is DeviceTabControl dtc)
                        {
                            var dict = dtc.ExportConfigDict();
                            var vals = keys.Select(k => dict.ContainsKey(k) ? dict[k] : "");
                            
                            string safeTabName = dtc.GetDashboardState()?.TabName ?? "Unknown";
                            sb.AppendLine($"{safeTabName};{string.Join(";", vals)}");
                            exportedCount++;
                        }
                    }
                    File.WriteAllText(sfd.FileName, sb.ToString());
                    AddWarning($"Successfully exported config for {exportedCount} devices.");
                }
                catch (Exception ex)
                {
                    AddWarning($"CSV export failed: {ex.Message}");
                }
            }
        }
    }
}