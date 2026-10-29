using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LightIsland;

internal sealed record LyricLine(double Time, string Text);

/// <summary>
/// Time-synced lyrics from LRCLIB (free, no account, no key). Only real lyrics are shown: when nothing is found
/// (or the PC is offline) the wallpaper simply shows no lyrics.
/// </summary>
internal static class Lyrics
{
    private static readonly HttpClient Http = Make();
    private static string _key = "";
    private static List<LyricLine>? _cached;

    private static HttpClient Make()
    {
        var h = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("LightIsland/1.6 (https://github.com/hussain5taqvi-del/LightIsland)");
        return h;
    }

    /// <summary>"Artist - Song (Official Video)" style titles are cleaned up so they can be found.</summary>
    public static (string Title, string Artist) Clean(string title, string artist)
    {
        string t = title ?? "", a = artist ?? "";
        int dash = t.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            string left = t.Substring(0, dash).Trim(), right = t.Substring(dash + 3).Trim();
            if (a.Length == 0 || a.EndsWith("- Topic", StringComparison.OrdinalIgnoreCase) || !t.Contains(a, StringComparison.OrdinalIgnoreCase) || left.Contains(a, StringComparison.OrdinalIgnoreCase))
            { a = left; t = right; }
        }
        t = Regex.Replace(t, @"[\(\[][^\)\]]*[\)\]]", " ");
        t = Regex.Replace(t, @"(?i)\b(official|lyrics?|video|audio|hd|4k|remastered)\b", " ");
        t = Regex.Replace(t, @"(?i)\b(feat|ft)\.?\s.*$", " ");
        a = Regex.Replace(a, @"(?i)\s*-\s*topic$", "");
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return (t, a.Trim());
    }

    public static async Task<List<LyricLine>?> GetAsync(string title, string artist, double duration, CancellationToken ct)
    {
        var (t, a) = Clean(title, artist);
        if (t.Length == 0) return null;
        string key = t + "|" + a;
        if (key == _key) return _cached;

        List<LyricLine>? lines = null;
        try
        {
            lines = await Search("track_name=" + Uri.EscapeDataString(t) + (a.Length > 0 ? "&artist_name=" + Uri.EscapeDataString(a) : ""), duration, ct);
            if (lines == null && a.Length > 0)
                lines = await Search("q=" + Uri.EscapeDataString(t + " " + a), duration, ct);
        }
        catch
        {
            return null; // offline or blocked: no lyrics, and not remembered (it may work next time)
        }

        _key = key;
        _cached = lines;
        return lines;
    }

    private static async Task<List<LyricLine>?> Search(string query, double duration, CancellationToken ct)
    {
        using var resp = await Http.GetAsync("https://lrclib.net/api/search?" + query, ct);
        if (!resp.IsSuccessStatusCode) return null;
        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

        string? best = null;
        double bestDiff = double.MaxValue;
        foreach (JsonElement e in doc.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("syncedLyrics", out JsonElement s) || s.ValueKind != JsonValueKind.String) continue;
            string? text = s.GetString();
            if (string.IsNullOrWhiteSpace(text)) continue;
            double d = e.TryGetProperty("duration", out JsonElement du) && du.ValueKind == JsonValueKind.Number ? du.GetDouble() : 0;
            double diff = duration > 0 && d > 0 ? Math.Abs(d - duration) : 5;
            if (duration > 0 && d > 0 && diff > 6) continue; // a different version of the song: lines would not match the audio
            if (diff < bestDiff) { bestDiff = diff; best = text; }
        }
        return best == null ? null : Parse(best);
    }

    private static readonly Regex Tag = new(@"\[(\d+):(\d+(?:[.,]\d+)?)\]", RegexOptions.Compiled);

    public static List<LyricLine>? Parse(string lrc)
    {
        var list = new List<LyricLine>();
        foreach (string raw in lrc.Split('\n'))
        {
            string line = raw.Trim();
            var ms = Tag.Matches(line);
            if (ms.Count == 0) continue;
            string text = Tag.Replace(line, "").Trim();
            foreach (Match m in ms)
            {
                double min = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                double sec = double.Parse(m.Groups[2].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
                list.Add(new LyricLine(min * 60 + sec, text));
            }
        }
        list.Sort((x, y) => x.Time.CompareTo(y.Time));
        return list.Count > 3 ? list : null;
    }
}
