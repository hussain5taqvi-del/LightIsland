using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightIsland;

/// <summary>Screen color picker (magnifier) + palette generator.</summary>
internal sealed class ColorTool : Window
{
    private Color _c = Color.FromRgb(0x38, 0xBD, 0xF8);
    private readonly Border _big = new() { Height = 96, CornerRadius = new CornerRadius(14) };
    private readonly StackPanel _vals = new();
    private readonly WrapPanel _pal = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly WrapPanel _hist = new();
    private readonly Ui.Input _hex = new("#RRGGBB", false, 0);
    private readonly Ui.Status _st = new();
    private Ui.Chips _scheme = null!;
    private readonly List<string> _history = new();
    private static string File_ => ToolHost.DataFile("colors.json");

    public ColorTool(string arg)
    {
        Ui.Setup(this, "Color picker", 520, 760, true);
        LoadHistory();

        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(Ui.Row(Ui.Btn("Pick from screen", () => _ = PickAsync(), true), Ui.Btn("Paste", PasteColor)));
        _big.Margin = new Thickness(0, 12, 0, 10);
        root.Children.Add(_big);

        var hexRow = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        hexRow.ColumnDefinitions.Add(new ColumnDefinition());
        hexRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        hexRow.Children.Add(_hex);
        var set = Ui.Btn("Set", () => SetFromText(_hex.Text));
        set.Margin = new Thickness(8, 0, 0, 0);
        Grid.SetColumn(set, 1);
        hexRow.Children.Add(set);
        root.Children.Add(hexRow);

        root.Children.Add(Ui.Card(_vals, 8));

        var pt = Ui.Label("Palette", 15, false, true);
        pt.Margin = new Thickness(0, 16, 0, 6);
        root.Children.Add(pt);
        _scheme = new Ui.Chips(new[] { "Complementary", "Analogous", "Triadic", "Split", "Shades", "Tints" }, 1);
        _scheme.Selected += _ => BuildPalette();
        root.Children.Add(_scheme);
        root.Children.Add(_pal);
        root.Children.Add(Ui.Row(Ui.Btn("Copy palette as CSS", CopyCss), Ui.Btn("Copy HEX list", CopyHexList)));

        var ht = Ui.Label("Recent colors", 15, false, true);
        ht.Margin = new Thickness(0, 14, 0, 6);
        root.Children.Add(ht);
        root.Children.Add(_hist);
        _st.Margin = new Thickness(0, 8, 0, 0);
        root.Children.Add(_st);

        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };
        Refresh();
        _hex.Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) SetFromText(_hex.Text); };
        Loaded += (_, _) => { if (arg == "pick") _ = PickAsync(); };
    }

    private async Task PickAsync()
    {
        WindowState = WindowState.Minimized;
        await Task.Delay(350);
        PickOverlay? o = null;
        try { o = PickOverlay.Run(true, "Click a pixel (arrow keys move 1 px)"); }
        finally { WindowState = WindowState.Normal; Activate(); }
        if (o?.PickedColor is Color c)
        {
            _c = c;
            AddHistory(c);
            Refresh();
            Ui.Copy(Ui.Hex(c));
            _st.Say(Ui.Hex(c) + " copied");
        }
    }

    private void PasteColor()
    {
        try { if (Clipboard.ContainsText()) SetFromText(Clipboard.GetText()); }
        catch { }
    }

    private void SetFromText(string t)
    {
        if (TryParse(t, out Color c)) { _c = c; AddHistory(c); Refresh(); }
        else _st.Say("Not a color. Use #RRGGBB, rgb(12,34,56) or hsl(200,80%,50%).", true);
    }

    public static bool TryParse(string t, out Color c)
    {
        c = default;
        t = (t ?? "").Trim();
        if (t.Length == 0) return false;
        try
        {
            if (t.StartsWith("rgb", StringComparison.OrdinalIgnoreCase))
            {
                var n = System.Text.RegularExpressions.Regex.Matches(t, @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
                if (n.Length < 3) return false;
                c = Color.FromRgb((byte)Math.Clamp(n[0], 0, 255), (byte)Math.Clamp(n[1], 0, 255), (byte)Math.Clamp(n[2], 0, 255));
                return true;
            }
            if (t.StartsWith("hsl", StringComparison.OrdinalIgnoreCase))
            {
                var n = System.Text.RegularExpressions.Regex.Matches(t, @"\d+(\.\d+)?").Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
                if (n.Length < 3) return false;
                c = FromHsl(n[0], n[1] / 100.0, n[2] / 100.0);
                return true;
            }
            string h = t.TrimStart('#');
            if (h.Length == 3) h = string.Concat(h.Select(ch => new string(ch, 2)));
            if (h.Length == 8) h = h.Substring(0, 6);
            if (h.Length != 6 || !h.All(Uri.IsHexDigit)) return false;
            c = Color.FromRgb(Convert.ToByte(h.Substring(0, 2), 16), Convert.ToByte(h.Substring(2, 2), 16), Convert.ToByte(h.Substring(4, 2), 16));
            return true;
        }
        catch { return false; }
    }

    // ----- color math -----
    public static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
        double l = (mx + mn) / 2, h = 0, s = 0, d = mx - mn;
        if (d > 1e-9)
        {
            s = l > 0.5 ? d / (2 - mx - mn) : d / (mx + mn);
            if (mx == r) h = ((g - b) / d + (g < b ? 6 : 0));
            else if (mx == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h *= 60;
        }
        return (h, s, l);
    }

    public static Color FromHsl(double h, double s, double l)
    {
        h = ((h % 360) + 360) % 360;
        s = Math.Clamp(s, 0, 1);
        l = Math.Clamp(l, 0, 1);
        double c = (1 - Math.Abs(2 * l - 1)) * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        double r = 0, g = 0, b = 0;
        if (h < 60) { r = c; g = x; }
        else if (h < 120) { r = x; g = c; }
        else if (h < 180) { g = c; b = x; }
        else if (h < 240) { g = x; b = c; }
        else if (h < 300) { r = x; b = c; }
        else { r = c; b = x; }
        return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
    }

    private List<Color> Palette()
    {
        var (h, s, l) = ToHsl(_c);
        var list = new List<Color>();
        switch (_scheme.Index)
        {
            case 0: list.Add(_c); list.Add(FromHsl(h + 180, s, l)); list.Add(FromHsl(h, s, Math.Min(0.9, l + 0.2))); list.Add(FromHsl(h + 180, s, Math.Min(0.9, l + 0.2))); list.Add(FromHsl(h, s, Math.Max(0.1, l - 0.2))); break;
            case 1: for (int i = -2; i <= 2; i++) list.Add(FromHsl(h + i * 30, s, l)); break;
            case 2: list.Add(_c); list.Add(FromHsl(h + 120, s, l)); list.Add(FromHsl(h + 240, s, l)); break;
            case 3: list.Add(_c); list.Add(FromHsl(h + 150, s, l)); list.Add(FromHsl(h + 210, s, l)); break;
            case 4: for (int i = 0; i < 6; i++) list.Add(FromHsl(h, s, l * (1 - i * 0.14))); break;
            default: for (int i = 0; i < 6; i++) list.Add(FromHsl(h, s, l + (1 - l) * i * 0.17)); break;
        }
        return list;
    }

    private void BuildPalette()
    {
        _pal.Children.Clear();
        foreach (Color c in Palette())
        {
            Color cc = c;
            var sw = Ui.Swatch(cc, 72, 72, 12);
            sw.Margin = new Thickness(0, 0, 8, 8);
            sw.Cursor = Cursors.Hand;
            sw.ToolTip = Ui.Hex(cc);
            sw.Child = new TextBlock { Text = Ui.Hex(cc), FontSize = 10.5, FontFamily = Ui.Mono, Foreground = Lum(cc) > 0.55 ? System.Windows.Media.Brushes.Black : System.Windows.Media.Brushes.White, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 5) };
            sw.MouseLeftButtonUp += (_, _) => { Ui.Copy(Ui.Hex(cc)); _st.Say(Ui.Hex(cc) + " copied"); };
            sw.MouseRightButtonUp += (_, _) => { _c = cc; AddHistory(cc); Refresh(); };
            _pal.Children.Add(sw);
        }
    }

    private static double Lum(Color c) => (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;

    private void Refresh()
    {
        _big.Background = Ui.Solid(_c);
        _big.BorderBrush = Ui.Line;
        _big.BorderThickness = new Thickness(1);
        _hex.Box.Text = "";
        var (h, s, l) = ToHsl(_c);
        _vals.Children.Clear();
        AddVal("HEX", Ui.Hex(_c));
        AddVal("RGB", $"rgb({_c.R}, {_c.G}, {_c.B})");
        AddVal("HSL", $"hsl({Math.Round(h)}, {Math.Round(s * 100)}%, {Math.Round(l * 100)}%)");
        AddVal("Contrast", $"{ContrastWith(_c, Colors.White):0.0}:1 on white   {ContrastWith(_c, Colors.Black):0.0}:1 on black");
        BuildPalette();
        BuildHistory();
    }

    private static double RelLum(Color c)
    {
        double F(byte v) { double x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        return 0.2126 * F(c.R) + 0.7152 * F(c.G) + 0.0722 * F(c.B);
    }

    private static double ContrastWith(Color a, Color b)
    {
        double l1 = RelLum(a), l2 = RelLum(b);
        return (Math.Max(l1, l2) + 0.05) / (Math.Min(l1, l2) + 0.05);
    }

    private void AddVal(string name, string val)
    {
        var g = new Grid { Margin = new Thickness(4, 4, 4, 4), Cursor = Cursors.Hand, Background = System.Windows.Media.Brushes.Transparent };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Label(name, 12, true));
        var v = new TextBlock { Text = val, FontFamily = Ui.Mono, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(v, 1);
        g.Children.Add(v);
        var cp = Ui.Label("Copy", 12, true);
        Grid.SetColumn(cp, 2);
        g.Children.Add(cp);
        if (name != "Contrast") g.MouseLeftButtonUp += (_, _) => { Ui.Copy(val); _st.Say(name + " copied"); };
        _vals.Children.Add(g);
    }

    private void CopyCss()
    {
        var l = Palette();
        string css = ":root {\n" + string.Join("\n", l.Select((c, i) => $"  --color-{i + 1}: {Ui.Hex(c)};")) + "\n}";
        Ui.Copy(css);
        _st.Say("CSS copied");
    }

    private void CopyHexList() { Ui.Copy(string.Join("\n", Palette().Select(Ui.Hex))); _st.Say("Colors copied"); }

    // ----- history -----
    private void LoadHistory()
    {
        try
        {
            if (File.Exists(File_)) _history.AddRange(JsonSerializer.Deserialize<List<string>>(File.ReadAllText(File_)) ?? new List<string>());
        }
        catch { }
    }

    private void AddHistory(Color c)
    {
        string h = Ui.Hex(c);
        _history.Remove(h);
        _history.Insert(0, h);
        if (_history.Count > 18) _history.RemoveRange(18, _history.Count - 18);
        try { File.WriteAllText(File_, JsonSerializer.Serialize(_history)); } catch { }
    }

    private void BuildHistory()
    {
        _hist.Children.Clear();
        foreach (string h in _history)
        {
            if (!TryParse(h, out Color c)) continue;
            var sw = Ui.Swatch(c, 34, 34, 9);
            sw.Margin = new Thickness(0, 0, 6, 6);
            sw.Cursor = Cursors.Hand;
            sw.ToolTip = h;
            sw.MouseLeftButtonUp += (_, _) => { _c = c; Refresh(); };
            _hist.Children.Add(sw);
        }
        if (_history.Count == 0) _hist.Children.Add(Ui.Label("Colors you pick show up here.", 12, true));
    }
}
