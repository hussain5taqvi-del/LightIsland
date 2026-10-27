using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media;

namespace LightIsland;

// Hold the voice key (Caps Lock by default), speak, let go: the command is done.
// The speech and language models run in separate helper programs that exit afterwards (see Voice.cs),
// so the island itself stays inside its small memory limit.
public partial class MainWindow
{
    private MicRecorder? _mic;
    private bool _voiceBusy;
    private CancellationTokenSource? _voiceCts;

    private const string GlyphMic = "", GlyphOk = "", GlyphWarn = "";

    private Color AccentValue()
    {
        try { return (Color)ColorConverter.ConvertFromString(_s.AccentColor); }
        catch { return Color.FromRgb(0x38, 0xBD, 0xF8); }
    }

    private void InitVoice()
    {
        if (_sel == null) return;
        _sel.VoiceDown += OnVoiceDown;
        _sel.VoiceUp += OnVoiceUp;
        _sel.VoiceCancel += OnVoiceCancel;
    }

    private void OnVoiceDown()
    {
        if (_voiceBusy || _mic != null) return;
        if (!VoiceBrain.Available)
        {
            ShowHud(GlyphWarn, "Voice engine is not in this build", null, null, 2600);
            return;
        }
        var m = new MicRecorder();
        if (!m.Start())
        {
            ShowHud(GlyphWarn, "No microphone available", null, null, 2600);
            return;
        }
        _mic = m;
        ShowHud(GlyphMic, "Listening...", null, null, 31000);
    }

    private void OnVoiceCancel()
    {
        var m = _mic;
        _mic = null;
        if (m == null) return;
        try { m.Stop(); } catch { }
        HideHud();
    }

    private async void OnVoiceUp()
    {
        var m = _mic;
        if (m == null) return;
        _mic = null;

        byte[] pcm;
        try { pcm = m.Stop(); }
        catch { HideHud(); return; }

        if (MicRecorder.Seconds(pcm) < 0.4) { HideHud(); return; }   // a tap, not a command
        if (MicRecorder.Peak(pcm) < 250)
        {
            ShowHud(GlyphWarn, "Mic is silent: check Windows microphone privacy", null, null, 3200);
            return;
        }
        await RunVoiceAsync(pcm);
    }

    private async Task RunVoiceAsync(byte[] pcm)
    {
        _voiceBusy = true;
        _voiceCts?.Dispose();
        _voiceCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        CancellationToken ct = _voiceCts.Token;
        try
        {
            ShowHud(GlyphMic, "Understanding...", null, null, 30000);
            string text = await VoiceBrain.TranscribeAsync(pcm, ct);
            if (text.Length < 2)
            {
                ShowHud(GlyphWarn, "Could not hear that, try again", null, null, 2400);
                return;
            }

            ShowHud(GlyphMic, Short(text, 30), null, "...", 30000);
            VoiceIntent it = await VoiceBrain.UnderstandAsync(text, ct);
            await DoVoiceAsync(text, it, ct);
        }
        catch (OperationCanceledException) { HideHud(); }
        catch (Exception ex) { ShowHud(GlyphWarn, Short(ex.Message, 40), null, null, 3000); }
        finally { _voiceBusy = false; }
    }

    private static string Describe(VoiceIntent it)
    {
        switch (it.Action)
        {
            case "open": return "Open " + it.Query;
            case "web_search": return "Search the web for “" + it.Query + "”";
            case "food_search": return "Look up “" + it.Query + "” on Foodpanda (nothing is ordered)";
            case "youtube":
                string who = it.Channel.Length > 0 ? it.Channel + (it.Query.Length > 0 && !it.Query.Equals(it.Channel, StringComparison.OrdinalIgnoreCase) ? " " + it.Query : "") : it.Query;
                return "Play on YouTube: " + who + (it.DaysAgo > 0 ? " (about " + it.DaysAgo + " days old)" : "");
        }
        return it.Action;
    }

    private async Task DoVoiceAsync(string heard, VoiceIntent it, CancellationToken ct)
    {
        if (it.Action == "unknown")
        {
            ShowHud(GlyphWarn, "Not understood: " + Short(heard, 26), null, null, 3200);
            return;
        }

        // Anything that opens something outside the app asks first (once, or always for this kind of action)
        string key = VoiceBrain.PermissionKey(it);
        if (key.Length > 0 && !_s.VoiceAllow.Contains(key))
        {
            HideHud();
            int r = await VoiceConfirm.AskAsync(heard, Describe(it), _s.Theme == "Light", AccentValue());
            if (r == 0) { ShowHud(GlyphWarn, "Cancelled", null, null, 1400); return; }
            if (r == 2) { _s.VoiceAllow.Add(key); _s.Save(); }
        }

        string msg;
        switch (it.Action)
        {
            case "open":
                msg = await Task.Run(() => VoiceBrain.OpenSomething(it.Query), ct);
                break;
            case "web_search":
                VoiceBrain.OpenUrl(VoiceBrain.SearchUrl(it.Query));
                msg = "Searching: " + Short(it.Query, 26);
                break;
            case "food_search":
                VoiceBrain.OpenUrl(VoiceBrain.SearchUrl("foodpanda.pk " + it.Query));
                msg = "Foodpanda: " + Short(it.Query, 24);
                break;
            case "youtube":
            {
                ShowHud(GlyphMic, "Finding the video...", null, null, 30000);
                string? url = await VoiceBrain.FindYouTubeAsync(it, ct);
                if (url == null)
                {
                    VoiceBrain.OpenUrl("https://www.youtube.com/results?search_query=" + Uri.EscapeDataString((it.Channel + " " + it.Query).Trim()));
                    msg = "YouTube search";
                }
                else
                {
                    VoiceBrain.OpenUrl(url);
                    msg = "Playing on YouTube";
                }
                break;
            }
            case "volume":
                msg = await Task.Run(() => VoiceBrain.Volume(it), ct);
                break;
            case "media":
                msg = VoiceBrain.Media(it.Query);
                break;
            case "timer":
            {
                int secs = it.Value > 0 ? it.Value : 60;
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(secs));
                    await Dispatcher.InvokeAsync(() =>
                    {
                        ShowHud("", "Timer done", null, null, 6000);
                        System.Media.SystemSounds.Exclamation.Play();
                    });
                });
                msg = "Timer: " + (secs >= 3600 ? (secs / 3600) + " h" : secs >= 60 ? (secs / 60) + " min" : secs + " s");
                break;
            }
            default:
                ShowHud(GlyphWarn, "Not understood: " + Short(heard, 26), null, null, 3200);
                return;
        }

        ShowHud(GlyphOk, msg, null, null, 2400);
    }
}
