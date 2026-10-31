using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using WLang = Windows.Globalization;
using WOcr = Windows.Media.Ocr;

namespace LightIsland;

/// <summary>Reads the text of a picture with Windows' own offline OCR (nothing is uploaded).</summary>
internal sealed class OcrTool : Window
{
    private readonly Ui.Input _out = new("The text of the picture shows up here", true, 0);
    private readonly Ui.Status _st = new();
    private readonly Image _preview = new() { MaxHeight = 120, Stretch = System.Windows.Media.Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
    private Ui.Chips? _langs;
    private WOcr.OcrEngine[] _engines = Array.Empty<WOcr.OcrEngine>();
    private byte[]? _png;

    public OcrTool(string arg)
    {
        Ui.Setup(this, "OCR", 560, 620, true);
        AllowDrop = true;

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var top = Ui.Row(
            Ui.Btn("Select area on screen", () => _ = PickAreaAsync(), true),
            Ui.Btn("From clipboard image", () => _ = FromClipboardAsync()),
            Ui.Btn("Open image...", OpenFile));
        root.Children.Add(top);

        var hint = Ui.Label("You can also drop an image file here. Text is read on this PC, nothing is sent anywhere.", 12, true);
        hint.Margin = new Thickness(0, 10, 0, 8);
        Grid.SetRow(hint, 1);
        root.Children.Add(hint);

        _preview.Margin = new Thickness(0, 0, 0, 10);
        var lp = new StackPanel();
        lp.Children.Add(_preview);
        Grid.SetRow(lp, 2);
        root.Children.Add(lp);

        _out.Margin = new Thickness(0, 0, 0, 10);
        _out.Box.FontSize = 14;
        Grid.SetRow(_out, 3);
        root.Children.Add(_out);

        var bottom = new StackPanel();
        _langs = BuildLangs();
        if (_langs != null) bottom.Children.Add(_langs);
        bottom.Children.Add(Ui.Row(Ui.Btn("Copy text", () => { if (_out.Text.Length > 0) { Ui.Copy(_out.Text); _st.Say("Copied"); } }, true), Ui.Btn("Clear", () => { _out.Text = ""; _preview.Source = null; })));
        bottom.Children.Add(_st);
        Grid.SetRow(bottom, 4);
        root.Children.Add(bottom);
        Content = root;

        Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) _ = FromFileAsync(f[0]);
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox) _ = FromClipboardAsync();
        };

        Loaded += (_, _) =>
        {
            if (arg == "area") _ = PickAreaAsync();
            else if (arg == "clip") _ = FromClipboardAsync();
        };
    }

    private Ui.Chips? BuildLangs()
    {
        try
        {
            var list = WOcr.OcrEngine.AvailableRecognizerLanguages.ToList();
            var engines = new System.Collections.Generic.List<WOcr.OcrEngine>();
            var names = new System.Collections.Generic.List<string>();
            WOcr.OcrEngine? def = WOcr.OcrEngine.TryCreateFromUserProfileLanguages();
            foreach (WLang.Language l in list)
            {
                var e = WOcr.OcrEngine.TryCreateFromLanguage(l);
                if (e == null) continue;
                engines.Add(e);
                names.Add(l.DisplayName.Split('(')[0].Trim());
            }
            _engines = engines.ToArray();
            if (_engines.Length == 0)
            {
                _st.Say("No OCR language is installed. Add one in Windows Settings > Time & language > Language.", true, 0);
                return null;
            }
            int idx = 0;
            if (def != null) for (int i = 0; i < _engines.Length; i++) if (_engines[i].RecognizerLanguage.LanguageTag == def.RecognizerLanguage.LanguageTag) idx = i;
            var chips = new Ui.Chips(names.ToArray(), idx);
            chips.Margin = new Thickness(0, 0, 0, 6);
            return chips;
        }
        catch (Exception ex)
        {
            _st.Say("OCR is not available: " + ex.Message, true, 0);
            return null;
        }
    }

    private async Task PickAreaAsync()
    {
        WindowState = WindowState.Minimized;
        await Task.Delay(350);
        PickOverlay? o = null;
        try { o = PickOverlay.Run(false, "Drag over the text you want to read"); }
        finally { WindowState = WindowState.Normal; Activate(); }
        if (o?.Cropped == null) return;
        await RunOcrAsync(ScreenGrab.ToPng(o.Cropped), o.Cropped);
    }

    private async Task FromClipboardAsync()
    {
        try
        {
            if (!Clipboard.ContainsImage()) { _st.Say("There is no image in the clipboard.", true); return; }
            BitmapSource? src = Clipboard.GetImage();
            if (src == null) { _st.Say("Could not read the clipboard image.", true); return; }
            await RunOcrAsync(ScreenGrab.ToPng(src), src);
        }
        catch (Exception ex) { _st.Say(ex.Message, true); }
    }

    private void OpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|All files|*.*" };
        if (dlg.ShowDialog(this) == true) _ = FromFileAsync(dlg.FileName);
    }

    private async Task FromFileAsync(string path)
    {
        try
        {
            byte[] data = await File.ReadAllBytesAsync(path);
            BitmapImage? bi = null;
            try
            {
                bi = new BitmapImage();
                bi.BeginInit();
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.DecodePixelWidth = 600;
                bi.StreamSource = new MemoryStream(data);
                bi.EndInit();
                bi.Freeze();
            }
            catch { bi = null; }
            await RunOcrAsync(data, bi);
        }
        catch (Exception ex) { _st.Say("Could not open the file: " + ex.Message, true); }
    }

    private async Task RunOcrAsync(byte[] png, BitmapSource? preview)
    {
        if (_engines.Length == 0) { _st.Say("No OCR language is installed.", true, 0); return; }
        _png = png;
        if (preview != null) _preview.Source = preview;
        _st.Info("Reading...");
        try
        {
            var eng = _engines[Math.Clamp(_langs?.Index ?? 0, 0, _engines.Length - 1)];
            string text = await WinRtOps.OcrBytesAsync(eng, png);
            text = text.Trim();
            _out.Text = text;
            if (text.Length == 0) _st.Say("No text found. Try a bigger area, or another language below.", true);
            else { Ui.Copy(text); _st.Say("Done - the text is copied to the clipboard"); }
        }
        catch (Exception ex) { _st.Say("Could not read the picture: " + ex.Message, true); }
    }
}
