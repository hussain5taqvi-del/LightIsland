using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Devices.Radios;
using Windows.Devices.WiFi;
using Windows.Security.Credentials;

namespace LightIsland;

/// <summary>Wi-Fi networks and Bluetooth devices in one place: switch the radios, join / leave a network, see and remove paired devices.</summary>
internal sealed class NetTool : Window
{
    private readonly Ui.Chips _tabs = new(new[] { "Wi-Fi", "Bluetooth" }, 0);
    private readonly StackPanel _wifiPanel = new(), _btPanel = new();
    private readonly StackPanel _wifiList = new(), _btList = new();
    private readonly StackPanel _wifiRadio = new(), _btRadio = new();
    private readonly Ui.Status _st = new();
    private readonly Ui.Input _pass = new("Password", false, 0);
    private readonly StackPanel _connect = new();
    private Border _connectCard = null!;
    private readonly TextBlock _connectTitle = new() { FontWeight = FontWeights.SemiBold, FontSize = 14, Foreground = Ui.Text };
    private WiFiAdapter? _adapter;
    private readonly Dictionary<string, WiFiAvailableNetwork> _nets = new();
    private string? _sel, _connectedName;
    private bool _selSecure;

    public NetTool()
    {
        Ui.Setup(this, "Wi-Fi & Bluetooth", 560, 720, true);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(_tabs);

        // Wi-Fi
        _wifiPanel.Children.Add(_wifiRadio);
        _wifiPanel.Children.Add(Ui.Row(Ui.Btn("Scan again", () => _ = LoadWifiAsync(true), true), Ui.Btn("Wi-Fi settings", () => Open("ms-settings:network-wifi"))));
        _wifiList.Margin = new Thickness(0, 10, 0, 0);
        _wifiPanel.Children.Add(_wifiList);
        _connect.Margin = new Thickness(0, 8, 0, 0);
        _connect.Children.Add(_connectTitle);
        _pass.Margin = new Thickness(0, 8, 0, 8);
        _connect.Children.Add(_pass);
        _connect.Children.Add(Ui.Row(Ui.Btn("Connect", () => _ = ConnectAsync(), true), Ui.Btn("Disconnect", DisconnectWifi)));
        _connectCard = Ui.Card(_connect, 12);
        _connectCard.Visibility = Visibility.Collapsed;
        _connect.Visibility = Visibility.Visible;
        _wifiPanel.Children.Add(_connectCard);
        root.Children.Add(_wifiPanel);

        // Bluetooth
        _btPanel.Visibility = Visibility.Collapsed;
        _btPanel.Children.Add(_btRadio);
        _btPanel.Children.Add(Ui.Row(Ui.Btn("Refresh", () => _ = LoadBtAsync(), true), Ui.Btn("Bluetooth settings", () => Open("ms-settings:bluetooth"))));
        var note = Ui.Label("To connect a device, open Bluetooth settings (Windows does not let apps connect devices). Here you can switch Bluetooth, see what is connected and remove devices.", 11.5, true);
        note.Margin = new Thickness(0, 8, 0, 0);
        _btPanel.Children.Add(note);
        _btList.Margin = new Thickness(0, 10, 0, 0);
        _btPanel.Children.Add(_btList);
        root.Children.Add(_btPanel);

        _st.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_st);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _tabs.Selected += i =>
        {
            _wifiPanel.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _btPanel.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (i == 1 && _btList.Children.Count == 0) _ = LoadBtAsync();
        };
        Loaded += (_, _) => _ = LoadWifiAsync(false);
    }

    private static void Open(string uri)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri) { UseShellExecute = true }); } catch { }
    }

    // ---------- radios ----------

    private async Task<Radio?> GetRadioAsync(RadioKind kind)
    {
        try
        {
            if (await Radio.RequestAccessAsync() != RadioAccessStatus.Allowed) return null;
            var all = await Radio.GetRadiosAsync();
            return all.FirstOrDefault(r => r.Kind == kind);
        }
        catch { return null; }
    }

    private async Task FillRadioAsync(StackPanel host, RadioKind kind, string name, Func<Task> after)
    {
        host.Children.Clear();
        var radio = await GetRadioAsync(kind);
        if (radio == null)
        {
            host.Children.Add(Ui.Label("No " + name + " adapter found (or Windows blocked access).", 13, true));
            host.Margin = new Thickness(0, 12, 0, 12);
            return;
        }
        var g = new Grid { Margin = new Thickness(0, 12, 0, 12) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Label(name + " is " + (radio.State == RadioState.On ? "on" : "off"), 15, false, true));
        bool on = radio.State == RadioState.On;
        var b = Ui.Btn(on ? "Turn off" : "Turn on", () => { }, !on);
        b.MouseLeftButtonUp += async (_, _) =>
        {
            b.IsEnabled = false;
            try
            {
                var res = await radio.SetStateAsync(on ? RadioState.Off : RadioState.On);
                if (res != RadioAccessStatus.Allowed) _st.Say("Windows did not allow this change.", true);
            }
            catch { _st.Say("Could not switch " + name + ".", true); }
            await Task.Delay(600);
            await after();
        };
        Grid.SetColumn(b, 1);
        g.Children.Add(b);
        host.Children.Add(g);
    }

    // ---------- Wi-Fi ----------

    private async Task LoadWifiAsync(bool rescan)
    {
        _st.Info("");
        await FillRadioAsync(_wifiRadio, RadioKind.WiFi, "Wi-Fi", () => LoadWifiAsync(true));
        _wifiList.Children.Clear();
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed)
            {
                _wifiList.Children.Add(Ui.Label("Windows needs location access for apps to list Wi-Fi networks. Turn on \"Let desktop apps access your location\" in Privacy settings.", 13, true));
                _wifiList.Children.Add(Ui.Row(Ui.Btn("Open privacy settings", () => Open("ms-settings:privacy-location"), true)));
                return;
            }
            if (_adapter == null)
            {
                var all = await WiFiAdapter.FindAllAdaptersAsync();
                _adapter = all.FirstOrDefault();
            }
            if (_adapter == null) { _wifiList.Children.Add(Ui.Label("No Wi-Fi adapter found on this PC.", 13, true)); return; }

            _st.Info("Scanning...");
            try { await _adapter.ScanAsync(); } catch { }
            _connectedName = null;
            try
            {
                var prof = await _adapter.NetworkAdapter.GetConnectedProfileAsync();
                _connectedName = prof?.ProfileName;
            }
            catch { }

            _nets.Clear();
            var best = new Dictionary<string, WiFiAvailableNetwork>();
            foreach (var n in _adapter.NetworkReport.AvailableNetworks)
            {
                if (string.IsNullOrWhiteSpace(n.Ssid)) continue;
                if (!best.TryGetValue(n.Ssid, out var o) || n.SignalBars > o.SignalBars) best[n.Ssid] = n;
            }
            foreach (var kv in best.OrderByDescending(x => x.Key == _connectedName).ThenByDescending(x => x.Value.SignalBars))
            {
                _nets[kv.Key] = kv.Value;
                AddNetRow(kv.Key, kv.Value);
            }
            if (_nets.Count == 0) _wifiList.Children.Add(Ui.Label("No networks found.", 13, true));
            _st.Info(_connectedName != null ? "Connected to " + _connectedName : "Not connected");
        }
        catch (Exception ex)
        {
            _st.Say("Could not read Wi-Fi: " + ex.Message, true, 5000);
        }
    }

    private static bool IsOpen(WiFiAvailableNetwork n) => n.SecuritySettings.NetworkAuthenticationType == NetworkAuthenticationType.Open80211;

    private void AddNetRow(string ssid, WiFiAvailableNetwork n)
    {
        bool secure = !IsOpen(n);
        bool connected = ssid == _connectedName;
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var l = new StackPanel();
        l.Children.Add(Ui.Label(ssid, 14, false, true));
        l.Children.Add(Ui.Label((connected ? "Connected  -  " : "") + (secure ? "Secured" : "Open") , 12, true));
        g.Children.Add(l);
        var bars = Ui.Label(new string('▂', Math.Max(1, (int)n.SignalBars)) + new string('·', Math.Max(0, 4 - (int)n.SignalBars)), 14, true);
        Grid.SetColumn(bars, 1);
        g.Children.Add(bars);
        var card = Ui.Card(g, 12);
        card.Margin = new Thickness(0, 0, 0, 8);
        card.Cursor = Cursors.Hand;
        if (connected) card.BorderBrush = Ui.Accent;
        card.MouseLeftButtonUp += (_, _) =>
        {
            _sel = ssid;
            _selSecure = secure;
            _connectTitle.Text = ssid;
            _pass.Visibility = secure ? Visibility.Visible : Visibility.Collapsed;
            _pass.Text = "";
            _connectCard.Visibility = Visibility.Visible;
            if (secure) _pass.Focus();
        };
        _wifiList.Children.Add(card);
    }

    private async Task ConnectAsync()
    {
        if (_adapter == null || _sel == null || !_nets.TryGetValue(_sel, out var net)) return;
        _st.Info("Connecting to " + _sel + "...");
        try
        {
            WiFiConnectionResult res;
            if (_selSecure && _pass.Text.Length > 0)
                res = await _adapter.ConnectAsync(net, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = _pass.Text });
            else
                res = await _adapter.ConnectAsync(net, WiFiReconnectionKind.Automatic);
            if (res.ConnectionStatus == WiFiConnectionStatus.Success)
            {
                _st.Say("Connected to " + _sel, false, 4000);
                await LoadWifiAsync(false);
            }
            else if (res.ConnectionStatus == WiFiConnectionStatus.InvalidCredential)
                _st.Say(_selSecure && _pass.Text.Length == 0 ? "Enter the password for this network." : "Wrong password.", true, 4000);
            else
                _st.Say("Could not connect (" + res.ConnectionStatus + ").", true, 4000);
        }
        catch (Exception ex) { _st.Say("Could not connect: " + ex.Message, true, 5000); }
    }

    private void DisconnectWifi()
    {
        try
        {
            _adapter?.Disconnect();
            _st.Say("Disconnected");
            _ = Task.Delay(800).ContinueWith(_ => Dispatcher.Invoke(() => _ = LoadWifiAsync(false)));
        }
        catch { _st.Say("Could not disconnect.", true); }
    }

    // ---------- Bluetooth ----------

    private async Task LoadBtAsync()
    {
        await FillRadioAsync(_btRadio, RadioKind.Bluetooth, "Bluetooth", LoadBtAsync);
        _btList.Children.Clear();
        try
        {
            var infos = await DeviceInformation.FindAllAsync(BluetoothDevice.GetDeviceSelectorFromPairingState(true));
            var rows = new List<(DeviceInformation Info, bool Conn)>();
            foreach (var info in infos)
            {
                if (string.IsNullOrWhiteSpace(info.Name)) continue;
                bool conn = false;
                try
                {
                    using var bd = await BluetoothDevice.FromIdAsync(info.Id);
                    conn = bd != null && bd.ConnectionStatus == BluetoothConnectionStatus.Connected;
                }
                catch { }
                rows.Add((info, conn));
            }
            if (rows.Count == 0) { _btList.Children.Add(Ui.Label("No paired devices.", 13, true)); return; }
            foreach (var r in rows.OrderByDescending(x => x.Conn).ThenBy(x => x.Info.Name))
            {
                var info = r.Info;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var l = new StackPanel();
                l.Children.Add(Ui.Label(info.Name, 14, false, true));
                var sub = Ui.Label(r.Conn ? "Connected" : "Paired", 12, !r.Conn);
                if (r.Conn) sub.Foreground = Ui.Good;
                l.Children.Add(sub);
                g.Children.Add(l);
                var rm = Ui.Btn("Remove", () => _ = UnpairAsync(info));
                Grid.SetColumn(rm, 1);
                g.Children.Add(rm);
                var card = Ui.Card(g, 12);
                card.Margin = new Thickness(0, 0, 0, 8);
                _btList.Children.Add(card);
            }
        }
        catch { _btList.Children.Add(Ui.Label("Could not read Bluetooth devices. Is Bluetooth turned on?", 13, true)); }
    }

    private async Task UnpairAsync(DeviceInformation info)
    {
        if (MessageBox.Show(this, "Remove \"" + info.Name + "\"? You will have to pair it again to use it.", "Remove device", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try
        {
            var r = await info.Pairing.UnpairAsync();
            _st.Say(r.Status == DeviceUnpairingResultStatus.Unpaired ? "Removed" : "Windows could not remove it.", r.Status != DeviceUnpairingResultStatus.Unpaired);
        }
        catch { _st.Say("Could not remove the device.", true); }
        await LoadBtAsync();
    }
}
