using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LightIsland;

/// <summary>What the user asked for, in a form the app can act on.</summary>
internal sealed class VoiceIntent
{
    public string Action = "unknown";   // open, web_search, youtube, food_search, volume, media, timer, unknown
    public string Query = "";
    public string Channel = "";
    public int DaysAgo;                 // youtube: 0 = newest
    public int Value = -1;              // volume 0-100, timer seconds
    public string Source = "";          // "rule" or "ai" (for the log)
}

/// <summary>
/// Offline voice commands: whisper.cpp turns speech into text, simple rules catch the common phrases at once, and a small
/// local language model (Qwen2.5, via llama.cpp) understands the rest. Everything runs on this PC in separate helper
/// programs that exit when they are done, so the island itself stays within its small memory limit.
/// </summary>
internal static class VoiceBrain
{
    public static string Dir => Path.Combine(Tools.Base, "voice");
    private static string Model(string name) => Path.Combine(Dir, "models", name);
    private static string WhisperExe => FindExe(Path.Combine(Dir, "whisper"), "whisper-cli.exe");
    private static string LlamaExe => FindExe(Path.Combine(Dir, "llama"), "llama-completion.exe", "llama-cli.exe");
    private static string WhisperModel => Model("ggml-base.bin");
    private static string LlamaModel => Model("qwen2.5-1.5b-instruct-q4_k_m.gguf");

    private static string FindExe(string dir, params string[] names)
    {
        foreach (string n in names) { string p = Path.Combine(dir, n); if (File.Exists(p)) return p; }
        return "";
    }

    public static bool Available => WhisperExe.Length > 0 && File.Exists(WhisperModel);
    public static bool AiAvailable => LlamaExe.Length > 0 && File.Exists(LlamaModel);

    private static int Threads => Math.Clamp(Environment.ProcessorCount - 1, 2, 8);

    // ---------- Speech to text ----------

    public static async Task<string> TranscribeAsync(byte[] pcm, CancellationToken ct)
    {
        Directory.CreateDirectory(Tools.CacheDir);
        string wav = Path.Combine(Tools.CacheDir, "voice-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
        try
        {
            MicRecorder.WriteWav(wav, pcm);
            var sb = new StringBuilder();
            // -l auto + -tr: any spoken language comes out as English text; greedy decoding for speed
            int code = await Proc.RunAsync(WhisperExe,
                new[] { "-m", WhisperModel, "-f", wav, "-nt", "-np", "-l", "auto", "-tr", "-t", Threads.ToString(CultureInfo.InvariantCulture), "-bs", "1", "-bo", "1" },
                l => { lock (sb) sb.Append(l).Append(' '); }, null, ct, null, ProcessPriorityClass.Normal);
            if (code != 0) throw new InvalidOperationException("The speech engine failed");
            string text;
            lock (sb) text = sb.ToString();
            text = Regex.Replace(text, @"\[[^\]]*\]|\([^)]*\)|\*[^*]*\*", " "); // [BLANK_AUDIO], (music) ...
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
        finally { try { File.Delete(wav); } catch { } }
    }

    // ---------- Understanding: rules first (instant), the model for the rest ----------

    public static async Task<VoiceIntent> UnderstandAsync(string text, CancellationToken ct)
    {
        VoiceIntent? q = QuickParse(text);
        if (q != null) return q;
        if (!AiAvailable) return new VoiceIntent { Action = "unknown", Query = text };
        try { return await AskModelAsync(text, ct); }
        catch (OperationCanceledException) { throw; }
        catch { return new VoiceIntent { Action = "unknown", Query = text }; }
    }

    private static string Clean(string t) =>
        Regex.Replace(t.ToLowerInvariant().Replace("'", ""), @"[^a-z0-9؀-ۿ ]", " ").Trim().Replace("  ", " ");

    public static VoiceIntent? QuickParse(string raw)
    {
        string t = Regex.Replace(Clean(raw), @"\s+", " ");
        t = Regex.Replace(t, @"^(hey |ok |okay |please |can you |could you )+", "");
        if (t.Length == 0) return null;
        Match m;

        if ((m = Regex.Match(t, @"^(?:set |turn )?(?:the )?(?:volume|sound) (?:to |at )?(\d{1,3})\b")).Success)
            return new VoiceIntent { Action = "volume", Value = Math.Clamp(int.Parse(m.Groups[1].Value), 0, 100), Source = "rule" };
        if (Regex.IsMatch(t, @"^(?:volume up|louder|turn it up|increase (?:the )?volume|raise (?:the )?volume)"))
            return new VoiceIntent { Action = "volume", Query = "up", Source = "rule" };
        if (Regex.IsMatch(t, @"^(?:volume down|quieter|turn it down|lower (?:the )?volume|decrease (?:the )?volume|reduce (?:the )?volume)"))
            return new VoiceIntent { Action = "volume", Query = "down", Source = "rule" };
        if (Regex.IsMatch(t, @"^(?:mute|unmute|toggle mute)\b"))
            return new VoiceIntent { Action = "volume", Query = "mute", Source = "rule" };

        if (Regex.IsMatch(t, @"^(?:pause|resume|play|stop)(?: the)?(?: music| song| video| playback)?$"))
            return new VoiceIntent { Action = "media", Query = "play", Source = "rule" };
        if (Regex.IsMatch(t, @"^(?:next|skip)(?: the)?(?: song| track| video)?$"))
            return new VoiceIntent { Action = "media", Query = "next", Source = "rule" };
        if (Regex.IsMatch(t, @"^(?:previous|go back|last)(?: the)?(?: song| track| video)?$"))
            return new VoiceIntent { Action = "media", Query = "previous", Source = "rule" };

        if ((m = Regex.Match(t, @"timer (?:for )?(\d+) ?(second|sec|minute|min|hour)")).Success)
        {
            int n = int.Parse(m.Groups[1].Value);
            string u = m.Groups[2].Value;
            int secs = u.StartsWith("sec") || u == "second" ? n : u.StartsWith("hour") ? n * 3600 : n * 60;
            return new VoiceIntent { Action = "timer", Value = secs, Source = "rule" };
        }

        if ((m = Regex.Match(t, @"^(?:search|google|look up|find)(?: for| up)? (.+)$")).Success && !m.Groups[1].Value.Contains(" on youtube"))
            return new VoiceIntent { Action = "web_search", Query = m.Groups[1].Value, Source = "rule" };

        if ((m = Regex.Match(t, @"^(?:open|launch|start|run|go to) (?:the )?(.+)$")).Success && !Regex.IsMatch(m.Groups[1].Value, @" and |\bplay\b|\bsearch\b"))
            return new VoiceIntent { Action = "open", Query = m.Groups[1].Value, Source = "rule" };

        if ((m = Regex.Match(t, @"^(?:play|watch) (.+?)(?: on youtube)?$")).Success &&
            !Regex.IsMatch(m.Groups[1].Value, @"\bago\b|\blatest\b|\byesterday\b|\blast\b|\bnewest\b|\bnew\b|\bfrom\b|\bby\b|\btoday\b"))
            return new VoiceIntent { Action = "youtube", Query = m.Groups[1].Value, Source = "rule" };

        return null;
    }

    private const string Schema =
        "{\"type\":\"object\",\"properties\":{\"action\":{\"enum\":[\"youtube\",\"open\",\"web_search\",\"volume\",\"media\",\"timer\",\"food_search\",\"unknown\"]}," +
        "\"query\":{\"type\":\"string\"},\"channel\":{\"type\":\"string\"},\"days_ago\":{\"type\":\"integer\"},\"value\":{\"type\":\"integer\"}},\"required\":[\"action\"]}";

    private const string System =
        "You turn a spoken command into ONE JSON action for a PC assistant. Actions: " +
        "youtube (play a video: query = what to search for, channel = channel name if one is named, days_ago = how many days old the video should be, 0 = the newest); " +
        "open (open an app or website: query = its name); web_search (query); volume (value 0-100, or query up/down/mute); " +
        "media (query = play, next or previous); timer (value = number of minutes); food_search (query = the food or restaurant, for a food delivery app); " +
        "unknown (the command fits none of these).\n" +
        "Examples:\n" +
        "play the MrBeast video from two days ago -> {\"action\":\"youtube\",\"query\":\"MrBeast\",\"channel\":\"MrBeast\",\"days_ago\":2}\n" +
        "order a zinger burger from foodpanda -> {\"action\":\"food_search\",\"query\":\"zinger burger\"}\n" +
        "open calculator -> {\"action\":\"open\",\"query\":\"calculator\"}\n" +
        "what is the weather in Karachi -> {\"action\":\"web_search\",\"query\":\"weather in Karachi\"}";

    private static async Task<VoiceIntent> AskModelAsync(string text, CancellationToken ct)
    {
        Directory.CreateDirectory(Tools.CacheDir);
        string schemaFile = Path.Combine(Tools.CacheDir, "voice-schema.json");
        File.WriteAllText(schemaFile, Schema);

        string prompt = "<|im_start|>system\n" + System + "<|im_end|>\n<|im_start|>user\n" + text + "<|im_end|>\n<|im_start|>assistant\n";
        var sb = new StringBuilder();
        int code = await Proc.RunAsync(LlamaExe,
            new[] { "-m", LlamaModel, "-p", prompt, "-n", "100", "--temp", "0", "-jf", schemaFile, "-no-cnv", "-c", "1024",
                    "-t", Threads.ToString(CultureInfo.InvariantCulture), "--no-display-prompt" },
            l => { lock (sb) sb.AppendLine(l); }, null, ct, null, ProcessPriorityClass.Normal);
        if (code != 0) throw new InvalidOperationException("The language model failed");

        string o;
        lock (sb) o = sb.ToString();
        int a = o.IndexOf('{'), b = o.LastIndexOf('}');
        if (a < 0 || b <= a) throw new InvalidOperationException("No answer");
        using var doc = JsonDocument.Parse(o.Substring(a, b - a + 1));
        var r = doc.RootElement;
        string S(string n) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        int I(string n, int d) => r.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : d;

        var it = new VoiceIntent
        {
            Action = S("action"), Query = S("query"), Channel = S("channel"),
            DaysAgo = Math.Max(0, I("days_ago", 0)), Value = I("value", -1), Source = "ai"
        };
        if (it.Action == "timer" && it.Value >= 0) it.Value *= 60; // the model counts minutes
        if (it.Action.Length == 0) it.Action = "unknown";
        if (it.Action == "unknown") it.Query = text;
        return it;
    }

    // ---------- Doing it ----------

    private static readonly Dictionary<string, string> Sites = new(StringComparer.OrdinalIgnoreCase)
    {
        ["youtube"] = "https://www.youtube.com", ["google"] = "https://www.google.com", ["gmail"] = "https://mail.google.com",
        ["facebook"] = "https://www.facebook.com", ["instagram"] = "https://www.instagram.com", ["twitter"] = "https://x.com",
        ["x"] = "https://x.com", ["whatsapp"] = "https://web.whatsapp.com", ["whatsapp web"] = "https://web.whatsapp.com",
        ["github"] = "https://github.com", ["netflix"] = "https://www.netflix.com", ["spotify"] = "https://open.spotify.com",
        ["foodpanda"] = "https://www.foodpanda.pk", ["food panda"] = "https://www.foodpanda.pk", ["daraz"] = "https://www.daraz.pk",
        ["chatgpt"] = "https://chatgpt.com", ["claude"] = "https://claude.ai", ["maps"] = "https://maps.google.com",
        ["google maps"] = "https://maps.google.com", ["linkedin"] = "https://www.linkedin.com", ["reddit"] = "https://www.reddit.com",
        ["tiktok"] = "https://www.tiktok.com", ["twitch"] = "https://www.twitch.tv", ["amazon"] = "https://www.amazon.com",
        ["discord"] = "https://discord.com/app", ["pinterest"] = "https://www.pinterest.com", ["drive"] = "https://drive.google.com"
    };

    public static string SearchUrl(string q) => "https://www.google.com/search?q=" + Uri.EscapeDataString(q);

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { }
    }

    /// <summary>Opens an app (Start Menu shortcut) or a website. Returns what happened, for the message.</summary>
    public static string OpenSomething(string name)
    {
        string n = name.Trim().Trim('.');
        if (Sites.TryGetValue(n, out string? site)) { OpenUrl(site); return "Opening " + n; }

        string? lnk = FindApp(n);
        if (lnk != null)
        {
            try { Process.Start(new ProcessStartInfo(lnk) { UseShellExecute = true }); return "Opening " + Path.GetFileNameWithoutExtension(lnk); }
            catch { }
        }
        if (Regex.IsMatch(n, @"^[\w-]+(\.[\w-]+)+(/.*)?$")) { OpenUrl("https://" + n); return "Opening " + n; }
        OpenUrl(SearchUrl(n));
        return "Searching " + n;
    }

    private static string? FindApp(string name)
    {
        string key = Regex.Replace(name.ToLowerInvariant(), @"[^a-z0-9]", "");
        if (key.Length == 0) return null;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
        };
        string? best = null;
        int bestScore = int.MaxValue;
        foreach (string root in roots)
        {
            if (!Directory.Exists(root)) continue;
            IEnumerable<string> files;
            try { files = Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }
            foreach (string f in files)
            {
                string fn = Regex.Replace(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(), @"[^a-z0-9]", "");
                if (fn.Contains("uninstall")) continue;
                int score = fn == key ? 0 : fn.StartsWith(key, StringComparison.Ordinal) ? 1 + fn.Length - key.Length
                          : fn.Contains(key, StringComparison.Ordinal) ? 100 + fn.Length - key.Length : int.MaxValue;
                if (score < bestScore) { bestScore = score; best = f; }
            }
        }
        return best;
    }

    /// <summary>A YouTube video for the request: the newest / N days old one of a channel, or the first search result.</summary>
    public static async Task<string?> FindYouTubeAsync(VoiceIntent it, CancellationToken ct)
    {
        await Tools.EnsureYtDlpAsync(null, ct);

        string channel = it.Channel.Trim();
        if (channel.Length > 0)
        {
            string handle = Regex.Replace(channel, @"[^\p{L}\p{N}]", "");
            var lines = await RunYtDlpAsync(new[]
            {
                "--flat-playlist", "--no-warnings", "--playlist-end", "30", "--extractor-args", "youtubetab:approximate_date",
                "--print", "%(id)s|%(upload_date)s", "https://www.youtube.com/@" + handle + "/videos"
            }, ct);

            var vids = new List<(string Id, DateTime? Date)>();
            foreach (string l in lines)
            {
                string[] p = l.Split('|');
                if (p.Length < 2 || p[0].Length != 11) continue;
                DateTime? d = DateTime.TryParseExact(p[1], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt) ? dt : null;
                vids.Add((p[0], d));
            }
            if (vids.Count > 0)
            {
                if (it.DaysAgo <= 0 || vids.All(v => v.Date == null)) return "https://www.youtube.com/watch?v=" + vids[0].Id;
                DateTime target = DateTime.Today.AddDays(-it.DaysAgo);
                var best = vids.Where(v => v.Date != null).OrderBy(v => Math.Abs((v.Date!.Value - target).TotalDays)).First();
                return "https://www.youtube.com/watch?v=" + best.Id;
            }
        }

        string q = (channel + " " + it.Query).Trim();
        if (q.Length == 0) return null;
        var res = await RunYtDlpAsync(new[] { "--flat-playlist", "--no-warnings", "--print", "%(id)s", "ytsearch1:" + q }, ct);
        string? id = res.FirstOrDefault(x => x.Length == 11);
        return id == null ? null : "https://www.youtube.com/watch?v=" + id;
    }

    private static async Task<List<string>> RunYtDlpAsync(string[] args, CancellationToken ct)
    {
        var lines = new List<string>();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(25));
        try
        {
            await Proc.RunAsync(Tools.YtDlp, args, l => { lock (lines) lines.Add(l.Trim()); }, null, cts.Token, null, ProcessPriorityClass.Normal);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        lock (lines) return lines.ToList();
    }

    // ---------- Volume and media keys ----------

    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, 2, UIntPtr.Zero);
    }

    public static string Volume(VoiceIntent it)
    {
        if (it.Value >= 0)
        {
            for (int i = 0; i < 50; i++) Tap(0xAE);                 // down to 0 (each step is 2 %)
            for (int i = 0; i < it.Value / 2; i++) Tap(0xAF);
            return "Volume " + it.Value + "%";
        }
        switch (it.Query)
        {
            case "up": for (int i = 0; i < 5; i++) Tap(0xAF); return "Volume up";
            case "down": for (int i = 0; i < 5; i++) Tap(0xAE); return "Volume down";
            default: Tap(0xAD); return "Mute";
        }
    }

    public static string Media(string what)
    {
        switch (what)
        {
            case "next": Tap(0xB0); return "Next";
            case "previous": Tap(0xB1); return "Previous";
            default: Tap(0xB3); return "Play / pause";
        }
    }

    // What needs a permission ("Allow once / Always"): everything that opens something outside the app
    public static string PermissionKey(VoiceIntent it) => it.Action switch
    {
        "open" => "open",
        "web_search" => "web",
        "youtube" => "youtube",
        "food_search" => "food",
        _ => ""
    };
}
