using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>Weather (Open-Meteo) and crypto prices (CoinGecko). Both are free services that need internet.</summary>
internal static class Market
{
    public sealed record Coin(string Id, string Symbol, string Name, double Price, double? Change);
    public sealed record Day(DateTime Date, double Hi, double Lo, int Code);
    public sealed record Wx(string City, double Temp, double Feels, int Humidity, double Wind, int Code, List<Day> Days);

    private static readonly HttpClient Http = Make();
    private static HttpClient Make()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.Add("User-Agent", "LightIslandLab/2.0");
        c.DefaultRequestHeaders.Add("Accept", "application/json");
        return c;
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Symbol(string vs) => vs switch { "usd" => "$", "eur" => "€", "gbp" => "£", "pkr" => "Rs ", "inr" => "₹", _ => vs.ToUpperInvariant() + " " };

    public static string Price(double p, string vs)
    {
        string n = p >= 100 ? p.ToString("#,0.##", Inv) : p >= 1 ? p.ToString("#,0.00", Inv) : p.ToString("0.0000##", Inv);
        return Symbol(vs) + n;
    }

    /// <summary>Throws "limit" when CoinGecko says slow down.</summary>
    public static async Task<List<Coin>> CoinsAsync(string ids, string vs)
    {
        string url = "https://api.coingecko.com/api/v3/coins/markets?vs_currency=" + vs + "&ids=" + Uri.EscapeDataString(ids) + "&price_change_percentage=24h&per_page=50";
        using var r = await Http.GetAsync(url);
        if ((int)r.StatusCode == 429) throw new Exception("limit");
        r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var l = new List<Coin>();
        foreach (var e in d.RootElement.EnumerateArray())
        {
            double price = e.TryGetProperty("current_price", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;
            double? ch = e.TryGetProperty("price_change_percentage_24h", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDouble() : null;
            l.Add(new Coin(e.GetProperty("id").GetString() ?? "", (e.GetProperty("symbol").GetString() ?? "").ToUpperInvariant(), e.GetProperty("name").GetString() ?? "", price, ch));
        }
        // keep the order the user chose
        var order = ids.Split(',').ToList();
        return l.OrderBy(x => order.IndexOf(x.Id)).ToList();
    }

    public static async Task<List<(string Id, string Name, string Symbol)>> SearchCoinsAsync(string q)
    {
        using var r = await Http.GetAsync("https://api.coingecko.com/api/v3/search?query=" + Uri.EscapeDataString(q));
        if ((int)r.StatusCode == 429) throw new Exception("limit");
        r.EnsureSuccessStatusCode();
        using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        var l = new List<(string, string, string)>();
        foreach (var e in d.RootElement.GetProperty("coins").EnumerateArray().Take(8))
            l.Add((e.GetProperty("id").GetString() ?? "", e.GetProperty("name").GetString() ?? "", (e.GetProperty("symbol").GetString() ?? "").ToUpperInvariant()));
        return l;
    }

    public static async Task<Wx?> WeatherAsync(string city, bool fahrenheit)
    {
        double lat, lon;
        string name;
        if (string.IsNullOrWhiteSpace(city))
        {
            using var d = JsonDocument.Parse(await Http.GetStringAsync("https://ipwho.is/"));
            var e = d.RootElement;
            if (!e.TryGetProperty("latitude", out var la)) return null;
            lat = la.GetDouble();
            lon = e.GetProperty("longitude").GetDouble();
            name = e.TryGetProperty("city", out var c) ? c.GetString() ?? "" : "";
        }
        else
        {
            using var d = JsonDocument.Parse(await Http.GetStringAsync("https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(city)));
            if (!d.RootElement.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var g = res[0];
            lat = g.GetProperty("latitude").GetDouble();
            lon = g.GetProperty("longitude").GetDouble();
            name = g.GetProperty("name").GetString() ?? city;
        }
        string url = "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString("0.####", Inv) + "&longitude=" + lon.ToString("0.####", Inv) +
                     "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m&daily=weather_code,temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=5" +
                     (fahrenheit ? "&temperature_unit=fahrenheit" : "");
        using var f = JsonDocument.Parse(await Http.GetStringAsync(url));
        var cur = f.RootElement.GetProperty("current");
        var daily = f.RootElement.GetProperty("daily");
        var times = daily.GetProperty("time").EnumerateArray().Select(x => DateTime.Parse(x.GetString()!, Inv)).ToList();
        var hi = daily.GetProperty("temperature_2m_max").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var lo = daily.GetProperty("temperature_2m_min").EnumerateArray().Select(x => x.GetDouble()).ToList();
        var codes = daily.GetProperty("weather_code").EnumerateArray().Select(x => x.GetInt32()).ToList();
        var days = new List<Day>();
        for (int i = 0; i < times.Count && i < hi.Count && i < lo.Count && i < codes.Count; i++) days.Add(new Day(times[i], hi[i], lo[i], codes[i]));
        return new Wx(name, cur.GetProperty("temperature_2m").GetDouble(), cur.GetProperty("apparent_temperature").GetDouble(),
            (int)cur.GetProperty("relative_humidity_2m").GetDouble(), cur.GetProperty("wind_speed_10m").GetDouble(), cur.GetProperty("weather_code").GetInt32(), days);
    }

    public static string Icon(int code) => code switch
    {
        0 => "☀", 1 => "🌤", 2 => "⛅", 3 => "☁", 45 or 48 => "🌫",
        51 or 53 or 55 or 56 or 57 => "🌦", 61 or 63 or 65 or 66 or 67 or 80 or 81 or 82 => "🌧",
        71 or 73 or 75 or 77 or 85 or 86 => "❄", 95 or 96 or 99 => "⛈", _ => "🌡"
    };
}

/// <summary>Crypto prices and weather in one window. What you choose here is also what the ticker bar shows.</summary>
internal sealed class CryptoTool : Window
{
    private readonly Ui.Chips _tabs = new(new[] { "Crypto", "Weather" }, 0);
    private readonly StackPanel _cp = new(), _wp = new();
    private readonly StackPanel _coins = new(), _found = new(), _wx = new();
    private readonly Ui.Input _add = new("Add a coin (name or symbol)", false, 0);
    private readonly Ui.Input _city = new("City (empty = my location)", false, 0);
    private Ui.Chips _vs = null!, _unit = null!;
    private readonly Ui.Status _st = new();
    private static readonly string[] Cur = { "usd", "eur", "gbp", "pkr", "inr" };
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(60) };
    private List<string> _ids;
    private string _vsKey;

    public CryptoTool()
    {
        Ui.Setup(this, "Weather & Crypto", 540, 700, true);
        _ids = Pref.Get("coins", "bitcoin,ethereum,solana").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
        _vsKey = Pref.Get("vs", "usd");
        if (!Cur.Contains(_vsKey)) _vsKey = "usd";

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(_tabs);

        _vs = new Ui.Chips(Cur.Select(c => c.ToUpperInvariant()).ToArray(), Array.IndexOf(Cur, _vsKey));
        _cp.Children.Add(_vs);
        _add.Margin = new Thickness(0, 0, 0, 8);
        _cp.Children.Add(_add);
        _found.Margin = new Thickness(0, 0, 0, 8);
        _cp.Children.Add(_found);
        _cp.Children.Add(_coins);
        root.Children.Add(_cp);

        _wp.Visibility = Visibility.Collapsed;
        _city.Text = Pref.Get("city", "");
        _city.Margin = new Thickness(0, 0, 0, 8);
        _unit = new Ui.Chips(new[] { "°C", "°F" }, Pref.Get("unit", "C") == "F" ? 1 : 0);
        _wp.Children.Add(_city);
        _wp.Children.Add(_unit);
        _wp.Children.Add(Ui.Row(Ui.Btn("Show weather", () => { Pref.Set("city", _city.Text.Trim()); _ = LoadWeatherAsync(); }, true)));
        _wx.Margin = new Thickness(0, 12, 0, 0);
        _wp.Children.Add(_wx);
        root.Children.Add(_wp);

        _st.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_st);
        root.Children.Add(Ui.Row(Ui.Btn("Show / hide the top bar ticker", () => ToolHost.Launch("ticker"))));
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _tabs.Selected += i =>
        {
            _cp.Visibility = i == 0 ? Visibility.Visible : Visibility.Collapsed;
            _wp.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            if (i == 1 && _wx.Children.Count == 0) _ = LoadWeatherAsync();
        };
        _vs.Selected += i => { _vsKey = Cur[i]; Pref.Set("vs", _vsKey); _ = LoadCoinsAsync(); };
        _unit.Selected += i => { Pref.Set("unit", i == 1 ? "F" : "C"); _ = LoadWeatherAsync(); };
        _add.Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) _ = SearchAsync(); };
        _city.Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Pref.Set("city", _city.Text.Trim()); _ = LoadWeatherAsync(); } };
        _t.Tick += (_, _) => { if (_tabs.Index == 0) _ = LoadCoinsAsync(); };
        Closed += (_, _) => _t.Stop();
        Loaded += (_, _) => { _ = LoadCoinsAsync(); _t.Start(); };
    }

    private async Task LoadCoinsAsync()
    {
        _coins.Children.Clear();
        if (_ids.Count == 0) { _coins.Children.Add(Ui.Label("Add a coin above.", 13, true)); return; }
        _st.Info("Loading prices...");
        try
        {
            var l = await Market.CoinsAsync(string.Join(",", _ids), _vsKey);
            _coins.Children.Clear();
            foreach (var c in l)
            {
                var coin = c;
                var g = new Grid();
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                var n = new StackPanel();
                n.Children.Add(Ui.Label(c.Symbol, 15, false, true));
                n.Children.Add(Ui.Label(c.Name, 12, true));
                g.Children.Add(n);
                var pr = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
                pr.Children.Add(Ui.Label(Market.Price(c.Price, _vsKey), 15, false, true));
                if (c.Change != null)
                {
                    var ch = Ui.Label((c.Change >= 0 ? "▲ " : "▼ ") + Math.Abs(c.Change.Value).ToString("0.00", CultureInfo.InvariantCulture) + "% (24h)", 12);
                    ch.Foreground = c.Change >= 0 ? Ui.Good : Ui.Bad;
                    ch.HorizontalAlignment = HorizontalAlignment.Right;
                    pr.Children.Add(ch);
                }
                Grid.SetColumn(pr, 1);
                g.Children.Add(pr);
                var rm = Ui.Btn("✕", () => { _ids.Remove(coin.Id); Pref.Set("coins", string.Join(",", _ids)); _ = LoadCoinsAsync(); });
                rm.Padding = new Thickness(9, 5, 9, 5);
                rm.Margin = new Thickness(0);
                Grid.SetColumn(rm, 2);
                g.Children.Add(rm);
                var card = Ui.Card(g, 12);
                card.Margin = new Thickness(0, 0, 0, 8);
                _coins.Children.Add(card);
            }
            _st.Info("Prices by CoinGecko, updated " + DateTime.Now.ToString("HH:mm"));
        }
        catch (Exception ex)
        {
            _st.Say(ex.Message == "limit" ? "CoinGecko says too many requests. Wait a minute and try again." : "Could not load prices. Check your internet connection.", true, 6000);
        }
    }

    private async Task SearchAsync()
    {
        string q = _add.Text.Trim();
        if (q.Length == 0) return;
        _found.Children.Clear();
        try
        {
            var r = await Market.SearchCoinsAsync(q);
            if (r.Count == 0) { _found.Children.Add(Ui.Label("No coin found.", 12.5, true)); return; }
            var row = new WrapPanel();
            foreach (var c in r)
            {
                var it = c;
                row.Children.Add(Ui.Btn(c.Symbol + "  " + c.Name, () =>
                {
                    if (!_ids.Contains(it.Id) && _ids.Count < 12) _ids.Add(it.Id);
                    Pref.Set("coins", string.Join(",", _ids));
                    _found.Children.Clear();
                    _add.Text = "";
                    _ = LoadCoinsAsync();
                }));
            }
            _found.Children.Add(row);
        }
        catch (Exception ex) { _st.Say(ex.Message == "limit" ? "Too many requests, wait a minute." : "Search failed. Check your internet connection.", true, 5000); }
    }

    private async Task LoadWeatherAsync()
    {
        _wx.Children.Clear();
        _st.Info("Loading weather...");
        bool f = _unit.Index == 1;
        string u = f ? "°F" : "°C";
        try
        {
            var w = await Market.WeatherAsync(_city.Text.Trim(), f);
            if (w == null) { _st.Say("City not found.", true); return; }
            var head = new StackPanel();
            head.Children.Add(Ui.Label(w.City, 14, true));
            head.Children.Add(Ui.Label(Market.Icon(w.Code) + " " + Math.Round(w.Temp) + u, 38, false, true));
            head.Children.Add(Ui.Label(WeatherService.Describe(w.Code) + "  -  feels like " + Math.Round(w.Feels) + u + "  -  humidity " + w.Humidity + "%  -  wind " + Math.Round(w.Wind) + " km/h", 12.5, true));
            _wx.Children.Add(Ui.Card(head, 16));
            var days = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            foreach (var d in w.Days)
            {
                var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                g.ColumnDefinitions.Add(new ColumnDefinition());
                g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                g.Children.Add(Ui.Label(d.Date.ToString("ddd d", CultureInfo.InvariantCulture), 13, true));
                var ds = Ui.Label(Market.Icon(d.Code) + " " + WeatherService.Describe(d.Code), 13);
                Grid.SetColumn(ds, 1);
                g.Children.Add(ds);
                var hl = Ui.Label(Math.Round(d.Hi) + "° / " + Math.Round(d.Lo) + "°", 13, false, true);
                Grid.SetColumn(hl, 2);
                g.Children.Add(hl);
                days.Children.Add(g);
            }
            _wx.Children.Add(days);
            _st.Info("Weather by Open-Meteo");
        }
        catch { _st.Say("Could not load the weather. Check your internet connection.", true, 5000); }
    }
}

/// <summary>A slim always-on-top bar with the weather and your coins. Start it again to close it.</summary>
internal sealed class TickerWindow : Window
{
    private readonly TextBlock _tx = new() { Foreground = Ui.Text, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
    private readonly DispatcherTimer _t = new() { Interval = TimeSpan.FromSeconds(90) };
    private static Mutex? _mutex;
    private static EventWaitHandle? _quit;
    private int _loading;
    private string _weather = "";
    private string _coins = "";
    private int _n;

    /// <summary>Returns null (and closes the running bar) when a ticker is already running.</summary>
    public static Window? Create()
    {
        _mutex = new Mutex(true, "LightIslandLab.Ticker", out bool created);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "LightIslandLab.Ticker.Quit");
        if (!created)
        {
            _quit.Set();
            return null;
        }
        return new TickerWindow();
    }

    private TickerWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        ShowActivated = false;
        Title = "LightIsland Lab Ticker";

        var bd = new Border
        {
            Background = Ui.Solid(Ui.Light ? Color.FromArgb(235, 250, 250, 252) : Color.FromArgb(235, 18, 19, 22)),
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(16, 7, 16, 7),
            Child = _tx
        };
        _tx.Text = "Loading...";
        Content = bd;

        var menu = new ContextMenu();
        var refresh = new MenuItem { Header = "Refresh" };
        refresh.Click += (_, _) => _ = RefreshAsync();
        var set = new MenuItem { Header = "Choose coins and city..." };
        set.Click += (_, _) => ToolHost.Launch("crypto");
        var close = new MenuItem { Header = "Close ticker" };
        close.Click += (_, _) => Close();
        menu.Items.Add(refresh);
        menu.Items.Add(set);
        menu.Items.Add(close);
        bd.ContextMenu = menu;
        bd.MouseLeftButtonDown += (_, e) => { try { DragMove(); } catch { } };
        LocationChanged += (_, _) => { if (IsLoaded) { Pref.Set("ticker.x", Left.ToString(CultureInfo.InvariantCulture)); Pref.Set("ticker.y", Top.ToString(CultureInfo.InvariantCulture)); } };

        Loaded += (_, _) =>
        {
            Place();
            _ = RefreshAsync();
            _t.Start();
            var th = new Thread(() => { try { _quit!.WaitOne(); Dispatcher.BeginInvoke(new Action(Close)); } catch { } }) { IsBackground = true };
            th.Start();
        };
        SizeChanged += (_, _) => { if (IsLoaded && Pref.Get("ticker.x", "") == "") Place(); };
        _t.Tick += (_, _) => _ = RefreshAsync();
        Closed += (_, _) => { _t.Stop(); try { _mutex?.ReleaseMutex(); } catch { } };
    }

    private void Place()
    {
        var wa = SystemParameters.WorkArea;
        if (double.TryParse(Pref.Get("ticker.x", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double x) &&
            double.TryParse(Pref.Get("ticker.y", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
        {
            Left = Math.Clamp(x, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 80);
            Top = Math.Clamp(y, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40);
        }
        else
        {
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Bottom - ActualHeight - 12;
        }
    }

    private async Task RefreshAsync()
    {
        if (Interlocked.Exchange(ref _loading, 1) == 1) return;
        try
        {
            bool f = Pref.Get("unit", "C") == "F";
            string vs = Pref.Get("vs", "usd");
            try
            {
                var w = await Market.WeatherAsync(Pref.Get("city", ""), f);
                if (w != null) _weather = Market.Icon(w.Code) + " " + Math.Round(w.Temp) + (f ? "°F " : "°C ") + w.City;
            }
            catch { }
            try
            {
                string ids = Pref.Get("coins", "bitcoin,ethereum,solana");
                var l = await Market.CoinsAsync(ids, vs);
                _coins = string.Join("   ", l.Select(c => c.Symbol + " " + Market.Price(c.Price, vs) + (c.Change != null ? (c.Change >= 0 ? " ▲" : " ▼") + Math.Abs(c.Change.Value).ToString("0.0", CultureInfo.InvariantCulture) + "%" : "")));
            }
            catch { }
            string all = string.Join("   |   ", new[] { _weather, _coins }.Where(s => s.Length > 0));
            _tx.Text = all.Length > 0 ? all : "No connection";
            if (IsLoaded && Pref.Get("ticker.x", "") == "") Place();
        }
        finally { Interlocked.Exchange(ref _loading, 0); _n++; }
    }
}
