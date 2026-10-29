using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>
/// Music visuals that react to the song that is playing: a glow around the screen edges, or a full wallpaper behind the
/// icons (blurred album art, moving colors, the cover, title and time-synced lyrics). Off by default. While one is on, the
/// RAM cap of the island is raised to 100 MB; when it is switched off the normal cap is back at once.
/// </summary>
internal static class MusicFx
{
    private static MusicFxWindow? _win;
    private static string _mode = "Off";
    private static bool _lyrics = true;
    private static Func<(double Position, double Duration)>? _progress;
    private static string _title = "", _artist = "";
    private static bool _playing;
    private static byte[]? _art;
    private static DispatcherTimer? _delay;
    private static bool _first = true;

    public static void Configure(string mode, bool lyrics, Func<(double Position, double Duration)> progress)
    {
        _progress = progress;
        if (mode != "Edge" && mode != "Wallpaper") mode = "Off";
        if (mode == _mode && lyrics == _lyrics && (_win != null || mode == "Off" || _delay != null)) return;

        _delay?.Stop();
        _delay = null;
        Stop();
        _mode = mode;
        _lyrics = lyrics;
        if (mode == "Off") return;

        if (_first && mode == "Wallpaper")
        {
            // right after Windows start: let the live wallpaper (if any) come back first
            _first = false;
            _delay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
            _delay.Tick += (_, _) => { _delay?.Stop(); _delay = null; Start(); };
            _delay.Start();
            return;
        }
        _first = false;
        Start();
    }

    private static void Start()
    {
        if (_mode == "Off") return;
        string mode = _mode;
        // the wallpaper cannot share the desktop layer with a live video wallpaper: the edge glow is used instead
        if (mode == "Wallpaper" && WallpaperManager.IsLiveRunning) mode = "Edge";
        try
        {
            MemoryGuard.SetFeatureCap(100);
            _win = new MusicFxWindow(mode == "Wallpaper", _lyrics, () => _progress?.Invoke() ?? (0, 0));
            _win.Show();
            _win.SetTrack(_title, _artist, _playing, _art);
        }
        catch
        {
            Stop();
        }
    }

    private static void Stop()
    {
        var w = _win;
        _win = null;
        if (w != null) { try { w.Shutdown(); } catch { } }
        MemoryGuard.SetFeatureCap(0);
    }

    public static void OnTrack(string title, string artist, bool playing, byte[]? art)
    {
        _title = title ?? "";
        _artist = artist ?? "";
        _playing = playing;
        _art = art;
        _win?.SetTrack(_title, _artist, playing, art);
    }

    public static void Shutdown()
    {
        _delay?.Stop();
        _delay = null;
        Stop();
    }
}

internal sealed class MusicFxWindow : Window
{
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    private readonly bool _wall, _lyrics;
    private readonly Func<(double Position, double Duration)> _progress;
    private readonly AudioMeter _meter = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private IntPtr _hwnd;
    private int _frame;
    private double _level, _t, _fade;
    private bool _playing, _hiddenByFullscreen;

    // colors (current follow the targets smoothly)
    private Color _p = Color.FromRgb(90, 140, 255), _s = Color.FromRgb(190, 90, 255);
    private Color _tp = Color.FromRgb(90, 140, 255), _ts = Color.FromRgb(190, 90, 255);

    // edge glow
    private Border? _top, _bottom, _left, _right;
    private GradientStop? _t0, _b0, _l0, _r0;

    // wallpaper
    private Image? _bg;
    private System.Windows.Shapes.Ellipse? _blobA, _blobB;
    private GradientStop? _ga, _gb;
    private TranslateTransform? _ta, _tb;
    private ScaleTransform? _sa, _sb, _bgScale;
    private Border? _cover;
    private TextBlock? _titleTx, _artistTx;
    private StackPanel? _lyricBox;
    private readonly TextBlock[] _ly = new TextBlock[5];
    private Grid? _stage;
    private ColumnDefinition? _colLeft, _colRight;

    // lyrics
    private System.Collections.Generic.List<LyricLine>? _lines;
    private int _lineIdx = -2;
    private CancellationTokenSource? _cts;
    private string _trackKey = "", _lastStarted = "";

    public MusicFxWindow(bool wallpaper, bool lyrics, Func<(double Position, double Duration)> progress)
    {
        _wall = wallpaper;
        _lyrics = lyrics;
        _progress = progress;

        Title = "LightIsland Music";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;
        UseLayoutRounding = true;

        if (_wall)
        {
            Background = new SolidColorBrush(Color.FromRgb(8, 8, 12));
            Content = BuildWallpaper();
        }
        else
        {
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Topmost = true;
            Opacity = 0;
            Content = BuildEdge();
        }

        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            if (_wall) DesktopHost.Layout(_hwnd);
            _timer.Start();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        if (_wall)
        {
            if (!DesktopHost.Attach(_hwnd)) throw new InvalidOperationException("desktop");
        }
        else
        {
            // clicks go straight through; never takes focus; not in Alt+Tab
            int ex = GetWindowLong(_hwnd, -20);
            SetWindowLong(_hwnd, -20, ex | 0x20 | 0x08000000 | 0x80);
        }
    }

    // ---------- Edge glow ----------

    private UIElement BuildEdge()
    {
        var g = new Grid { IsHitTestVisible = false };
        _top = Edge(out _t0, new Point(0.5, 0), new Point(0.5, 1), VerticalAlignment.Top, HorizontalAlignment.Stretch);
        _bottom = Edge(out _b0, new Point(0.5, 1), new Point(0.5, 0), VerticalAlignment.Bottom, HorizontalAlignment.Stretch);
        _left = Edge(out _l0, new Point(0, 0.5), new Point(1, 0.5), VerticalAlignment.Stretch, HorizontalAlignment.Left);
        _right = Edge(out _r0, new Point(1, 0.5), new Point(0, 0.5), VerticalAlignment.Stretch, HorizontalAlignment.Right);
        g.Children.Add(_top);
        g.Children.Add(_bottom);
        g.Children.Add(_left);
        g.Children.Add(_right);
        return g;
    }

    private static Border Edge(out GradientStop first, Point a, Point b, VerticalAlignment va, HorizontalAlignment ha)
    {
        first = new GradientStop(Colors.Transparent, 0);
        var br = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        br.GradientStops.Add(first);
        br.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 1));
        return new Border { Background = br, VerticalAlignment = va, HorizontalAlignment = ha, IsHitTestVisible = false };
    }

    // ---------- Wallpaper ----------

    private UIElement BuildWallpaper()
    {
        var root = new Grid { ClipToBounds = true };

        _bgScale = new ScaleTransform(1.2, 1.2);
        _bg = new Image
        {
            Stretch = Stretch.UniformToFill,
            Opacity = 0.5,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _bgScale
        };
        RenderOptions.SetBitmapScalingMode(_bg, BitmapScalingMode.Linear);
        root.Children.Add(_bg);

        var canvas = new Canvas { IsHitTestVisible = false };
        _blobA = Blob(out _ga, out _ta, out _sa);
        _blobB = Blob(out _gb, out _tb, out _sb);
        canvas.Children.Add(_blobA);
        canvas.Children.Add(_blobB);
        root.Children.Add(canvas);

        // soft dark layer: the text stays readable on any cover
        root.Children.Add(new Border
        {
            Background = new LinearGradientBrush(Color.FromArgb(120, 0, 0, 0), Color.FromArgb(190, 0, 0, 0), 90)
        });

        // content is designed for 1920 x 1080 and scaled to the screen
        _stage = new Grid { Width = 1920, Height = 1080 };
        _colLeft = new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) };
        _colRight = new ColumnDefinition { Width = new GridLength(0) };
        _stage.ColumnDefinitions.Add(_colLeft);
        _stage.ColumnDefinitions.Add(_colRight);

        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        _cover = new Border
        {
            Width = 380,
            Height = 380,
            CornerRadius = new CornerRadius(34),
            Background = new SolidColorBrush(Color.FromRgb(30, 30, 36)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        _titleTx = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 38,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 700,
            Margin = new Thickness(0, 34, 0, 0)
        };
        _artistTx = new TextBlock
        {
            Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            FontSize = 24,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 700,
            Margin = new Thickness(0, 6, 0, 0)
        };
        left.Children.Add(_cover);
        left.Children.Add(_titleTx);
        left.Children.Add(_artistTx);
        Grid.SetColumn(left, 0);
        _stage.Children.Add(left);

        _lyricBox = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 90, 0), Visibility = Visibility.Collapsed };
        double[] size = { 30, 34, 54, 34, 30 };
        double[] op = { 0.22, 0.4, 1.0, 0.45, 0.25 };
        for (int i = 0; i < 5; i++)
        {
            _ly[i] = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = size[i],
                FontWeight = i == 2 ? FontWeights.Bold : FontWeights.SemiBold,
                Opacity = op[i],
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 10, 0, 10)
            };
            _lyricBox.Children.Add(_ly[i]);
        }
        Grid.SetColumn(_lyricBox, 1);
        _stage.Children.Add(_lyricBox);

        root.Children.Add(new Viewbox { Stretch = Stretch.Uniform, Child = _stage });
        return root;
    }

    private static System.Windows.Shapes.Ellipse Blob(out GradientStop center, out TranslateTransform tr, out ScaleTransform sc)
    {
        center = new GradientStop(Color.FromArgb(150, 90, 140, 255), 0);
        var br = new RadialGradientBrush();
        br.GradientStops.Add(center);
        br.GradientStops.Add(new GradientStop(Color.FromArgb(0, 90, 140, 255), 1));
        tr = new TranslateTransform();
        sc = new ScaleTransform(1, 1);
        var tg = new TransformGroup();
        tg.Children.Add(sc);
        tg.Children.Add(tr);
        return new System.Windows.Shapes.Ellipse { Width = 1100, Height = 1100, Fill = br, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = tg, IsHitTestVisible = false };
    }

    // ---------- Track ----------

    public void SetTrack(string title, string artist, bool playing, byte[]? art)
    {
        bool wasPlaying = _playing;
        _playing = playing;
        string key = title + "|" + artist;

        if (key != _trackKey)
        {
            _trackKey = key;
            if (_titleTx != null) _titleTx.Text = string.IsNullOrWhiteSpace(title) ? "Nothing playing" : title;
            if (_artistTx != null) _artistTx.Text = artist;
            ApplyArt(art);
            StartLyrics(title, artist);
        }
        else if (art != null && _cover != null && _cover.Background is SolidColorBrush)
        {
            ApplyArt(art); // art can arrive a moment after the title
        }

        if (playing && !wasPlaying || key != _lastStarted) _timer.Start();
        _lastStarted = key;
    }

    private void ApplyArt(byte[]? data)
    {
        BitmapImage? small = null;
        if (data != null && data.Length > 0)
        {
            try
            {
                using var ms = new MemoryStream(data);
                small = new BitmapImage();
                small.BeginInit();
                small.CacheOption = BitmapCacheOption.OnLoad;
                small.DecodePixelWidth = 48; // tiny on purpose: stretched up it is a soft blur
                small.StreamSource = ms;
                small.EndInit();
                small.Freeze();
            }
            catch { small = null; }
        }

        if (small != null) PickColors(small);
        if (!_wall) return;

        if (_bg != null) _bg.Source = small;
        if (_cover != null)
        {
            try
            {
                if (data != null && data.Length > 0)
                {
                    using var ms = new MemoryStream(data);
                    var big = new BitmapImage();
                    big.BeginInit();
                    big.CacheOption = BitmapCacheOption.OnLoad;
                    big.DecodePixelWidth = 520;
                    big.StreamSource = ms;
                    big.EndInit();
                    big.Freeze();
                    _cover.Background = new ImageBrush(big) { Stretch = Stretch.UniformToFill };
                }
                else
                {
                    _cover.Background = new SolidColorBrush(Color.FromRgb(30, 30, 36));
                }
            }
            catch { }
        }
    }

    // the two most colorful hues of the cover
    private void PickColors(BitmapSource bmp)
    {
        try
        {
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);

            const int N = 12;
            var wt = new double[N];
            var r = new double[N];
            var g = new double[N];
            var b = new double[N];
            for (int i = 0; i < px.Length; i += 4)
            {
                double B = px[i] / 255.0, G = px[i + 1] / 255.0, R = px[i + 2] / 255.0;
                double mx = Math.Max(R, Math.Max(G, B)), mn = Math.Min(R, Math.Min(G, B));
                double sat = mx <= 0 ? 0 : (mx - mn) / mx;
                if (mx < 0.18 || sat < 0.22) continue;
                double hue;
                double d = mx - mn;
                if (mx == R) hue = ((G - B) / d) % 6; else if (mx == G) hue = (B - R) / d + 2; else hue = (R - G) / d + 4;
                hue = hue * 60; if (hue < 0) hue += 360;
                int k = Math.Min(N - 1, (int)(hue / (360.0 / N)));
                double ww = sat * mx;
                wt[k] += ww; r[k] += R * ww; g[k] += G * ww; b[k] += B * ww;
            }

            int a1 = -1;
            for (int k = 0; k < N; k++) if (a1 < 0 || wt[k] > wt[a1]) a1 = k;
            if (a1 < 0 || wt[a1] <= 0) { _tp = Color.FromRgb(220, 220, 235); _ts = Color.FromRgb(140, 150, 190); return; }
            int a2 = -1;
            for (int k = 0; k < N; k++)
            {
                int dist = Math.Min(Math.Abs(k - a1), N - Math.Abs(k - a1));
                if (dist < 2 || wt[k] <= wt[a1] * 0.08) continue;
                if (a2 < 0 || wt[k] > wt[a2]) a2 = k;
            }
            _tp = Glow(r[a1] / wt[a1], g[a1] / wt[a1], b[a1] / wt[a1]);
            _ts = a2 >= 0 ? Glow(r[a2] / wt[a2], g[a2] / wt[a2], b[a2] / wt[a2]) : Shift(_tp);
        }
        catch { }
    }

    // bright enough to glow on any background
    private static Color Glow(double r, double g, double b)
    {
        double mx = Math.Max(r, Math.Max(g, b));
        double k = mx > 0 ? Math.Min(1.0 / mx, 2.2) : 1;
        r = Math.Min(1, r * k); g = Math.Min(1, g * k); b = Math.Min(1, b * k);
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static Color Shift(Color c) => Color.FromRgb(c.B, c.R, c.G);

    // ---------- Lyrics ----------

    private void StartLyrics(string title, string artist)
    {
        _cts?.Cancel();
        _lines = null;
        _lineIdx = -2;
        ShowLyricLines(-1);
        if (!_wall || !_lyrics || string.IsNullOrWhiteSpace(title)) return;

        var cts = _cts = new CancellationTokenSource();
        double dur = 0;
        try { dur = _progress().Duration; } catch { }
        _ = LoadLyrics(title, artist, dur, cts);
    }

    private async System.Threading.Tasks.Task LoadLyrics(string title, string artist, double dur, CancellationTokenSource cts)
    {
        try
        {
            var lines = await Lyrics.GetAsync(title, artist, dur, cts.Token);
            if (cts.IsCancellationRequested) return;
            _lines = lines;
            _lineIdx = -2;
            if (lines != null && _colRight != null)
            {
                _colLeft!.Width = new GridLength(0.9, GridUnitType.Star);
                _colRight.Width = new GridLength(1.4, GridUnitType.Star);
                _lyricBox!.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private void ShowLyricLines(int idx)
    {
        if (!_wall || _lyricBox == null) return;
        if (_lines == null)
        {
            _lyricBox.Visibility = Visibility.Collapsed;
            if (_colLeft != null && _colRight != null) { _colLeft.Width = new GridLength(1, GridUnitType.Star); _colRight.Width = new GridLength(0); }
            return;
        }
        for (int i = 0; i < 5; i++)
        {
            int n = idx - 2 + i;
            _ly[i].Text = n >= 0 && n < _lines.Count ? (string.IsNullOrWhiteSpace(_lines[n].Text) ? "♪" : _lines[n].Text) : "";
        }
        if (_ly[2].Text.Length > 0)
            _ly[2].BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(260)));
    }

    // ---------- Frame ----------

    private void Tick()
    {
        _frame++;
        _t += 0.033;

        // pause the whole thing while something full screen (game, video) is on top, or the desktop is covered
        if (_frame % 30 == 0)
        {
            bool cover = _wall ? DesktopHost.IsDesktopCovered(_hwnd) : DesktopHost.IsFullscreenApp(_hwnd);
            if (cover != _hiddenByFullscreen)
            {
                _hiddenByFullscreen = cover;
                if (!_wall) Visibility = cover ? Visibility.Hidden : Visibility.Visible;
            }
        }
        if (_hiddenByFullscreen) return;

        // colors glide to the new song's colors
        _p = Lerp(_p, _tp, 0.07);
        _s = Lerp(_s, _ts, 0.07);

        double raw = _playing ? Math.Min(1.0, Math.Sqrt(_meter.GetPeak()) * 1.3) : 0;
        _level += (raw - _level) * (raw > _level ? 0.6 : 0.14);
        _fade += ((_playing ? 1.0 : 0.0) - _fade) * 0.06;

        if (_wall) TickWall(); else TickEdge();

        // idle: nothing moves any more, so nothing runs
        if (!_playing && _fade < 0.01 && !_wall) { Opacity = 0; _timer.Stop(); }
        else if (!_playing && _level < 0.01 && _wall && _frame % 15 == 0) { _timer.Stop(); }
    }

    private void TickEdge()
    {
        if (_top == null) return;
        Opacity = _fade;
        double a = 0.5 + 0.5 * Math.Sin(_t * 0.6);
        double b = 0.5 + 0.5 * Math.Sin(_t * 0.6 + 2.1);
        Color c1 = Lerp(_p, _s, a), c2 = Lerp(_s, _p, a), c3 = Lerp(_p, _s, b), c4 = Lerp(_s, _p, b);
        double alpha = 70 + 150 * _level;
        _t0!.Color = Color.FromArgb((byte)alpha, c1.R, c1.G, c1.B);
        _b0!.Color = Color.FromArgb((byte)alpha, c2.R, c2.G, c2.B);
        _l0!.Color = Color.FromArgb((byte)alpha, c3.R, c3.G, c3.B);
        _r0!.Color = Color.FromArgb((byte)alpha, c4.R, c4.G, c4.B);
        double th = 36 + 90 * _level;
        _top.Height = th;
        _bottom!.Height = th;
        _left!.Width = th;
        _right!.Width = th;
    }

    private void TickWall()
    {
        double w = ActualWidth > 10 ? ActualWidth : 1920, h = ActualHeight > 10 ? ActualHeight : 1080;
        double sz = Math.Max(w, h) * 0.62;
        _blobA!.Width = _blobA.Height = sz;
        _blobB!.Width = _blobB.Height = sz;
        Canvas.SetLeft(_blobA, w * 0.15 - sz / 2);
        Canvas.SetTop(_blobA, h * 0.25 - sz / 2);
        Canvas.SetLeft(_blobB, w * 0.85 - sz / 2);
        Canvas.SetTop(_blobB, h * 0.8 - sz / 2);
        _ta!.X = Math.Sin(_t * 0.35) * w * 0.12;
        _ta.Y = Math.Cos(_t * 0.27) * h * 0.1;
        _tb!.X = Math.Cos(_t * 0.31) * w * 0.12;
        _tb.Y = Math.Sin(_t * 0.23) * h * 0.1;
        double sc = 0.85 + 0.45 * _level;
        _sa!.ScaleX = _sa.ScaleY = sc;
        _sb!.ScaleX = _sb.ScaleY = 1.3 - 0.45 * _level;
        byte al = (byte)(90 + 110 * _level);
        _ga!.Color = Color.FromArgb(al, _p.R, _p.G, _p.B);
        _gb!.Color = Color.FromArgb(al, _s.R, _s.G, _s.B);
        ((RadialGradientBrush)_blobA.Fill).GradientStops[1].Color = Color.FromArgb(0, _p.R, _p.G, _p.B);
        ((RadialGradientBrush)_blobB.Fill).GradientStops[1].Color = Color.FromArgb(0, _s.R, _s.G, _s.B);
        double bs = 1.18 + 0.05 * Math.Sin(_t * 0.2) + 0.03 * _level;
        _bgScale!.ScaleX = _bgScale.ScaleY = bs;

        // lyrics follow the position of the song (a few times per second is enough)
        if (_lines != null && _frame % 4 == 0)
        {
            double pos = 0;
            try { pos = _progress().Position; } catch { }
            pos += 0.25;
            int idx = -1;
            var l = _lines;
            for (int i = 0; i < l.Count; i++) { if (l[i].Time <= pos) idx = i; else break; }
            if (idx != _lineIdx)
            {
                _lineIdx = idx;
                ShowLyricLines(idx);
            }
        }
    }

    private static Color Lerp(Color a, Color b, double t)
        => Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));

    public void Shutdown()
    {
        _timer.Stop();
        _cts?.Cancel();
        _meter.Dispose();
        Content = null;
        try { Close(); } catch { }
        if (_wall) DesktopHost.RefreshWallpaper();
    }
}
