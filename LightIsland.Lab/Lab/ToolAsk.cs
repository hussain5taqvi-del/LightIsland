using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightIsland;

internal sealed class AiConfig
{
    public string Provider { get; set; } = "Anthropic";
    public string Model { get; set; } = "claude-haiku-5-5";
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string KeyProtected { get; set; } = "";

    private static string F => ToolHost.DataFile("ai.json");

    public static AiConfig Load()
    {
        try { if (File.Exists(F)) return JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(F)) ?? new AiConfig(); }
        catch { }
        return new AiConfig();
    }

    public void Save() { try { File.WriteAllText(F, JsonSerializer.Serialize(this)); } catch { } }

    public string Key
    {
        get
        {
            try { return KeyProtected.Length == 0 ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(KeyProtected), null, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        set
        {
            KeyProtected = string.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
    }
}

/// <summary>
/// Quick AI bar (Ctrl+Alt+Space). Uses your own API key (Anthropic or any OpenAI-compatible service), so nothing runs locally.
/// Also runs commands: /ocr /color /timer /translate /remind /qr /convert /mail /ram /wifi /snippets /crypto /ticker.
/// </summary>
internal sealed class AskTool : Window
{
    private sealed record Msg(string Role, string Text);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private static Mutex? _mutex;
    private static EventWaitHandle? _quit;

    private readonly List<Msg> _hist = new();
    private readonly StackPanel _chat = new();
    private readonly ScrollViewer _scroll = new();
    private readonly Ui.Input _in = new("Ask anything, or type / for commands (Enter = send)", true, 0, 14);
    private readonly Ui.Status _st = new();
    private readonly Border _settings;
    private Ui.Chips _prov = null!;
    private readonly Ui.Input _model = new("Model", false, 0), _base = new("Base URL", false, 0);
    private readonly PasswordBox _key = new();
    private AiConfig _cfg = AiConfig.Load();
    private bool _busy;
    private string _lastAnswer = "";

    public static Window? Create()
    {
        _mutex = new Mutex(true, "LightIslandLab.Ask", out bool created);
        _quit = new EventWaitHandle(false, EventResetMode.AutoReset, "LightIslandLab.Ask.Quit");
        if (!created) { _quit.Set(); return null; }
        return new AskTool();
    }

    private AskTool()
    {
        Ui.Setup(this, "Ask AI", 640, 620, true);
        var root = new Grid { Margin = new Thickness(16) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // header + settings
        var head = new StackPanel();
        var hr = Ui.Row(Ui.Btn("New chat", NewChat), Ui.Btn("Clipboard text", UseClipboard), Ui.Btn("Copy answer", () => { if (_lastAnswer.Length > 0) { Ui.Copy(_lastAnswer); _st.Say("Copied"); } }), Ui.Btn("Settings", () => ToggleSettings()));
        head.Children.Add(hr);

        _prov = new Ui.Chips(new[] { "Anthropic", "OpenAI-compatible" }, _cfg.Provider == "Anthropic" ? 0 : 1);
        _key.Background = Ui.Panel; _key.Foreground = Ui.Text; _key.BorderBrush = Ui.Line; _key.Padding = new Thickness(10, 8, 10, 8); _key.CaretBrush = Ui.Text;
        var sp = new StackPanel();
        sp.Children.Add(Ui.Label("AI provider", 13, false, true));
        _prov.Margin = new Thickness(0, 6, 0, 4);
        sp.Children.Add(_prov);
        _model.Text = _cfg.Model; _model.Margin = new Thickness(0, 0, 0, 8);
        _base.Text = _cfg.BaseUrl; _base.Margin = new Thickness(0, 0, 0, 8);
        sp.Children.Add(_model);
        sp.Children.Add(_base);
        _key.Margin = new Thickness(0, 0, 0, 8);
        sp.Children.Add(_key);
        sp.Children.Add(Ui.Label("Paste your own API key. It is stored encrypted for your Windows account on this PC and is only sent to the provider you picked.", 11.5, true));
        var sr = Ui.Row(Ui.Btn("Save", SaveSettings, true), Ui.Btn("Close", () => ToggleSettings(false)));
        sr.Margin = new Thickness(0, 8, 0, 0);
        sp.Children.Add(sr);
        _settings = Ui.Card(sp, 14);
        _settings.Margin = new Thickness(0, 0, 0, 10);
        _settings.Visibility = Visibility.Collapsed;
        head.Children.Add(_settings);
        _prov.Selected += _ => _base.Visibility = _prov.Index == 1 ? Visibility.Visible : Visibility.Collapsed;
        _base.Visibility = _prov.Index == 1 ? Visibility.Visible : Visibility.Collapsed;
        root.Children.Add(head);

        _scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _scroll.Content = _chat;
        _scroll.Margin = new Thickness(0, 10, 0, 10);
        Grid.SetRow(_scroll, 1);
        root.Children.Add(_scroll);
        ShowHello();

        var bottom = new StackPanel();
        _in.Box.MinHeight = 44;
        _in.Box.MaxHeight = 140;
        bottom.Children.Add(_in);
        _st.Margin = new Thickness(0, 6, 0, 0);
        bottom.Children.Add(_st);
        Grid.SetRow(bottom, 2);
        root.Children.Add(bottom);
        Content = root;

        _in.Box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0) { e.Handled = true; _ = SendAsync(); }
            else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        };
        Loaded += (_, _) =>
        {
            _in.Focus();
            if (_cfg.Key.Length == 0) { ToggleSettings(true); _st.Say("Add your API key to start (Settings).", false, 0); }
            var th = new Thread(() => { try { _quit!.WaitOne(); Dispatcher.BeginInvoke(new Action(Close)); } catch { } }) { IsBackground = true };
            th.Start();
        };
        Closed += (_, _) => { try { _mutex?.ReleaseMutex(); } catch { } };
    }

    private void ShowHello()
    {
        _chat.Children.Clear();
        var t = Ui.Label("Ask a question or give a command:\n/ocr   /color   /timer   /translate text   /remind call mom in 20 min\n/qr text   /convert   /mail   /ram   /wifi   /snippets   /crypto   /ticker", 12.5, true);
        t.Margin = new Thickness(2, 4, 2, 4);
        _chat.Children.Add(t);
    }

    private void NewChat() { _hist.Clear(); _lastAnswer = ""; ShowHello(); _in.Focus(); }

    private void UseClipboard()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                string c = Clipboard.GetText();
                _in.Text = (_in.Text.Length > 0 ? _in.Text + "\n\n" : "") + c;
                _in.Box.CaretIndex = _in.Text.Length;
                _in.Focus();
            }
            else _st.Say("The clipboard has no text.", true);
        }
        catch { _st.Say("Clipboard is busy.", true); }
    }

    private void ToggleSettings(bool? show = null)
    {
        bool on = show ?? _settings.Visibility != Visibility.Visible;
        _settings.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveSettings()
    {
        _cfg.Provider = _prov.Index == 0 ? "Anthropic" : "OpenAI-compatible";
        _cfg.Model = _model.Text.Trim().Length > 0 ? _model.Text.Trim() : (_prov.Index == 0 ? "claude-haiku-5-5" : "gpt-4o-mini");
        _cfg.BaseUrl = _base.Text.Trim().TrimEnd('/');
        if (_key.Password.Length > 0) _cfg.Key = _key.Password.Trim();
        _cfg.Save();
        _key.Password = "";
        _st.Say("Saved");
        ToggleSettings(false);
        _in.Focus();
    }

    private void Bubble(string text, bool user, bool error = false)
    {
        var tb = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            Foreground = error ? Ui.Bad : Ui.Text,
            FontSize = 14,
            SelectionBrush = Ui.Accent
        };
        var b = new Border
        {
            Background = user ? Ui.Panel2 : Ui.Panel,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = user ? new Thickness(60, 0, 0, 8) : new Thickness(0, 0, 60, 8),
            Child = tb,
            HorizontalAlignment = user ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1)
        };
        if (_chat.Children.Count == 1 && _hist.Count <= 1 && _chat.Children[0] is TextBlock) _chat.Children.Clear();
        _chat.Children.Add(b);
        _scroll.ScrollToBottom();
    }

    private bool Command(string text)
    {
        if (!text.StartsWith("/")) return false;
        int sp = text.IndexOf(' ');
        string cmd = (sp < 0 ? text : text.Substring(0, sp)).Substring(1).ToLowerInvariant();
        string arg = sp < 0 ? "" : text.Substring(sp + 1).Trim();
        switch (cmd)
        {
            case "ocr": ToolHost.Launch("ocr", "area"); break;
            case "color": case "colour": case "picker": ToolHost.Launch("color", arg); break;
            case "timer": case "pomodoro": ToolHost.Launch("timer", arg); break;
            case "translate": case "tr": ToolHost.Launch("translate", arg); break;
            case "qr": ToolHost.Launch("qr", arg); break;
            case "scan": ToolHost.Launch("qr", "scan"); break;
            case "convert": case "unit": ToolHost.Launch("convert"); break;
            case "mail": case "email": ToolHost.Launch("mail"); break;
            case "ram": case "perf": case "boost": ToolHost.Launch("ram"); break;
            case "wifi": case "bluetooth": case "bt": ToolHost.Launch("net"); break;
            case "snippets": case "clips": ToolHost.Launch("snippets"); break;
            case "crypto": case "weather": ToolHost.Launch("crypto"); break;
            case "ticker": ToolHost.Launch("ticker"); break;
            case "remind":
                {
                    var p = Reminders.Parse(arg, DateTime.Now);
                    if (p == null) { Bubble("Add a time, e.g. /remind call mom in 20 min", false, true); return true; }
                    var l = Reminders.Load();
                    l.Add(new ReminderItem { Text = p.Value.Text, DueUtc = p.Value.Due.ToUniversalTime() });
                    Reminders.Save(l);
                    Bubble("Reminder set for " + p.Value.Due.ToString("ddd d MMM, h:mm tt", System.Globalization.CultureInfo.InvariantCulture) + ": " + p.Value.Text, false);
                    return true;
                }
            default:
                Bubble("Unknown command. Try /ocr /color /timer /translate /remind /qr /convert /mail /ram /wifi /snippets /crypto /ticker", false, true);
                return true;
        }
        Close();
        return true;
    }

    private async Task SendAsync()
    {
        if (_busy) return;
        string text = _in.Text.Trim();
        if (text.Length == 0) return;
        if (Command(text)) { _in.Text = ""; return; }

        _cfg = AiConfig.Load();
        string key = _cfg.Key;
        if (key.Length == 0) { ToggleSettings(true); _st.Say("Add your API key first.", true, 4000); return; }

        _busy = true;
        _in.Text = "";
        Bubble(text, true);
        _hist.Add(new Msg("user", text));
        while (_hist.Count > 16) _hist.RemoveAt(0);
        _st.Info("Thinking...");
        try
        {
            string ans = _cfg.Provider == "Anthropic" ? await AskAnthropicAsync(key) : await AskOpenAiAsync(key);
            _hist.Add(new Msg("assistant", ans));
            _lastAnswer = ans;
            Bubble(ans, false);
            _st.Text = "";
        }
        catch (Exception ex)
        {
            if (_hist.Count > 0 && _hist[^1].Role == "user") _hist.RemoveAt(_hist.Count - 1);
            Bubble(ex.Message, false, true);
            _st.Text = "";
        }
        finally { _busy = false; _in.Focus(); }
    }

    private const string System_ = "You are a fast, concise assistant inside a Windows app. Answer clearly and briefly. Use plain text, no markdown headings.";

    private static string ErrorText(string body, int code)
    {
        try
        {
            using var d = JsonDocument.Parse(body);
            if (d.RootElement.TryGetProperty("error", out var e))
            {
                string? m = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("message", out var mm) ? mm.GetString() : e.GetString();
                if (!string.IsNullOrEmpty(m)) return "The AI service said: " + m;
            }
        }
        catch { }
        return code == 401 || code == 403 ? "The API key was not accepted. Check it in Settings." : "The AI service returned an error (" + code + ").";
    }

    private async Task<string> AskAnthropicAsync(string key)
    {
        var msgs = _hist.Select(m => new { role = m.Role, content = m.Text }).ToList();
        var body = JsonSerializer.Serialize(new { model = _cfg.Model, max_tokens = 1500, system = System_, messages = msgs });
        using var rq = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        rq.Headers.Add("x-api-key", key);
        rq.Headers.Add("anthropic-version", "2023-06-01");
        using var r = await SendAsync(rq);
        string s = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Exception(ErrorText(s, (int)r.StatusCode));
        using var d = JsonDocument.Parse(s);
        var sb = new StringBuilder();
        foreach (var c in d.RootElement.GetProperty("content").EnumerateArray())
            if (c.TryGetProperty("text", out var t)) sb.Append(t.GetString());
        string res = sb.ToString().Trim();
        return res.Length > 0 ? res : "(empty answer)";
    }

    private async Task<string> AskOpenAiAsync(string key)
    {
        var msgs = new List<object> { new { role = "system", content = System_ } };
        msgs.AddRange(_hist.Select(m => (object)new { role = m.Role, content = m.Text }));
        var body = JsonSerializer.Serialize(new { model = _cfg.Model, messages = msgs });
        string url = (_cfg.BaseUrl.Length > 0 ? _cfg.BaseUrl : "https://api.openai.com/v1") + "/chat/completions";
        using var rq = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        rq.Headers.Add("Authorization", "Bearer " + key);
        using var r = await SendAsync(rq);
        string s = await r.Content.ReadAsStringAsync();
        if (!r.IsSuccessStatusCode) throw new Exception(ErrorText(s, (int)r.StatusCode));
        using var d = JsonDocument.Parse(s);
        string res = (d.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "").Trim();
        return res.Length > 0 ? res : "(empty answer)";
    }

    private static async Task<HttpResponseMessage> SendAsync(HttpRequestMessage rq)
    {
        try { return await Http.SendAsync(rq); }
        catch (TaskCanceledException) { throw new Exception("The AI service took too long to answer."); }
        catch (HttpRequestException) { throw new Exception("Could not reach the AI service. Check your internet connection."); }
    }
}
