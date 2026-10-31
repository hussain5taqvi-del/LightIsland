using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LightIsland;

internal sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Text { get; set; } = "";
    public DateTime DueUtc { get; set; }
    public string Repeat { get; set; } = "none";   // none, daily, weekly
    public bool Fired { get; set; }
}

/// <summary>Reminders live in a small JSON file; the island checks it, the Reminders tool edits it.</summary>
internal static class Reminders
{
    private static string F => ToolHost.DataFile("reminders.json");

    public static List<ReminderItem> Load()
    {
        try
        {
            if (File.Exists(F))
                return JsonSerializer.Deserialize<List<ReminderItem>>(File.ReadAllText(F)) ?? new List<ReminderItem>();
        }
        catch { }
        return new List<ReminderItem>();
    }

    public static DateTime LastWrite()
    {
        try { return File.Exists(F) ? File.GetLastWriteTimeUtc(F) : DateTime.MinValue; } catch { return DateTime.MinValue; }
    }

    public static void Save(List<ReminderItem> list)
    {
        try
        {
            string tmp = F + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, F, true);
        }
        catch { }
    }

    /// <summary>Marks a reminder as handled. Repeating ones move to their next time.</summary>
    public static void Complete(string id)
    {
        var l = Load();
        var r = l.FirstOrDefault(x => x.Id == id);
        if (r == null) return;
        if (r.Repeat == "daily" || r.Repeat == "weekly")
        {
            TimeSpan step = r.Repeat == "daily" ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
            while (r.DueUtc <= DateTime.UtcNow) r.DueUtc += step;
            r.Fired = false;
        }
        else r.Fired = true;
        Save(l);
    }

    public static void Snooze(string id, int minutes)
    {
        var l = Load();
        var r = l.FirstOrDefault(x => x.Id == id);
        if (r == null) return;
        r.DueUtc = DateTime.UtcNow.AddMinutes(minutes);
        r.Fired = false;
        Save(l);
    }

    // ---------- natural language: "call mom in 20 min", "tomorrow 9am pay bill", "fri 18:30 gym" ----------

    private static readonly string[] Days = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };

    /// <summary>Returns the local due time and the text without the time words. Null when no time was found.</summary>
    public static (DateTime Due, string Text)? Parse(string input, DateTime now)
    {
        string s = " " + input.Trim() + " ";
        DateTime? date = null;
        TimeSpan? time = null;
        DateTime? exact = null;

        // in 10 min / in 2 hours / in 1 day / in 30s
        var m = Regex.Match(s, @"\b(?:in\s+)?(\d+(?:\.\d+)?)\s*(seconds?|secs?|s|minutes?|mins?|m|hours?|hrs?|h|days?|d|weeks?|w)\b", RegexOptions.IgnoreCase);
        if (m.Success && (Regex.IsMatch(m.Value, @"^\s*in\s", RegexOptions.IgnoreCase) || !Regex.IsMatch(s, @"\b(at|on|tomorrow|today|tonight)\b", RegexOptions.IgnoreCase)))
        {
            double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string u = m.Groups[2].Value.ToLowerInvariant();
            TimeSpan span = u.StartsWith("s") ? TimeSpan.FromSeconds(n)
                          : u.StartsWith("m") && !u.StartsWith("mo") ? TimeSpan.FromMinutes(n)
                          : u.StartsWith("h") ? TimeSpan.FromHours(n)
                          : u.StartsWith("d") ? TimeSpan.FromDays(n)
                          : TimeSpan.FromDays(7 * n);
            if (m.Value.Trim().StartsWith("in", StringComparison.OrdinalIgnoreCase) || u.Length > 1)
            {
                exact = now + span;
                s = s.Remove(m.Index, m.Length);
            }
        }

        // tomorrow / today / tonight / weekday names
        var dm = Regex.Match(s, @"\b(tomorrow|tmrw|today|tonight|" + string.Join("|", Days) + @"|" + string.Join("|", Days.Select(d => d.Substring(0, 3))) + @")\b", RegexOptions.IgnoreCase);
        if (dm.Success && exact == null)
        {
            string w = dm.Value.ToLowerInvariant();
            if (w == "tomorrow" || w == "tmrw") date = now.Date.AddDays(1);
            else if (w == "today" || w == "tonight") { date = now.Date; if (w == "tonight") time = new TimeSpan(21, 0, 0); }
            else
            {
                int idx = Array.FindIndex(Days, d => d == w || d.Substring(0, 3) == w);
                if (idx >= 0)
                {
                    int add = ((idx - (int)now.DayOfWeek) + 7) % 7;
                    date = now.Date.AddDays(add);
                    if (add == 0) date = now.Date.AddDays(0);
                }
            }
            s = s.Remove(dm.Index, dm.Length);
        }

        // 18:30 / 6pm / 6:15 pm / at 7 / noon / midnight
        if (exact == null)
        {
            var tm = Regex.Match(s, @"\b(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm|a\.m\.|p\.m\.)\b", RegexOptions.IgnoreCase);
            if (!tm.Success) tm = Regex.Match(s, @"\b(?:at\s+)?(\d{1,2}):(\d{2})()\b", RegexOptions.IgnoreCase);
            if (!tm.Success) tm = Regex.Match(s, @"\bat\s+(\d{1,2})()()\b", RegexOptions.IgnoreCase);
            if (tm.Success)
            {
                int h = int.Parse(tm.Groups[1].Value, CultureInfo.InvariantCulture);
                int mi = tm.Groups[2].Value.Length > 0 ? int.Parse(tm.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
                string ap = tm.Groups[3].Value.ToLowerInvariant();
                if (ap.StartsWith("p") && h < 12) h += 12;
                if (ap.StartsWith("a") && h == 12) h = 0;
                if (h < 24 && mi < 60) { time = new TimeSpan(h, mi, 0); s = s.Remove(tm.Index, tm.Length); }
            }
            else
            {
                var nm = Regex.Match(s, @"\b(noon|midnight)\b", RegexOptions.IgnoreCase);
                if (nm.Success) { time = nm.Value.ToLowerInvariant() == "noon" ? new TimeSpan(12, 0, 0) : TimeSpan.Zero; s = s.Remove(nm.Index, nm.Length); if (date == null && time == TimeSpan.Zero) date = now.Date.AddDays(1); }
            }
        }

        DateTime due;
        if (exact != null) due = exact.Value;
        else if (date != null || time != null)
        {
            DateTime d = date ?? now.Date;
            TimeSpan t = time ?? new TimeSpan(9, 0, 0);
            due = d + t;
            if (due <= now)
            {
                if (date == null) due = due.AddDays(1);              // "at 7" said at 8 pm means tomorrow 7
                else if (dm.Success && Days.Any(x => x.StartsWith(dm.Value.ToLowerInvariant().Substring(0, 3)))) due = due.AddDays(7);
            }
        }
        else return null;

        string text = Regex.Replace(s, @"\b(remind me to|remind me|reminder|to)\b", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', ',', '.', '-');
        if (text.Length == 0) text = Regex.Replace(input.Trim(), @"\s+", " ");
        return (due, text);
    }
}
