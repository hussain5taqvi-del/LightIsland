using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>Disposable inbox from mail.tm (free). For sign-ups and tests, not for anything important.</summary>
internal sealed class MailTool : Window
{
    private static readonly HttpClient Http = Make();
    private static HttpClient Make()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20), BaseAddress = new Uri("https://api.mail.tm/") };
        c.DefaultRequestHeaders.Add("Accept", "application/json");
        return c;
    }

    private string _addr = "", _pass = "", _token = "", _accId = "";
    private readonly TextBlock _addrTx = new() { FontSize = 18, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new();
    private readonly Grid _listView = new(), _detail = new();
    private readonly TextBox _body = new();
    private readonly TextBlock _subj = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _from = new() { FontSize = 12, Foreground = Ui.Dim };
    private readonly Ui.Status _st = new();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(10) };
    private bool _busy;
    private readonly HashSet<string> _seen = new();

    public MailTool()
    {
        Ui.Setup(this, "Temp Mail", 560, 700, true);
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel();
        top.Children.Add(Ui.Label("YOUR TEMPORARY ADDRESS", 11, true, true));
        _addrTx.Margin = new Thickness(0, 4, 0, 10);
        _addrTx.Text = "Creating...";
        top.Children.Add(_addrTx);
        top.Children.Add(Ui.Row(Ui.Btn("Copy address", () => { if (_addr.Length > 0) { Ui.Copy(_addr); _st.Say("Copied"); } }, true), Ui.Btn("Refresh", () => _ = CheckAsync(true)), Ui.Btn("New address", () => _ = NewAccountAsync(true))));
        _st.Margin = new Thickness(0, 2, 0, 4);
        top.Children.Add(_st);
        top.Children.Add(Ui.Label("Anyone who knows the address can read it. Use it only for throw-away sign-ups.", 11.5, true));
        root.Children.Add(top);

        // inbox
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list, Margin = new Thickness(0, 12, 0, 0) };
        _listView.Children.Add(sv);
        Grid.SetRow(_listView, 1);
        root.Children.Add(_listView);

        // message
        _detail.Visibility = Visibility.Collapsed;
        _detail.Margin = new Thickness(0, 12, 0, 0);
        _detail.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _detail.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new StackPanel();
        head.Children.Add(Ui.Row(Ui.Btn("< Inbox", ShowList), Ui.Btn("Copy text", () => { Ui.Copy(_body.Text); _st.Say("Copied"); })));
        head.Children.Add(_subj);
        _from.Margin = new Thickness(0, 2, 0, 8);
        head.Children.Add(_from);
        _detail.Children.Add(head);
        _body.IsReadOnly = true;
        _body.TextWrapping = TextWrapping.Wrap;
        _body.Background = Ui.Panel;
        _body.Foreground = Ui.Text;
        _body.BorderBrush = Ui.Line;
        _body.Padding = new Thickness(10);
        _body.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        Grid.SetRow(_body, 1);
        _detail.Children.Add(_body);
        Grid.SetRow(_detail, 1);
        root.Children.Add(_detail);
        Content = root;

        _poll.Tick += (_, _) => _ = CheckAsync(false);
        Loaded += (_, _) => _ = StartAsync();
        Closed += (_, _) => _poll.Stop();
    }

    private static string Rand(int n, string chars)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(chars[RandomNumberGenerator.GetInt32(chars.Length)]);
        return sb.ToString();
    }

    private async Task StartAsync()
    {
        // reuse the last address while it still works
        try
        {
            string f = ToolHost.DataFile("mail.json");
            if (File.Exists(f))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(f));
                _addr = d.RootElement.GetProperty("a").GetString() ?? "";
                _pass = d.RootElement.GetProperty("p").GetString() ?? "";
                if (_addr.Length > 0 && await LoginAsync()) { Done(); return; }
            }
        }
        catch { }
        await NewAccountAsync(false);
    }

    private void Done()
    {
        _addrTx.Text = _addr;
        _poll.Start();
        _ = CheckAsync(false);
    }

    private async Task<bool> LoginAsync()
    {
        try
        {
            using var r = await Http.PostAsync("token", new StringContent(JsonSerializer.Serialize(new { address = _addr, password = _pass }), Encoding.UTF8, "application/json"));
            if (!r.IsSuccessStatusCode) return false;
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            _token = d.RootElement.GetProperty("token").GetString() ?? "";
            _accId = d.RootElement.TryGetProperty("id", out var id) ? id.GetString() ?? "" : "";
            return _token.Length > 0;
        }
        catch { return false; }
    }

    private async Task NewAccountAsync(bool deleteOld)
    {
        if (_busy) return;
        _busy = true;
        _poll.Stop();
        _addrTx.Text = "Creating...";
        _st.Info("");
        try
        {
            if (deleteOld && _token.Length > 0 && _accId.Length > 0)
            {
                try
                {
                    using var rq = new HttpRequestMessage(HttpMethod.Delete, "accounts/" + _accId);
                    rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
                    using var _ = await Http.SendAsync(rq);
                }
                catch { }
            }
            string domain;
            using (var r = await Http.GetAsync("domains"))
            {
                r.EnsureSuccessStatusCode();
                using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
                var arr = d.RootElement.TryGetProperty("hydra:member", out var m) ? m : d.RootElement;
                domain = "";
                foreach (var e in arr.EnumerateArray())
                {
                    bool active = !e.TryGetProperty("isActive", out var ia) || ia.ValueKind != JsonValueKind.False;
                    if (active) { domain = e.GetProperty("domain").GetString() ?? ""; break; }
                }
                if (domain.Length == 0) throw new Exception("no domain");
            }
            string addr = Rand(10, "abcdefghijklmnopqrstuvwxyz0123456789") + "@" + domain;
            string pass = Rand(18, "abcdefghjkmnpqrstuvwxyzABCDEFGHJKMNPQRSTUVWXYZ23456789");
            using (var r = await Http.PostAsync("accounts", new StringContent(JsonSerializer.Serialize(new { address = addr, password = pass }), Encoding.UTF8, "application/json")))
            {
                if (!r.IsSuccessStatusCode) throw new Exception("create " + (int)r.StatusCode);
            }
            _addr = addr;
            _pass = pass;
            if (!await LoginAsync()) throw new Exception("login");
            try { File.WriteAllText(ToolHost.DataFile("mail.json"), JsonSerializer.Serialize(new { a = _addr, p = _pass })); } catch { }
            _seen.Clear();
            _list.Children.Clear();
            ShowList();
            Done();
        }
        catch
        {
            _addrTx.Text = "Could not create an address";
            _st.Say("Check your internet connection and press \"New address\".", true, 0);
        }
        finally { _busy = false; }
    }

    private async Task CheckAsync(bool manual)
    {
        if (_token.Length == 0) return;
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, "messages");
            rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var r = await Http.SendAsync(rq);
            if (r.StatusCode == System.Net.HttpStatusCode.Unauthorized) { if (await LoginAsync()) { if (manual) await CheckAsync(false); } return; }
            r.EnsureSuccessStatusCode();
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var arr = d.RootElement.TryGetProperty("hydra:member", out var m) ? m : d.RootElement;
            _list.Children.Clear();
            int n = 0;
            bool fresh = false;
            foreach (var e in arr.EnumerateArray())
            {
                n++;
                string id = e.GetProperty("id").GetString() ?? "";
                string subj = e.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
                string from = "";
                if (e.TryGetProperty("from", out var f)) from = (f.TryGetProperty("name", out var fn) ? fn.GetString() : "") is { Length: > 0 } nm ? nm : (f.TryGetProperty("address", out var fa) ? fa.GetString() ?? "" : "");
                string intro = e.TryGetProperty("intro", out var i) ? i.GetString() ?? "" : "";
                if (_seen.Add(id) && _seen.Count > 1) fresh = true;
                AddRow(id, string.IsNullOrWhiteSpace(subj) ? "(no subject)" : subj, from, intro);
            }
            if (n == 0) _list.Children.Add(Ui.Label("Inbox is empty. New mail appears here by itself (checked every 10 seconds).", 13, true));
            if (fresh) { try { System.Media.SystemSounds.Asterisk.Play(); } catch { } }
            if (manual) _st.Say("Up to date");
        }
        catch
        {
            if (manual) _st.Say("Could not reach the mail server.", true);
        }
    }

    private void AddRow(string id, string subj, string from, string intro)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.Label(subj, 14, false, true));
        sp.Children.Add(Ui.Label(from, 12, true));
        if (intro.Length > 0)
        {
            var t = Ui.Label(intro, 12, true);
            t.TextTrimming = TextTrimming.CharacterEllipsis;
            t.MaxHeight = 34;
            sp.Children.Add(t);
        }
        var card = Ui.Card(sp, 12);
        card.Margin = new Thickness(0, 0, 0, 8);
        card.Cursor = Cursors.Hand;
        card.MouseLeftButtonUp += (_, _) => _ = OpenAsync(id);
        _list.Children.Add(card);
    }

    private void ShowList() { _detail.Visibility = Visibility.Collapsed; _listView.Visibility = Visibility.Visible; }

    private async Task OpenAsync(string id)
    {
        try
        {
            using var rq = new HttpRequestMessage(HttpMethod.Get, "messages/" + id);
            rq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _token);
            using var r = await Http.SendAsync(rq);
            r.EnsureSuccessStatusCode();
            using var d = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var e = d.RootElement;
            string text = e.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(text) && e.TryGetProperty("html", out var h))
            {
                string html = h.ValueKind == JsonValueKind.Array ? string.Join("\n", System.Linq.Enumerable.Select(h.EnumerateArray(), x => x.GetString() ?? "")) : h.GetString() ?? "";
                html = Regex.Replace(html, @"<(script|style)[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</tr>|</h\d>", "\n", RegexOptions.IgnoreCase);
                html = Regex.Replace(html, @"<a [^>]*href=""([^""]+)""[^>]*>(.*?)</a>", "$2 ($1)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
                html = Regex.Replace(html, @"<[^>]+>", "");
                text = System.Net.WebUtility.HtmlDecode(html);
                text = Regex.Replace(text, @"\n\s*\n\s*\n+", "\n\n").Trim();
            }
            _subj.Text = e.TryGetProperty("subject", out var s) ? s.GetString() ?? "" : "";
            string from = "";
            if (e.TryGetProperty("from", out var f)) from = (f.TryGetProperty("name", out var fn) ? fn.GetString() : "") + " <" + (f.TryGetProperty("address", out var fa) ? fa.GetString() : "") + ">";
            _from.Text = from;
            _body.Text = text;
            _listView.Visibility = Visibility.Collapsed;
            _detail.Visibility = Visibility.Visible;
        }
        catch { _st.Say("Could not open the message.", true); }
    }
}
