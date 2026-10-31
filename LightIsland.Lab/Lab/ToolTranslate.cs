using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace LightIsland;

/// <summary>Text translator. Uses the free MyMemory service (internet needed, daily limit for anonymous use).</summary>
internal sealed class TranslateTool : Window
{
    private static readonly HttpClient Http = Make();
    private readonly Ui.Input _in = new("Type or paste text (Ctrl+Enter translates)", true, 150, 15);
    private readonly Ui.Input _out = new("Translation", true, 150, 15);
    private readonly Ui.Status _st = new();
    private Ui.Picker _from = null!, _to = null!;
    private int _gen;

    public static readonly List<(string Key, string Name)> Langs = new()
    {
        ("Autodetect", "Detect language"), ("en", "English"), ("ur", "Urdu"), ("hi", "Hindi"), ("ar", "Arabic"), ("fa", "Persian"), ("bn", "Bengali"),
        ("pa", "Punjabi"), ("tr", "Turkish"), ("es", "Spanish"), ("fr", "French"), ("de", "German"), ("it", "Italian"), ("pt", "Portuguese"),
        ("ru", "Russian"), ("zh-CN", "Chinese (Simplified)"), ("zh-TW", "Chinese (Traditional)"), ("ja", "Japanese"), ("ko", "Korean"),
        ("id", "Indonesian"), ("ms", "Malay"), ("th", "Thai"), ("vi", "Vietnamese"), ("nl", "Dutch"), ("pl", "Polish"), ("uk", "Ukrainian"),
        ("cs", "Czech"), ("sv", "Swedish"), ("da", "Danish"), ("fi", "Finnish"), ("no", "Norwegian"), ("el", "Greek"), ("he", "Hebrew"),
        ("ro", "Romanian"), ("hu", "Hungarian"), ("sw", "Swahili"), ("ta", "Tamil"), ("te", "Telugu"), ("mr", "Marathi"), ("gu", "Gujarati"), ("ne", "Nepali")
    };

    private static HttpClient Make()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("LightIslandLab/2.0");
        return c;
    }

    public TranslateTool(string arg)
    {
        Ui.Setup(this, "Translator", 620, 640, true);
        var root = new Grid { Margin = new Thickness(18) };
        for (int i = 0; i < 6; i++) root.RowDefinitions.Add(new RowDefinition { Height = i == 4 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });

        _from = new Ui.Picker(Langs, "Autodetect");
        _to = new Ui.Picker(Langs.GetRange(1, Langs.Count - 1), Pref.Get("translateTo", "ur"));
        _to.Changed += () => { Pref.Set("translateTo", _to.Key); if (_in.Text.Trim().Length > 0) _ = TranslateAsync(); };
        _from.Changed += () => { if (_in.Text.Trim().Length > 0) _ = TranslateAsync(); };
        var swap = Ui.Btn("⇄", Swap, false, 40);
        swap.Margin = new Thickness(8, 0, 8, 0);
        var bar = new Grid();
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition());
        bar.Children.Add(_from);
        Grid.SetColumn(swap, 1);
        bar.Children.Add(swap);
        Grid.SetColumn(_to, 2);
        bar.Children.Add(_to);
        root.Children.Add(bar);

        _in.Margin = new Thickness(0, 12, 0, 8);
        Grid.SetRow(_in, 1);
        root.Children.Add(_in);

        var btns = Ui.Row(Ui.Btn("Translate", () => _ = TranslateAsync(), true), Ui.Btn("Paste", () => { try { if (Clipboard.ContainsText()) _in.Text = Clipboard.GetText(); } catch { } }), Ui.Btn("Clear", () => { _in.Text = ""; _out.Text = ""; }));
        Grid.SetRow(btns, 2);
        root.Children.Add(btns);

        _out.Margin = new Thickness(0, 10, 0, 8);
        _out.Box.IsReadOnly = true;
        Grid.SetRow(_out, 3);
        root.Children.Add(_out);

        var b2 = Ui.Row(Ui.Btn("Copy translation", () => { if (_out.Text.Length > 0) { Ui.Copy(_out.Text); _st.Say("Copied"); } }, true));
        Grid.SetRow(b2, 4);
        b2.VerticalAlignment = VerticalAlignment.Top;
        root.Children.Add(b2);

        var foot = new StackPanel();
        foot.Children.Add(_st);
        foot.Children.Add(Ui.Label("Free service (MyMemory): needs internet, limited to a few thousand characters per day.", 11, true));
        Grid.SetRow(foot, 5);
        root.Children.Add(foot);
        Content = root;

        _in.Box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; _ = TranslateAsync(); } };
        Loaded += (_, _) =>
        {
            if (arg == "clip") { try { if (Clipboard.ContainsText()) { _in.Text = Clipboard.GetText(); _ = TranslateAsync(); } } catch { } }
            else if (arg.Length > 0) { _in.Text = arg; _ = TranslateAsync(); }
            _in.Focus();
        };
    }

    private void Swap()
    {
        string f = _from.Key, t = _to.Key;
        if (f == "Autodetect") f = "en";
        _from.Set(t);
        _to.Set(f);
        string o = _out.Text;
        if (o.Length > 0) { _in.Text = o; _out.Text = ""; _ = TranslateAsync(); }
    }

    private async Task TranslateAsync()
    {
        string text = _in.Text.Trim();
        if (text.Length == 0) return;
        int gen = ++_gen;
        _st.Info("Translating...");
        try
        {
            var sb = new StringBuilder();
            foreach (string chunk in Split(text, 450))
            {
                string r = await OneAsync(chunk, _from.Key, _to.Key);
                if (gen != _gen) return;
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(r);
            }
            _out.Text = sb.ToString();
            _st.Say("Done", false, 1200);
        }
        catch (Exception ex)
        {
            if (gen == _gen) _st.Say(ex.Message, true, 8000);
        }
    }

    private static IEnumerable<string> Split(string t, int max)
    {
        var parts = System.Text.RegularExpressions.Regex.Split(t, @"(?<=[\.\!\?\n،۔])\s*");
        var cur = new StringBuilder();
        foreach (string p in parts)
        {
            if (p.Length == 0) continue;
            string q = p;
            while (q.Length > max) { if (cur.Length > 0) { yield return cur.ToString(); cur.Clear(); } yield return q.Substring(0, max); q = q.Substring(max); }
            if (cur.Length + q.Length + 1 > max) { yield return cur.ToString(); cur.Clear(); }
            if (cur.Length > 0) cur.Append(' ');
            cur.Append(q);
        }
        if (cur.Length > 0) yield return cur.ToString();
    }

    private static async Task<string> OneAsync(string text, string from, string to)
    {
        string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(text) + "&langpair=" + Uri.EscapeDataString(from + "|" + to);
        using var resp = await Http.GetAsync(url);
        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        int status = 200;
        if (root.TryGetProperty("responseStatus", out var st))
            status = st.ValueKind == JsonValueKind.Number ? st.GetInt32() : int.TryParse(st.GetString(), out int v) ? v : 200;
        string tr = root.GetProperty("responseData").GetProperty("translatedText").GetString() ?? "";
        if (status != 200 || tr.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
        {
            if (tr.Contains("QUOTA", StringComparison.OrdinalIgnoreCase) || tr.Contains("MYMEMORY WARNING", StringComparison.OrdinalIgnoreCase))
                throw new Exception("The free daily limit of the translation service is used up. Try again later.");
            throw new Exception("Translation failed (" + status + ")." + (tr.Length > 0 && tr.Length < 120 ? " " + tr : ""));
        }
        return System.Net.WebUtility.HtmlDecode(tr);
    }
}
