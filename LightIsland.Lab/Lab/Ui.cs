using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;

namespace LightIsland;

/// <summary>Small code-built UI kit for the tool windows (dark / light like the island, accent color from the settings).</summary>
internal static class Ui
{
    public static bool Light;
    public static Color AccentC = Color.FromRgb(0x38, 0xBD, 0xF8);
    public static Brush Bg = Brushes.Black, Panel = Brushes.Black, Panel2 = Brushes.Black, Text = Brushes.White, Dim = Brushes.Gray,
                        Line = Brushes.Gray, Accent = Brushes.DodgerBlue, OnAccent = Brushes.White, Good = Brushes.LimeGreen, Bad = Brushes.Red,
                        Hover = Brushes.Gray;
    public static readonly FontFamily Font = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly FontFamily Emoji = new("Segoe UI Emoji, Segoe UI Symbol");

    public static Brush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
    public static Brush Solid(string hex) => Solid((Color)ColorConverter.ConvertFromString(hex));

    public static void Init(AppSettings s)
    {
        Light = s.Theme == "Light";
        try { AccentC = (Color)ColorConverter.ConvertFromString(s.AccentColor); } catch { }
        Bg = Solid(Light ? "#F2F2F6" : "#121316");
        Panel = Solid(Light ? "#FFFFFF" : "#1C1D22");
        Panel2 = Solid(Light ? "#E8E8EE" : "#26272E");
        Hover = Solid(Light ? "#DADAE2" : "#32333B");
        Text = Solid(Light ? "#1B1B1F" : "#F4F4F6");
        Dim = Solid(Light ? "#6B6B75" : "#9A9AA6");
        Line = Solid(Light ? "#D5D5DD" : "#33343C");
        Accent = Solid(AccentC);
        double lum = (0.299 * AccentC.R + 0.587 * AccentC.G + 0.114 * AccentC.B) / 255.0;
        OnAccent = Solid(lum > 0.62 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White);
        Good = Solid("#3FD08A");
        Bad = Solid("#FF5C5C");
    }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Setup(Window win, string title, double w, double h, bool resizable = false)
    {
        win.Title = title + " - LightIsland Lab";
        win.Width = w;
        win.Height = h;
        win.Background = Bg;
        win.Foreground = Text;
        win.FontFamily = Font;
        win.FontSize = 13;
        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ResizeMode = resizable ? ResizeMode.CanResize : ResizeMode.CanMinimize;
        win.UseLayoutRounding = true;
        win.SnapsToDevicePixels = true;
        win.SourceInitialized += (_, _) =>
        {
            try
            {
                int dark = Light ? 0 : 1;
                DwmSetWindowAttribute(new WindowInteropHelper(win).Handle, 20, ref dark, sizeof(int));
            }
            catch { }
        };
        win.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.FocusedElement is not TextBox) win.Close(); };
    }

    public static TextBlock Label(string t, double size = 13, bool dim = false, bool bold = false)
    {
        return new TextBlock
        {
            Text = t,
            FontSize = size,
            Foreground = dim ? Dim : Text,
            FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static Border Btn(string text, Action click, bool primary = false, double minW = 0)
    {
        var tx = new TextBlock
        {
            Text = text,
            Foreground = primary ? OnAccent : Text,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var b = new Border
        {
            Child = tx,
            Background = primary ? Accent : Panel2,
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 14, 8),
            MinWidth = minW,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 8, 0),
            Tag = primary
        };
        b.MouseEnter += (_, _) => { if (b.IsEnabled) b.Opacity = 0.85; };
        b.MouseLeave += (_, _) => b.Opacity = 1;
        b.MouseLeftButtonUp += (_, e) => { if (!b.IsEnabled) return; e.Handled = true; click(); };
        b.IsEnabledChanged += (_, _) => b.Opacity = b.IsEnabled ? 1 : 0.4;
        return b;
    }

    public static void SetBtnText(Border b, string t) { if (b.Child is TextBlock tb) tb.Text = t; }

    public sealed class Input : Grid
    {
        public readonly TextBox Box;
        private readonly TextBlock _hint;
        public event Action? Changed;
        public Input(string hint, bool multi, double height, double fontSize = 14)
        {
            Box = new TextBox
            {
                Background = Panel,
                Foreground = Text,
                CaretBrush = Text,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 8, 10, 8),
                FontSize = fontSize,
                AcceptsReturn = multi,
                TextWrapping = multi ? TextWrapping.Wrap : TextWrapping.NoWrap,
                VerticalScrollBarVisibility = multi ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                VerticalContentAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center,
                SelectionBrush = Accent
            };
            if (height > 0) Box.Height = height;
            _hint = new TextBlock { Text = hint, Foreground = Dim, FontSize = fontSize, IsHitTestVisible = false, Margin = new Thickness(12, multi ? 9 : 0, 10, 0), VerticalAlignment = multi ? VerticalAlignment.Top : VerticalAlignment.Center };
            Children.Add(Box);
            Children.Add(_hint);
            Box.TextChanged += (_, _) => { _hint.Visibility = Box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Changed?.Invoke(); };
        }
        public string Text { get => Box.Text; set => Box.Text = value ?? ""; }
        public new bool Focus() => Box.Focus();
    }

    /// <summary>Row of chips; one is selected.</summary>
    public sealed class Chips : WrapPanel
    {
        private readonly List<Border> _items = new();
        private readonly string[] _names;
        public int Index { get; private set; }
        public event Action<int>? Selected;
        public Chips(string[] names, int index)
        {
            _names = names;
            Index = index;
            for (int i = 0; i < names.Length; i++)
            {
                int k = i;
                var b = new Border
                {
                    CornerRadius = new CornerRadius(14),
                    Padding = new Thickness(12, 5, 12, 5),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    Child = new TextBlock { Text = names[i], FontSize = 12.5, FontWeight = FontWeights.SemiBold }
                };
                b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Select(k, true); };
                _items.Add(b);
                Children.Add(b);
            }
            Paint();
        }
        public void Select(int i, bool raise)
        {
            if (i < 0 || i >= _items.Count) return;
            Index = i;
            Paint();
            if (raise) Selected?.Invoke(i);
        }
        private void Paint()
        {
            for (int i = 0; i < _items.Count; i++)
            {
                bool on = i == Index;
                _items[i].Background = on ? Accent : Panel2;
                ((TextBlock)_items[i].Child).Foreground = on ? OnAccent : Text;
            }
        }
        public string Value => _names[Index];
    }

    /// <summary>Button that opens a searchable list (languages, currencies ...).</summary>
    public sealed class Picker : Border
    {
        private readonly TextBlock _tx = new() { FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        private readonly List<(string Key, string Name)> _items;
        private readonly System.Windows.Controls.Primitives.Popup _pop;
        private readonly StackPanel _list = new();
        private readonly TextBox _search;
        public string Key { get; private set; }
        public event Action? Changed;

        public Picker(List<(string Key, string Name)> items, string key)
        {
            _items = items;
            Key = key;
            Background = Panel2;
            CornerRadius = new CornerRadius(10);
            Padding = new Thickness(12, 8, 12, 8);
            Cursor = Cursors.Hand;
            MinWidth = 120;
            var g = new Grid();
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _tx.Foreground = Text;
            g.Children.Add(_tx);
            var arrow = new TextBlock { Text = "▾", Foreground = Dim, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(arrow, 1);
            g.Children.Add(arrow);
            Child = g;

            _search = new TextBox { Background = Panel, Foreground = Text, CaretBrush = Text, BorderBrush = Line, Padding = new Thickness(8, 6, 8, 6), Margin = new Thickness(6) };
            var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, MaxHeight = 280 };
            var host = new StackPanel();
            host.Children.Add(_search);
            host.Children.Add(sv);
            var box = new Border { Background = Panel, BorderBrush = Line, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Child = host, Width = 260 };
            _pop = new System.Windows.Controls.Primitives.Popup { Child = box, PlacementTarget = this, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
            _search.TextChanged += (_, _) => Fill();
            MouseLeftButtonUp += (_, e) => { e.Handled = true; _search.Text = ""; Fill(); _pop.IsOpen = true; _search.Focus(); };
            Refresh();
        }

        public void Set(string key) { Key = key; Refresh(); }
        public string Name => _items.Find(i => i.Key == Key).Name ?? Key;

        private void Refresh() { _tx.Text = Name; }

        private void Fill()
        {
            _list.Children.Clear();
            string q = _search.Text.Trim();
            int n = 0;
            foreach (var it in _items)
            {
                if (q.Length > 0 && it.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0 && it.Key.IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0) continue;
                var row = new TextBlock { Text = it.Name, Foreground = Text, Padding = new Thickness(12, 7, 12, 7), Cursor = Cursors.Hand, Background = Brushes.Transparent };
                string k = it.Key;
                row.MouseEnter += (_, _) => row.Background = Hover;
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, e) => { e.Handled = true; Key = k; Refresh(); _pop.IsOpen = false; Changed?.Invoke(); };
                _list.Children.Add(row);
                if (++n > 250) break;
            }
        }
    }

    public static Border Card(UIElement child, double pad = 14)
    {
        return new Border { Background = Panel, CornerRadius = new CornerRadius(14), Padding = new Thickness(pad), Child = child, BorderBrush = Line, BorderThickness = new Thickness(1) };
    }

    public static Border Swatch(Color c, double w, double h, double r = 10)
    {
        return new Border { Width = w, Height = h, CornerRadius = new CornerRadius(r), Background = Solid(c), BorderBrush = Line, BorderThickness = new Thickness(1) };
    }

    public static void Copy(string text)
    {
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetText(text); return; }
            catch { System.Threading.Thread.Sleep(40); }
        }
    }

    public static string Hex(Color c) => "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");

    public static StackPanel Row(params UIElement[] items)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var i in items) p.Children.Add(i);
        return p;
    }

    /// <summary>Short message under a tool ("Copied", an error ...).</summary>
    public sealed class Status : TextBlock
    {
        private System.Windows.Threading.DispatcherTimer? _t;
        public Status() { FontSize = 12; Foreground = Dim; TextWrapping = TextWrapping.Wrap; }
        public void Say(string msg, bool bad = false, int ms = 2500)
        {
            Text = msg;
            Foreground = bad ? Bad : Good;
            _t?.Stop();
            if (ms <= 0) return;
            _t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            _t.Tick += (_, _) => { _t.Stop(); Text = ""; };
            _t.Start();
        }
        public void Info(string msg) { _t?.Stop(); Text = msg; Foreground = Dim; }
    }
}
