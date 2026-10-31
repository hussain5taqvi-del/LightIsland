using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace LightIsland;

/// <summary>Screenshot of the whole desktop (all screens) with plain GDI, no extra library.</summary>
internal static class ScreenGrab
{
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int w, int h);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(IntPtr d, int x, int y, int w, int h, IntPtr s, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr o);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);

    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }

    public static double Scale
    {
        get { try { double s = GetDpiForSystem() / 96.0; return s < 0.5 ? 1 : s; } catch { return 1; } }
    }

    /// <summary>Position and size of the whole desktop in real pixels.</summary>
    public static Int32Rect Virtual()
    {
        return new Int32Rect(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
    }

    public static BitmapSource Capture(Int32Rect r)
    {
        IntPtr screen = GetDC(IntPtr.Zero);
        IntPtr mem = CreateCompatibleDC(screen);
        IntPtr bmp = CreateCompatibleBitmap(screen, r.Width, r.Height);
        IntPtr old = SelectObject(mem, bmp);
        try
        {
            BitBlt(mem, 0, 0, r.Width, r.Height, screen, r.X, r.Y, 0x00CC0020 | 0x40000000); // SRCCOPY | CAPTUREBLT
            var src = Imaging.CreateBitmapSourceFromHBitmap(bmp, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var conv = new FormatConvertedBitmap(src, PixelFormats.Bgr32, null, 0); // alpha of a GDI picture is not reliable
            conv.Freeze();
            return conv;
        }
        finally
        {
            SelectObject(mem, old);
            DeleteObject(bmp);
            DeleteDC(mem);
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    public static byte[] ToPng(BitmapSource src)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(src));
        using var ms = new System.IO.MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    public static Color PixelAt(BitmapSource snap, int x, int y)
    {
        x = Math.Clamp(x, 0, snap.PixelWidth - 1);
        y = Math.Clamp(y, 0, snap.PixelHeight - 1);
        var b = new byte[4];
        snap.CopyPixels(new Int32Rect(x, y, 1, 1), b, 4, 0);
        return Color.FromRgb(b[2], b[1], b[0]);
    }
}

/// <summary>Full-screen overlay: drag a rectangle (region mode) or move a magnifier and click (color mode).</summary>
internal sealed class PickOverlay : Window
{
    private readonly BitmapSource _snap;
    private readonly Int32Rect _virt;
    private readonly double _scale;
    private readonly bool _color;
    private readonly Canvas _cv = new();
    private readonly Path _dim = new() { Fill = new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _sel = new() { Stroke = Brushes.White, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 4, 3 }, IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly Image _loupe = new() { Width = 153, Height = 153, IsHitTestVisible = false };
    private readonly Border _loupeBox = new() { Width = 161, Height = 187, CornerRadius = new CornerRadius(12), Background = new SolidColorBrush(Color.FromArgb(235, 18, 18, 22)), IsHitTestVisible = false, Visibility = Visibility.Collapsed };
    private readonly TextBlock _loupeTx = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Rectangle _cross = new() { Width = 9, Height = 9, Stroke = Brushes.White, StrokeThickness = 1.5, IsHitTestVisible = false };
    private Point _start;
    private bool _dragging;

    public Int32Rect? Region { get; private set; }      // in real pixels of the snapshot
    public BitmapSource? Cropped { get; private set; }
    public Color? PickedColor { get; private set; }

    public PickOverlay(bool colorMode, string hint)
    {
        _color = colorMode;
        _virt = ScreenGrab.Virtual();
        _scale = ScreenGrab.Scale;
        _snap = ScreenGrab.Capture(_virt);

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _virt.X / _scale;
        Top = _virt.Y / _scale;
        Width = _virt.Width / _scale;
        Height = _virt.Height / _scale;
        Cursor = Cursors.Cross;
        Background = Brushes.Black;
        UseLayoutRounding = false;

        var img = new Image { Source = _snap, Width = Width, Height = Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        _cv.Children.Add(img);
        if (!_color) _cv.Children.Add(_dim);
        _cv.Children.Add(_sel);

        var tip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 16, 16, 20)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 8, 14, 8),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = hint + "   (Esc to cancel)", Foreground = Brushes.White, FontSize = 14, FontFamily = new FontFamily("Segoe UI") }
        };
        Canvas.SetLeft(tip, 24);
        Canvas.SetTop(tip, 24);
        _cv.Children.Add(tip);

        if (_color)
        {
            var g = new Grid();
            g.Children.Add(_loupe);
            _loupe.Margin = new Thickness(4, 4, 4, 30);
            _loupe.HorizontalAlignment = HorizontalAlignment.Left;
            _loupe.VerticalAlignment = VerticalAlignment.Top;
            var ring = new Rectangle { Width = 153, Height = 153, Stroke = Brushes.White, StrokeThickness = 1, Margin = new Thickness(4, 4, 4, 30), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            g.Children.Add(ring);
            var centre = new Rectangle { Width = 9, Height = 9, Stroke = Brushes.White, StrokeThickness = 1.5, Margin = new Thickness(4 + 72, 4 + 72, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
            g.Children.Add(centre);
            _loupeTx.VerticalAlignment = VerticalAlignment.Bottom;
            _loupeTx.Margin = new Thickness(0, 0, 0, 6);
            g.Children.Add(_loupeTx);
            _loupeBox.Child = g;
            _cv.Children.Add(_loupeBox);
        }

        Content = _cv;
        MouseMove += OnMove;
        MouseLeftButtonDown += OnDown;
        MouseLeftButtonUp += OnUp;
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += OnKey;
        Loaded += (_, _) => { Activate(); Focus(); Keyboard.Focus(this); };
        UpdateDim(new Rect(0, 0, 0, 0));
    }

    private void UpdateDim(Rect r)
    {
        var all = new RectangleGeometry(new Rect(0, 0, Width, Height));
        _dim.Data = r.Width > 0 && r.Height > 0 ? new CombinedGeometry(GeometryCombineMode.Exclude, all, new RectangleGeometry(r)) : all;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); return; }
        if (!_color) return;
        int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;
        int dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
        if (dx != 0 || dy != 0)
        {
            if (ScreenGrab.GetCursorPos(out var q)) ScreenGrab.SetCursorPos(q.X + dx, q.Y + dy);
            e.Handled = true;
        }
        if (e.Key == Key.Enter) { PickAt(Mouse.GetPosition(this)); }
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        Point p = e.GetPosition(this);
        if (_color) { UpdateLoupe(p); return; }
        if (!_dragging) return;
        var r = new Rect(Math.Min(_start.X, p.X), Math.Min(_start.Y, p.Y), Math.Abs(p.X - _start.X), Math.Abs(p.Y - _start.Y));
        _sel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sel, r.X);
        Canvas.SetTop(_sel, r.Y);
        _sel.Width = r.Width;
        _sel.Height = r.Height;
        UpdateDim(r);
    }

    private void UpdateLoupe(Point p)
    {
        int px = (int)Math.Round(p.X * _scale), py = (int)Math.Round(p.Y * _scale);
        px = Math.Clamp(px, 0, _snap.PixelWidth - 1);
        py = Math.Clamp(py, 0, _snap.PixelHeight - 1);
        int x0 = Math.Clamp(px - 8, 0, Math.Max(0, _snap.PixelWidth - 17));
        int y0 = Math.Clamp(py - 8, 0, Math.Max(0, _snap.PixelHeight - 17));
        var crop = new CroppedBitmap(_snap, new Int32Rect(x0, y0, Math.Min(17, _snap.PixelWidth), Math.Min(17, _snap.PixelHeight)));
        RenderOptions.SetBitmapScalingMode(_loupe, BitmapScalingMode.NearestNeighbor);
        _loupe.Source = crop;
        Color c = ScreenGrab.PixelAt(_snap, px, py);
        _loupeTx.Text = Ui.Hex(c);
        _loupeBox.Visibility = Visibility.Visible;
        double lx = p.X + 24, ly = p.Y + 24;
        if (lx + 170 > Width) lx = p.X - 190;
        if (ly + 200 > Height) ly = p.Y - 210;
        Canvas.SetLeft(_loupeBox, lx);
        Canvas.SetTop(_loupeBox, ly);
    }

    private void PickAt(Point p)
    {
        int px = (int)Math.Round(p.X * _scale), py = (int)Math.Round(p.Y * _scale);
        PickedColor = ScreenGrab.PixelAt(_snap, px, py);
        Close();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        Point p = e.GetPosition(this);
        if (_color) { PickAt(p); return; }
        _start = p;
        _dragging = true;
        CaptureMouse();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        if (_color || !_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        Point p = e.GetPosition(this);
        int x = (int)Math.Round(Math.Min(_start.X, p.X) * _scale), y = (int)Math.Round(Math.Min(_start.Y, p.Y) * _scale);
        int w = (int)Math.Round(Math.Abs(p.X - _start.X) * _scale), h = (int)Math.Round(Math.Abs(p.Y - _start.Y) * _scale);
        x = Math.Clamp(x, 0, _snap.PixelWidth - 1);
        y = Math.Clamp(y, 0, _snap.PixelHeight - 1);
        w = Math.Min(w, _snap.PixelWidth - x);
        h = Math.Min(h, _snap.PixelHeight - y);
        if (w >= 6 && h >= 6)
        {
            Region = new Int32Rect(x, y, w, h);
            var c = new CroppedBitmap(_snap, Region.Value);
            c.Freeze();
            Cropped = c;
        }
        Close();
    }

    /// <summary>Shows the overlay and waits until it is closed.</summary>
    public static PickOverlay? Run(bool colorMode, string hint)
    {
        var o = new PickOverlay(colorMode, hint);
        o.ShowDialog();
        return o;
    }
}
