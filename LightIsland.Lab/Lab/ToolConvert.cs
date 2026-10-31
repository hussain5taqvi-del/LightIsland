using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightIsland;

/// <summary>Units and live currency rates. Rates come from open.er-api.com and are cached for a day.</summary>
internal sealed class ConvertTool : Window
{
    private sealed record Unit(string Key, string Name, double Factor, Func<double, double>? To = null, Func<double, double>? From = null);

    private static readonly Dictionary<string, Unit[]> Cats = new()
    {
        ["Length"] = new[] { new Unit("m", "Meter (m)", 1), new Unit("km", "Kilometer (km)", 1000), new Unit("cm", "Centimeter (cm)", 0.01), new Unit("mm", "Millimeter (mm)", 0.001), new Unit("mi", "Mile (mi)", 1609.344), new Unit("yd", "Yard (yd)", 0.9144), new Unit("ft", "Foot (ft)", 0.3048), new Unit("in", "Inch (in)", 0.0254), new Unit("nmi", "Nautical mile", 1852) },
        ["Weight"] = new[] { new Unit("kg", "Kilogram (kg)", 1), new Unit("g", "Gram (g)", 0.001), new Unit("mg", "Milligram (mg)", 0.000001), new Unit("t", "Tonne (t)", 1000), new Unit("lb", "Pound (lb)", 0.45359237), new Unit("oz", "Ounce (oz)", 0.028349523125), new Unit("st", "Stone (st)", 6.35029318) },
        ["Temperature"] = new[] { new Unit("C", "Celsius (°C)", 1, v => v, v => v), new Unit("F", "Fahrenheit (°F)", 1, v => (v - 32) * 5 / 9, v => v * 9 / 5 + 32), new Unit("K", "Kelvin (K)", 1, v => v - 273.15, v => v + 273.15) },
        ["Volume"] = new[] { new Unit("l", "Liter (L)", 1), new Unit("ml", "Milliliter (mL)", 0.001), new Unit("m3", "Cubic meter (m³)", 1000), new Unit("gal", "US gallon", 3.785411784), new Unit("qt", "US quart", 0.946352946), new Unit("cup", "US cup", 0.2365882365), new Unit("floz", "US fluid ounce", 0.0295735295625), new Unit("tbsp", "Tablespoon (US)", 0.01478676478125), new Unit("tsp", "Teaspoon (US)", 0.00492892159375) },
        ["Area"] = new[] { new Unit("m2", "Square meter (m²)", 1), new Unit("km2", "Square kilometer", 1000000), new Unit("ha", "Hectare (ha)", 10000), new Unit("ac", "Acre", 4046.8564224), new Unit("ft2", "Square foot", 0.09290304), new Unit("in2", "Square inch", 0.00064516), new Unit("mi2", "Square mile", 2589988.110336) },
        ["Speed"] = new[] { new Unit("kmh", "km/h", 1), new Unit("ms", "m/s", 3.6), new Unit("mph", "mph", 1.609344), new Unit("kn", "Knot", 1.852), new Unit("fts", "ft/s", 1.09728) },
        ["Data"] = new[] { new Unit("B", "Byte (B)", 1), new Unit("KB", "Kilobyte (KB)", 1000), new Unit("MB", "Megabyte (MB)", 1e6), new Unit("GB", "Gigabyte (GB)", 1e9), new Unit("TB", "Terabyte (TB)", 1e12), new Unit("KiB", "Kibibyte (KiB)", 1024), new Unit("MiB", "Mebibyte (MiB)", 1048576), new Unit("GiB", "Gibibyte (GiB)", 1073741824), new Unit("bit", "Bit", 0.125), new Unit("Mbit", "Megabit (Mb)", 125000) },
        ["Time"] = new[] { new Unit("s", "Second", 1), new Unit("min", "Minute", 60), new Unit("h", "Hour", 3600), new Unit("d", "Day", 86400), new Unit("wk", "Week", 604800), new Unit("ms", "Millisecond", 0.001), new Unit("yr", "Year (365 d)", 31536000) },
    };

    private static readonly Dictionary<string, string> CurNames = new()
    {
        ["USD"] = "US Dollar", ["EUR"] = "Euro", ["GBP"] = "British Pound", ["PKR"] = "Pakistani Rupee", ["INR"] = "Indian Rupee", ["AED"] = "UAE Dirham", ["SAR"] = "Saudi Riyal",
        ["JPY"] = "Japanese Yen", ["CNY"] = "Chinese Yuan", ["CAD"] = "Canadian Dollar", ["AUD"] = "Australian Dollar", ["CHF"] = "Swiss Franc", ["TRY"] = "Turkish Lira",
        ["BDT"] = "Bangladeshi Taka", ["KWD"] = "Kuwaiti Dinar", ["QAR"] = "Qatari Riyal", ["OMR"] = "Omani Rial", ["BHD"] = "Bahraini Dinar", ["EGP"] = "Egyptian Pound",
        ["MYR"] = "Malaysian Ringgit", ["SGD"] = "Singapore Dollar", ["RUB"] = "Russian Ruble", ["BRL"] = "Brazilian Real", ["MXN"] = "Mexican Peso", ["ZAR"] = "South African Rand",
        ["KRW"] = "South Korean Won", ["IDR"] = "Indonesian Rupiah", ["THB"] = "Thai Baht", ["NZD"] = "New Zealand Dollar", ["SEK"] = "Swedish Krona", ["NOK"] = "Norwegian Krone",
        ["DKK"] = "Danish Krone", ["PLN"] = "Polish Zloty", ["HKD"] = "Hong Kong Dollar", ["NGN"] = "Nigerian Naira", ["LKR"] = "Sri Lankan Rupee", ["AFN"] = "Afghan Afghani", ["IRR"] = "Iranian Rial"
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private readonly Ui.Chips _cats;
    private readonly Ui.Input _in = new("0", false, 0, 22);
    private readonly TextBox _out = new();
    private Ui.Picker _from = null!, _to = null!;
    private readonly StackPanel _holder = new(), _all = new();
    private readonly Ui.Status _st = new();
    private Dictionary<string, double>? _rates;
    private string _ratesInfo = "";
    private string _cat;

    public ConvertTool()
    {
        Ui.Setup(this, "Converter", 520, 700, true);
        var names = Cats.Keys.Concat(new[] { "Currency" }).ToArray();
        _cat = Pref.Get("conv.cat", "Length");
        if (!names.Contains(_cat)) _cat = "Length";
        _cats = new Ui.Chips(names, Array.IndexOf(names, _cat));

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(_cats);

        _in.Margin = new Thickness(0, 4, 0, 8);
        _in.Box.FontWeight = FontWeights.SemiBold;
        root.Children.Add(_in);
        root.Children.Add(_holder);

        _out.IsReadOnly = true;
        _out.Background = Ui.Panel;
        _out.Foreground = Ui.Text;
        _out.BorderBrush = Ui.Line;
        _out.Padding = new Thickness(10, 8, 10, 8);
        _out.FontSize = 22;
        _out.FontWeight = FontWeights.SemiBold;
        _out.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(_out);
        root.Children.Add(Ui.Row(Ui.Btn("Copy result", () => { if (_out.Text.Length > 0) { Ui.Copy(_out.Text); _st.Say("Copied"); } }, true), Ui.Btn("Swap", Swap)));
        _st.Margin = new Thickness(0, 8, 0, 8);
        root.Children.Add(_st);
        root.Children.Add(Ui.Label("All units", 12, true, true));
        _all.Margin = new Thickness(0, 6, 0, 0);
        root.Children.Add(_all);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _cats.Selected += i => { _cat = names[i]; Pref.Set("conv.cat", _cat); _ = BuildAsync(); };
        _in.Changed += Calc;
        _in.Text = Pref.Get("conv.val", "1");
        Loaded += (_, _) => { _in.Focus(); _in.Box.SelectAll(); };
        _ = BuildAsync();
    }

    private List<(string Key, string Name)> Items()
    {
        if (_cat == "Currency")
            return (_rates?.Keys ?? Enumerable.Empty<string>()).OrderBy(k => CurNames.ContainsKey(k) ? 0 : 1).ThenBy(k => k)
                .Select(k => (k, CurNames.TryGetValue(k, out string? n) ? k + " - " + n : k)).ToList();
        return Cats[_cat].Select(u => (u.Key, u.Name)).ToList();
    }

    private async Task BuildAsync()
    {
        _holder.Children.Clear();
        _all.Children.Clear();
        _out.Text = "";
        if (_cat == "Currency")
        {
            _st.Info("Loading rates...");
            await LoadRatesAsync();
            if (_rates == null) { _st.Say("Rates need internet and none are saved yet.", true, 0); return; }
            _st.Info(_ratesInfo);
        }
        else _st.Text = "";

        var items = Items();
        string def1 = _cat == "Currency" ? "USD" : items[0].Key;
        string def2 = _cat == "Currency" ? (_rates!.ContainsKey("PKR") ? "PKR" : "EUR") : items[Math.Min(1, items.Count - 1)].Key;
        string k1 = Pref.Get("conv.f." + _cat, def1), k2 = Pref.Get("conv.t." + _cat, def2);
        if (!items.Any(i => i.Key == k1)) k1 = def1;
        if (!items.Any(i => i.Key == k2)) k2 = def2;
        _from = new Ui.Picker(items, k1);
        _to = new Ui.Picker(items, k2);
        _from.Changed += () => { Pref.Set("conv.f." + _cat, _from.Key); Calc(); };
        _to.Changed += () => { Pref.Set("conv.t." + _cat, _to.Key); Calc(); };
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(_from);
        var arrow = Ui.Label("→", 16, true);
        arrow.HorizontalAlignment = HorizontalAlignment.Center;
        Grid.SetColumn(arrow, 1);
        g.Children.Add(arrow);
        Grid.SetColumn(_to, 2);
        g.Children.Add(_to);
        _holder.Children.Add(g);
        Calc();
    }

    private async Task LoadRatesAsync()
    {
        string cache = ToolHost.DataFile("rates.json");
        try
        {
            if (File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < TimeSpan.FromHours(12))
            {
                if (ParseRates(File.ReadAllText(cache), "Rates updated " + File.GetLastWriteTime(cache).ToString("d MMM, HH:mm", CultureInfo.InvariantCulture))) return;
            }
        }
        catch { }
        try
        {
            string json = await Http.GetStringAsync("https://open.er-api.com/v6/latest/USD");
            if (ParseRates(json, "Live rates"))
            {
                try { File.WriteAllText(cache, json); } catch { }
                return;
            }
        }
        catch { }
        try
        {
            if (File.Exists(cache)) ParseRates(File.ReadAllText(cache), "Offline: rates from " + File.GetLastWriteTime(cache).ToString("d MMM, HH:mm", CultureInfo.InvariantCulture));
        }
        catch { }
    }

    private bool ParseRates(string json, string info)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("rates", out var r)) return false;
            var d = new Dictionary<string, double>();
            foreach (var p in r.EnumerateObject()) if (p.Value.ValueKind == JsonValueKind.Number) d[p.Name] = p.Value.GetDouble();
            if (d.Count == 0) return false;
            _rates = d;
            _ratesInfo = info;
            return true;
        }
        catch { return false; }
    }

    private void Swap()
    {
        if (_from == null) return;
        string a = _from.Key, b = _to.Key;
        _from.Set(b);
        _to.Set(a);
        Pref.Set("conv.f." + _cat, b);
        Pref.Set("conv.t." + _cat, a);
        Calc();
    }

    private static bool TryNum(string s, out double v)
    {
        s = s.Trim().Replace(" ", "");
        if (s.Contains(',') && !s.Contains('.')) s = s.Replace(',', '.');
        else s = s.Replace(",", "");
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v) && !double.IsNaN(v) && !double.IsInfinity(v);
    }

    private static string Fmt(double v)
    {
        if (v == 0) return "0";
        double a = Math.Abs(v);
        if (a >= 1e15 || a < 1e-7) return v.ToString("0.#####E+0", CultureInfo.InvariantCulture);
        int mag = (int)Math.Floor(Math.Log10(a));
        int dec = Math.Clamp(8 - mag, 0, 10);
        return Math.Round(v, dec).ToString("0." + new string('#', Math.Max(dec, 1)), CultureInfo.InvariantCulture);
    }

    private double? Convert(double v, string from, string to)
    {
        if (_cat == "Currency")
        {
            if (_rates == null || !_rates.TryGetValue(from, out double rf) || !_rates.TryGetValue(to, out double rt) || rf == 0) return null;
            return v / rf * rt;
        }
        var us = Cats[_cat];
        var f = us.First(u => u.Key == from);
        var t = us.First(u => u.Key == to);
        double bas = f.To != null ? f.To(v) : v * f.Factor;
        return t.From != null ? t.From(bas) : bas / t.Factor;
    }

    private void Calc()
    {
        if (_from == null) return;
        Pref.Set("conv.val", _in.Text);
        _all.Children.Clear();
        if (!TryNum(_in.Text, out double v)) { _out.Text = ""; return; }
        double? r = Convert(v, _from.Key, _to.Key);
        _out.Text = r == null ? "" : Fmt(r.Value);

        var list = _cat == "Currency"
            ? Items().Where(i => CurNames.ContainsKey(i.Key)).Take(14).ToList()
            : Items();
        foreach (var it in list)
        {
            double? x = Convert(v, _from.Key, it.Key);
            if (x == null) continue;
            string txt = Fmt(x.Value);
            var g = new Grid { Margin = new Thickness(0, 0, 0, 2), Background = Brushes.Transparent, Cursor = Cursors.Hand };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Ui.Label(it.Name, 12.5, true));
            var val = Ui.Label(txt, 13, false, true);
            Grid.SetColumn(val, 1);
            g.Children.Add(val);
            g.ToolTip = "Click to copy";
            g.MouseLeftButtonUp += (_, _) => { Ui.Copy(txt); _st.Say("Copied " + txt); };
            _all.Children.Add(g);
        }
    }
}
