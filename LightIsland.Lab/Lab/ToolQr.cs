using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LightIsland;

/// <summary>Make QR codes (text, link, Wi-Fi) and read them from the screen, clipboard or a file. All offline.</summary>
internal sealed class QrTool : Window
{
    private readonly Ui.Chips _mode;
    private readonly StackPanel _gen = new(), _scan = new();
    private readonly Ui.Chips _kind = new(new[] { "Text / link", "Wi-Fi" }, 0);
    private readonly Ui.Input _text = new("Text or link", true, 90);
    private readonly Ui.Input _ssid = new("Network name", false, 0), _pass = new("Password", false, 0);
    private readonly Ui.Chips _sec = new(new[] { "WPA/WPA2", "Open" }, 0);
    private readonly Image _img = new() { Width = 260, Height = 260, HorizontalAlignment = HorizontalAlignment.Left, SnapsToDevicePixels = true };
    private readonly Ui.Status _st = new();
    private readonly Ui.Input _result = new("The content of the code shows up here", true, 110);
    private BitmapSource? _qr;

    public QrTool(string arg)
    {
        Ui.Setup(this, "QR Code", 520, 700, true);
        AllowDrop = true;
        RenderOptions.SetBitmapScalingMode(_img, BitmapScalingMode.NearestNeighbor);

        _mode = new Ui.Chips(new[] { "Create", "Scan" }, arg == "scan" ? 1 : 0);
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(_mode);

        // ---- create ----
        _gen.Children.Add(_kind);
        var wifi = new StackPanel { Visibility = Visibility.Collapsed };
        _ssid.Margin = new Thickness(0, 0, 0, 8);
        _pass.Margin = new Thickness(0, 0, 0, 8);
        wifi.Children.Add(_ssid);
        wifi.Children.Add(_pass);
        wifi.Children.Add(_sec);
        _gen.Children.Add(_text);
        _gen.Children.Add(wifi);
        var imgHost = Ui.Card(_img, 10);
        imgHost.HorizontalAlignment = HorizontalAlignment.Left;
        imgHost.Margin = new Thickness(0, 12, 0, 12);
        imgHost.Background = Brushes.White;
        _gen.Children.Add(imgHost);
        _gen.Children.Add(Ui.Row(Ui.Btn("Save PNG", SavePng, true), Ui.Btn("Copy image", CopyImage)));
        root.Children.Add(_gen);

        // ---- scan ----
        var sc = Ui.Row(Ui.Btn("Select area on screen", () => _ = ScanAreaAsync(), true), Ui.Btn("From clipboard", () => _ = ScanClipAsync()), Ui.Btn("Open image...", OpenFile));
        _scan.Children.Add(sc);
        var h = Ui.Label("Or drop an image here. Everything is read on this PC.", 12, true);
        h.Margin = new Thickness(0, 10, 0, 8);
        _scan.Children.Add(h);
        _scan.Children.Add(_result);
        var open = Ui.Btn("Open link", OpenLink);
        _scan.Children.Add(Ui.Row(Ui.Btn("Copy", () => { if (_result.Text.Length > 0) { Ui.Copy(_result.Text); _st.Say("Copied"); } }, true), open));
        _scan.Margin = new Thickness(0, 12, 0, 0);
        root.Children.Add(_scan);
        _st.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(_st);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        void Show()
        {
            bool create = _mode.Index == 0;
            _gen.Visibility = create ? Visibility.Visible : Visibility.Collapsed;
            _scan.Visibility = create ? Visibility.Collapsed : Visibility.Visible;
        }
        _mode.Selected += _ => Show();
        _kind.Selected += i =>
        {
            wifi.Visibility = i == 1 ? Visibility.Visible : Visibility.Collapsed;
            _text.Visibility = i == 1 ? Visibility.Collapsed : Visibility.Visible;
            Make();
        };
        _text.Changed += Make;
        _ssid.Changed += Make;
        _pass.Changed += Make;
        _sec.Selected += _ => { _pass.IsEnabled = _sec.Index == 0; Make(); };
        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) { _mode.Select(1, false); Show(); _ = ScanFileAsync(f[0]); }
        };
        Show();
        Make();
        Loaded += (_, _) =>
        {
            if (arg == "scan") _ = ScanAreaAsync();
            else if (arg.Length > 0 && arg != "gen") { _text.Text = arg; }
            else _text.Focus();
        };
    }

    private static string Esc(string s) => s.Replace("\\", "\\\\").Replace(";", "\;").Replace(",", "\\,").Replace(":", "\\:").Replace("\"", "\\\"");

    private string Payload()
    {
        if (_kind.Index == 1)
        {
            if (_ssid.Text.Trim().Length == 0) return "";
            return _sec.Index == 1
                ? "WIFI:T:nopass;S:" + Esc(_ssid.Text.Trim()) + ";;"
                : "WIFI:T:WPA;S:" + Esc(_ssid.Text.Trim()) + ";P:" + Esc(_pass.Text) + ";;";
        }
        return _text.Text;
    }

    private void Make()
    {
        string p = Payload();
        if (p.Length == 0) { _img.Source = null; _qr = null; return; }
        try
        {
            var w = new ZXing.BarcodeWriterPixelData
            {
                Format = ZXing.BarcodeFormat.QR_CODE,
                Options = new ZXing.Common.EncodingOptions { Width = 520, Height = 520, Margin = 2 }
            };
            var px = w.Write(p);
            var bmp = BitmapSource.Create(px.Width, px.Height, 96, 96, PixelFormats.Bgra32, null, px.Pixels, px.Width * 4);
            bmp.Freeze();
            _qr = bmp;
            _img.Source = bmp;
        }
        catch
        {
            _img.Source = null;
            _qr = null;
            _st.Say("This is too long for a QR code.", true);
        }
    }

    private void SavePng()
    {
        if (_qr == null) { _st.Say("Write something first.", true); return; }
        var d = new Microsoft.Win32.SaveFileDialog { Filter = "PNG image|*.png", FileName = "qr-code.png" };
        if (d.ShowDialog(this) != true) return;
        try { File.WriteAllBytes(d.FileName, ScreenGrab.ToPng(_qr)); _st.Say("Saved"); }
        catch (Exception ex) { _st.Say("Could not save: " + ex.Message, true); }
    }

    private void CopyImage()
    {
        if (_qr == null) { _st.Say("Write something first.", true); return; }
        for (int i = 0; i < 5; i++)
        {
            try { Clipboard.SetImage(_qr); _st.Say("Image copied"); return; }
            catch { System.Threading.Thread.Sleep(40); }
        }
        _st.Say("Clipboard is busy, try again.", true);
    }

    // ---------------- scan ----------------

    private static string? Decode(BitmapSource bmp)
    {
        try
        {
            var conv = new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
            int w = conv.PixelWidth, h = conv.PixelHeight;
            var px = new byte[w * h * 4];
            conv.CopyPixels(px, w * 4, 0);
            var reader = new ZXing.BarcodeReaderGeneric
            {
                AutoRotate = true,
                Options = new ZXing.Common.DecodingOptions { TryHarder = true, TryInverted = true }
            };
            var r = reader.Decode(new ZXing.RGBLuminanceSource(px, w, h, ZXing.RGBLuminanceSource.BitmapFormat.BGRA32));
            return r?.Text;
        }
        catch { return null; }
    }

    private async Task ShowScanAsync(BitmapSource bmp)
    {
        _st.Info("Reading...");
        string? t = await Task.Run(() => Decode(bmp));
        if (t == null) { _result.Text = ""; _st.Say("No QR code or barcode found. Try selecting a tighter area.", true, 4000); return; }
        _result.Text = t;
        _st.Say("Found it");
    }

    private async Task ScanAreaAsync()
    {
        _mode.Select(1, false);
        _gen.Visibility = Visibility.Collapsed;
        _scan.Visibility = Visibility.Visible;
        WindowState = WindowState.Minimized;
        await Task.Delay(250);
        var o = PickOverlay.Run(false, "Drag around the QR code. Esc cancels.");
        WindowState = WindowState.Normal;
        Activate();
        if (o?.Cropped != null) await ShowScanAsync(o.Cropped);
    }

    private async Task ScanClipAsync()
    {
        BitmapSource? b = null;
        try { if (Clipboard.ContainsImage()) b = Clipboard.GetImage(); } catch { }
        if (b == null) { _st.Say("There is no image in the clipboard.", true); return; }
        await ShowScanAsync(b);
    }

    private void OpenFile()
    {
        var d = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp|All files|*.*" };
        if (d.ShowDialog(this) == true) _ = ScanFileAsync(d.FileName);
    }

    private async Task ScanFileAsync(string path)
    {
        try
        {
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.UriSource = new Uri(path);
            bi.EndInit();
            bi.Freeze();
            await ShowScanAsync(bi);
        }
        catch { _st.Say("This file is not an image.", true); }
    }

    private void OpenLink()
    {
        string t = _result.Text.Trim();
        if (Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true }); } catch { }
        }
        else _st.Say("This is not a web link.", true);
    }
}
