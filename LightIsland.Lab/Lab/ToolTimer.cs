using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>Pomodoro timer, stopwatch and ambient sounds (generated on this PC, nothing is downloaded).</summary>
internal sealed class TimerTool : Window
{
    private enum Mode { Focus, Short, Long }

    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock _time = new() { FontSize = 56, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontFamily = Ui.Font };
    private readonly TextBlock _modeTx = new() { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Path _arc = new() { StrokeThickness = 10, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly Ellipse _track = new() { StrokeThickness = 10, Width = 220, Height = 220 };
    private readonly TextBlock _rounds = new() { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Ui.Status _st = new();
    private Border _startBtn = null!;
    private Ui.Chips _tabs = null!;
    private readonly StackPanel _pomo = new(), _watch = new(), _sound = new();
    private readonly TextBlock _focusTx = new(), _shortTx = new(), _longTx = new();

    // pomodoro state
    private int _focusMin = 25, _shortMin = 5, _longMin = 15;
    private Mode _mode = Mode.Focus;
    private bool _running, _auto = true;
    private DateTime _endsAt;
    private TimeSpan _left;
    private int _done; // finished focus sessions

    // stopwatch
    private readonly Stopwatch _sw = new();
    private readonly TextBlock _swTx = new() { FontSize = 52, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = Ui.Mono };
    private readonly StackPanel _laps = new();
    private TimeSpan _lastLap;
    private Border _swStart = null!;

    // ambient
    private System.Windows.Media.MediaPlayer? _player;
    private string _playing = "";
    private double _vol = 0.5;
    private Ui.Chips _ambient = null!;
    private static readonly string[] Sounds = { "Off", "Rain", "Ocean", "Wind", "Brown noise", "Pink noise", "White noise" };

    [DllImport("user32.dll")] private static extern bool FlashWindowEx(ref FLASHWINFO f);
    [StructLayout(LayoutKind.Sequential)] private struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags; public uint uCount; public uint dwTimeout; }

    public TimerTool(string arg)
    {
        Ui.Setup(this, "Focus timer", 460, 780);
        Topmost = false;
        _left = TimeSpan.FromMinutes(_focusMin);

        var root = new StackPanel { Margin = new Thickness(18) };
        _tabs = new Ui.Chips(new[] { "Pomodoro", "Stopwatch", "Sounds" }, arg == "stopwatch" ? 1 : arg == "sounds" ? 2 : 0);
        _tabs.Selected += _ => ShowTab();
        root.Children.Add(_tabs);

        BuildPomodoro();
        BuildStopwatch();
        BuildSounds();
        root.Children.Add(_pomo);
        root.Children.Add(_watch);
        root.Children.Add(_sound);
        root.Children.Add(_st);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _tick.Tick += (_, _) => Tick();
        _tick.Start();
        ShowTab();
        Paint();
        Closed += (_, _) => StopSound();
        Activated += (_, _) => { };
    }

    private void ShowTab()
    {
        _pomo.Visibility = _tabs.Index == 0 ? Visibility.Visible : Visibility.Collapsed;
        _watch.Visibility = _tabs.Index == 1 ? Visibility.Visible : Visibility.Collapsed;
        _sound.Visibility = _tabs.Index == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---------------- Pomodoro ----------------
    private void BuildPomodoro()
    {
        var ring = new Grid { Width = 240, Height = 240, Margin = new Thickness(0, 18, 0, 8), HorizontalAlignment = HorizontalAlignment.Center };
        _track.Stroke = Ui.Panel2;
        ring.Children.Add(_track);
        ring.Children.Add(_arc);
        var mid = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        mid.Children.Add(_time);
        mid.Children.Add(_modeTx);
        ring.Children.Add(mid);
        _pomo.Children.Add(ring);
        _rounds.Foreground = Ui.Dim;
        _pomo.Children.Add(_rounds);

        _startBtn = Ui.Btn("Start", ToggleRun, true, 90);
        var ctrl = Ui.Row(_startBtn, Ui.Btn("Reset", ResetPomo), Ui.Btn("Skip", () => Advance(false)));
        ctrl.HorizontalAlignment = HorizontalAlignment.Center;
        ctrl.Margin = new Thickness(0, 12, 0, 12);
        _pomo.Children.Add(ctrl);

        _pomo.Children.Add(Stepper("Focus", _focusTx, () => _focusMin, v => { _focusMin = v; if (!_running && _mode == Mode.Focus) _left = TimeSpan.FromMinutes(v); }, 1, 120));
        _pomo.Children.Add(Stepper("Short break", _shortTx, () => _shortMin, v => { _shortMin = v; if (!_running && _mode == Mode.Short) _left = TimeSpan.FromMinutes(v); }, 1, 60));
        _pomo.Children.Add(Stepper("Long break", _longTx, () => _longMin, v => { _longMin = v; if (!_running && _mode == Mode.Long) _left = TimeSpan.FromMinutes(v); }, 1, 90));

        var auto = new Ui.Chips(new[] { "Auto-start next: on", "Auto-start next: off" }, 0) { Margin = new Thickness(0, 8, 0, 0) };
        auto.Selected += i => _auto = i == 0;
        _pomo.Children.Add(auto);
        var note = Ui.Label("A long break comes after every 4 focus sessions.", 12, true);
        _pomo.Children.Add(note);
    }

    private UIElement Stepper(string name, TextBlock val, Func<int> get, Action<int> set, int min, int max)
    {
        var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Label(name, 13));
        val.FontWeight = FontWeights.SemiBold;
        val.Foreground = Ui.Text;
        val.MinWidth = 64;
        val.TextAlignment = TextAlignment.Center;
        val.VerticalAlignment = VerticalAlignment.Center;
        val.Text = get() + " min";
        var minus = Ui.Btn("-", () => { int v = Math.Max(min, get() - 1); set(v); val.Text = v + " min"; Paint(); }, false, 34);
        var plus = Ui.Btn("+", () => { int v = Math.Min(max, get() + 1); set(v); val.Text = v + " min"; Paint(); }, false, 34);
        plus.Margin = new Thickness(0);
        var row = Ui.Row(minus, val, plus);
        Grid.SetColumn(row, 1);
        g.Children.Add(row);
        return g;
    }

    private TimeSpan Full => TimeSpan.FromMinutes(_mode == Mode.Focus ? _focusMin : _mode == Mode.Short ? _shortMin : _longMin);

    private void ToggleRun()
    {
        if (_running)
        {
            _left = _endsAt - DateTime.UtcNow;
            if (_left < TimeSpan.Zero) _left = TimeSpan.Zero;
            _running = false;
        }
        else
        {
            if (_left <= TimeSpan.Zero) _left = Full;
            _endsAt = DateTime.UtcNow + _left;
            _running = true;
        }
        Paint();
    }

    private void ResetPomo()
    {
        _running = false;
        _left = Full;
        Paint();
    }

    private void Advance(bool finished)
    {
        if (_mode == Mode.Focus)
        {
            if (finished) _done++;
            _mode = (_done > 0 && _done % 4 == 0 && finished) ? Mode.Long : Mode.Short;
        }
        else _mode = Mode.Focus;
        _left = Full;
        _running = finished && _auto;
        if (_running) _endsAt = DateTime.UtcNow + _left;
        Paint();
    }

    private void Tick()
    {
        if (_running)
        {
            _left = _endsAt - DateTime.UtcNow;
            if (_left <= TimeSpan.Zero)
            {
                _left = TimeSpan.Zero;
                Finished();
            }
        }
        Paint();
        if (_sw.IsRunning || _sw.Elapsed > TimeSpan.Zero) _swTx.Text = Fmt(_sw.Elapsed, true);
    }

    private void Finished()
    {
        string what = _mode == Mode.Focus ? "Focus session finished - take a break" : "Break is over - back to focus";
        try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
        Flash();
        _st.Say(what, false, 6000);
        Advance(true);
    }

    private void Flash()
    {
        try
        {
            var f = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(), hwnd = new WindowInteropHelper(this).Handle, dwFlags = 3 | 12, uCount = 6, dwTimeout = 0 };
            FlashWindowEx(ref f);
        }
        catch { }
    }

    private static string Fmt(TimeSpan t, bool cs = false)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        string s = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes:00}:{t.Seconds:00}";
        return cs ? s + "." + (t.Milliseconds / 10).ToString("00") : s;
    }

    private void Paint()
    {
        TimeSpan shown = _running ? _endsAt - DateTime.UtcNow : _left;
        if (shown < TimeSpan.Zero) shown = TimeSpan.Zero;
        _time.Text = Fmt(TimeSpan.FromSeconds(Math.Ceiling(shown.TotalSeconds)));
        _time.Foreground = Ui.Text;
        _modeTx.Text = _mode == Mode.Focus ? "FOCUS" : _mode == Mode.Short ? "SHORT BREAK" : "LONG BREAK";
        _modeTx.Foreground = _mode == Mode.Focus ? Ui.Accent : Ui.Good;
        _arc.Stroke = _mode == Mode.Focus ? Ui.Accent : Ui.Good;
        _rounds.Text = "Finished focus sessions: " + _done;
        Ui.SetBtnText(_startBtn, _running ? "Pause" : (_left < Full && _left > TimeSpan.Zero ? "Resume" : "Start"));
        double frac = Full.TotalSeconds > 0 ? Math.Clamp(shown.TotalSeconds / Full.TotalSeconds, 0, 1) : 0;
        _arc.Data = ArcGeometry(120, 120, 105, frac);
        Title = (_running ? Fmt(shown) + " - " : "") + "Focus timer - LightIsland Lab";
    }

    private static Geometry ArcGeometry(double cx, double cy, double r, double frac)
    {
        if (frac <= 0.0005) return Geometry.Empty;
        if (frac >= 0.9995) return new EllipseGeometry(new Point(cx, cy), r, r);
        double a = frac * 2 * Math.PI;
        var start = new Point(cx, cy - r);
        var end = new Point(cx + r * Math.Sin(a), cy - r * Math.Cos(a));
        var fig = new PathFigure { StartPoint = start, IsClosed = false };
        fig.Segments.Add(new ArcSegment(end, new Size(r, r), 0, frac > 0.5, SweepDirection.Clockwise, true));
        var g = new PathGeometry();
        g.Figures.Add(fig);
        return g;
    }

    // ---------------- Stopwatch ----------------
    private void BuildStopwatch()
    {
        _swTx.Text = "00:00.00";
        _swTx.Foreground = Ui.Text;
        _swTx.Margin = new Thickness(0, 40, 0, 20);
        _watch.Children.Add(_swTx);
        _swStart = Ui.Btn("Start", () =>
        {
            if (_sw.IsRunning) _sw.Stop(); else _sw.Start();
            Ui.SetBtnText(_swStart, _sw.IsRunning ? "Pause" : "Resume");
        }, true, 90);
        var row = Ui.Row(_swStart, Ui.Btn("Lap", Lap), Ui.Btn("Reset", () => { _sw.Reset(); _lastLap = TimeSpan.Zero; _laps.Children.Clear(); _swTx.Text = "00:00.00"; Ui.SetBtnText(_swStart, "Start"); }));
        row.HorizontalAlignment = HorizontalAlignment.Center;
        _watch.Children.Add(row);
        _laps.Margin = new Thickness(0, 16, 0, 0);
        _watch.Children.Add(_laps);
    }

    private void Lap()
    {
        if (_sw.Elapsed == TimeSpan.Zero) return;
        TimeSpan now = _sw.Elapsed, split = now - _lastLap;
        _lastLap = now;
        var g = new Grid { Margin = new Thickness(0, 3, 0, 3) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(60) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.Children.Add(Ui.Label("Lap " + (_laps.Children.Count + 1), 13, true));
        var a = new TextBlock { Text = Fmt(split, true), FontFamily = Ui.Mono, Foreground = Ui.Text };
        Grid.SetColumn(a, 1);
        g.Children.Add(a);
        var b = new TextBlock { Text = Fmt(now, true), FontFamily = Ui.Mono, Foreground = Ui.Dim };
        Grid.SetColumn(b, 2);
        g.Children.Add(b);
        _laps.Children.Insert(0, g);
    }

    // ---------------- Sounds ----------------
    private void BuildSounds()
    {
        var t = Ui.Label("Ambient sounds help you focus. They are generated on this PC, nothing is downloaded.", 12, true);
        t.Margin = new Thickness(0, 14, 0, 10);
        _sound.Children.Add(t);
        _ambient = new Ui.Chips(Sounds, 0);
        _ambient.Selected += i => _ = PlayAsync(Sounds[i]);
        _sound.Children.Add(_ambient);
        _sound.Children.Add(Ui.Label("Volume", 13));
        var sl = new Slider { Minimum = 0, Maximum = 1, Value = _vol, Margin = new Thickness(0, 6, 0, 0) };
        sl.ValueChanged += (_, e) => { _vol = e.NewValue; if (_player != null) _player.Volume = _vol; };
        _sound.Children.Add(sl);
    }

    private async Task PlayAsync(string name)
    {
        StopSound();
        if (name == "Off") return;
        _st.Info("Preparing the sound...");
        string path;
        try { path = await Task.Run(() => Ambient.EnsureFile(name)); }
        catch (Exception ex) { _st.Say("Could not make the sound: " + ex.Message, true); return; }
        if (_ambient.Value != name) return; // user picked something else meanwhile
        try
        {
            var p = new System.Windows.Media.MediaPlayer { Volume = _vol };
            p.MediaEnded += (_, _) => { p.Position = TimeSpan.Zero; p.Play(); };
            p.MediaFailed += (_, e) => _st.Say("Could not play the sound: " + e.ErrorException?.Message, true);
            p.Open(new Uri(path));
            p.Play();
            _player = p;
            _playing = name;
            _st.Say(name + " is playing", false, 1800);
        }
        catch (Exception ex) { _st.Say(ex.Message, true); }
    }

    private void StopSound()
    {
        try { _player?.Stop(); _player?.Close(); } catch { }
        _player = null;
        _playing = "";
    }
}

/// <summary>Makes seamless loops of noise: rain, ocean, wind, brown / pink / white noise.</summary>
internal static class Ambient
{
    private const int SR = 22050;
    private const int Seconds = 27;

    public static string EnsureFile(string name)
    {
        string path = Path.Combine(Path.GetTempPath(), "LightIslandLab-ambient-" + name.Replace(' ', '_') + ".wav");
        if (File.Exists(path) && new FileInfo(path).Length > 100000) return path;
        float[] data = Make(name);
        string tmp = path + ".tmp";
        using (var fs = File.Create(tmp))
        using (var w = new BinaryWriter(fs))
        {
            int n = data.Length;
            w.Write(new[] { 'R', 'I', 'F', 'F' });
            w.Write(36 + n * 2);
            w.Write(new[] { 'W', 'A', 'V', 'E', 'f', 'm', 't', ' ' });
            w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(SR); w.Write(SR * 2); w.Write((short)2); w.Write((short)16);
            w.Write(new[] { 'd', 'a', 't', 'a' });
            w.Write(n * 2);
            foreach (float f in data) w.Write((short)Math.Clamp(f * 30000f, -32000f, 32000f));
        }
        File.Move(tmp, path, true);
        return path;
    }

    private static float[] Make(string name)
    {
        int fade = SR * 2;
        int total = SR * Seconds + fade;
        var rnd = new Random(name.GetHashCode() ^ 12345);
        var x = new float[total];
        double b0 = 0, b1 = 0, b2 = 0, b3 = 0, b4 = 0, b5 = 0, b6 = 0, brown = 0, lp = 0, drop = 0;
        for (int i = 0; i < total; i++)
        {
            double t = (double)i / SR;
            double w = rnd.NextDouble() * 2 - 1;
            double v;
            switch (name)
            {
                case "White noise": v = w * 0.5; break;
                case "Pink noise":
                    b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
                    b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
                    v = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11; b6 = w * 0.115926; break;
                case "Brown noise": brown = (brown + 0.02 * w) / 1.02; v = brown * 3.5; break;
                case "Rain":
                    b0 = 0.99886 * b0 + w * 0.0555179; b1 = 0.99332 * b1 + w * 0.0750759; b2 = 0.96900 * b2 + w * 0.1538520;
                    b3 = 0.86650 * b3 + w * 0.3104856; b4 = 0.55000 * b4 + w * 0.5329522; b5 = -0.7616 * b5 - w * 0.0168980;
                    double pink = (b0 + b1 + b2 + b3 + b4 + b5 + b6 + w * 0.5362) * 0.11; b6 = w * 0.115926;
                    if (rnd.NextDouble() < 70.0 / SR) drop += 0.15 + rnd.NextDouble() * 0.5;
                    drop *= 0.9925;
                    v = pink * 0.55 + drop * (rnd.NextDouble() * 2 - 1) * 0.6;
                    break;
                case "Ocean":
                    brown = (brown + 0.02 * w) / 1.02;
                    double swell = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 9.0 + 0.9 * Math.Sin(2 * Math.PI * t / 13.5));
                    v = brown * 3.5 * (0.25 + 0.75 * swell * swell);
                    break;
                default: // Wind
                    double gust = 0.5 + 0.5 * Math.Sin(2 * Math.PI * t / 13.5 + 1.3 * Math.Sin(2 * Math.PI * t / 27.0));
                    double a = 0.02 + 0.09 * gust;
                    lp += a * (w - lp);
                    v = lp * (1.2 + 5.0 * gust);
                    break;
            }
            x[i] = (float)v;
        }

        // seamless loop: the last 2 seconds fade into the first 2 seconds
        int n = SR * Seconds;
        var o = new float[n];
        Array.Copy(x, o, n);
        for (int i = 0; i < fade; i++)
        {
            float k = (float)i / fade;
            o[i] = o[i] * k + x[n + i] * (1 - k);
        }
        // normalize
        float peak = 0;
        foreach (float f in o) peak = Math.Max(peak, Math.Abs(f));
        float g = peak > 0 ? 0.9f / peak : 1f;
        for (int i = 0; i < n; i++) o[i] *= g;
        return o;
    }
}
