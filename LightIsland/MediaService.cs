using System;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace LightIsland;

/// <summary>
/// Reads the current song, play state, album art and progress from Windows' built-in media API.
/// No polling, only events, so it uses no CPU when idle.
/// Works with Spotify, YouTube (browser), VLC and most other players.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    // Cached so the album art of one song is not read again and again
    private string _artKey = "";
    private byte[]? _artBytes;

    /// <summary>title, artist, isPlaying, albumArt (same array reference for the same song)</summary>
    public event Action<string, string, bool, byte[]?>? Changed;

    /// <summary>Position or length changed (after a seek, for example)</summary>
    public event Action? TimelineChanged;

    /// <summary>Friendly name of the app that plays the current song (Spotify, Apple Music, ...).</summary>
    public string CurrentApp
    {
        get
        {
            try { return Friendly(_session?.SourceAppUserModelId); }
            catch { return ""; }
        }
    }

    private static string Friendly(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        string l = id.ToLowerInvariant();
        if (l.Contains("spotify")) return "Spotify";
        if (l.Contains("applemusic") || l.Contains("itunes") || l.Contains("apple")) return "Apple Music";
        if (l.Contains("msedge")) return "Edge";
        if (l.Contains("chrome")) return "Chrome";
        if (l.Contains("firefox")) return "Firefox";
        if (l.Contains("brave")) return "Brave";
        if (l.Contains("vlc")) return "VLC";
        if (l.Contains("zune") || l.Contains("media.player") || l.Contains("mediaplayer")) return "Media Player";
        if (l.Contains("youtube")) return "YouTube Music";
        string n = id;
        int bang = n.IndexOf('!');
        if (bang > 0) n = n.Substring(0, bang);
        int dot = n.LastIndexOf('.');
        if (dot > 0 && n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, dot);
        return n;
    }

    public async Task InitAsync()
    {
        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        _manager.CurrentSessionChanged += (_, _) => Attach();
        Attach();
    }

    private void Attach()
    {
        if (_session != null)
        {
            _session.MediaPropertiesChanged -= OnMediaChanged;
            _session.PlaybackInfoChanged -= OnPlaybackChanged;
            _session.TimelinePropertiesChanged -= OnTimelineChanged;
        }

        _session = _manager?.GetCurrentSession();

        if (_session != null)
        {
            _session.MediaPropertiesChanged += OnMediaChanged;
            _session.PlaybackInfoChanged += OnPlaybackChanged;
            _session.TimelinePropertiesChanged += OnTimelineChanged;
        }

        _ = RefreshAsync();
    }

    private void OnMediaChanged(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs a) => _ = RefreshAsync();
    private void OnPlaybackChanged(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs a) => _ = RefreshAsync();
    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs a) => TimelineChanged?.Invoke();

    private async Task RefreshAsync()
    {
        var s = _session;
        if (s == null)
        {
            _artKey = "";
            _artBytes = null;
            Changed?.Invoke("", "", false, null);
            return;
        }

        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var info = s.GetPlaybackInfo();
            bool playing = info.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            string title = props.Title ?? "";
            string artist = props.Artist ?? "";

            string key = title + "|" + artist;
            if (key != _artKey)
            {
                _artKey = key;
                _artBytes = await ReadThumbnailAsync(props.Thumbnail);
            }

            Changed?.Invoke(title, artist, playing, _artBytes);
        }
        catch
        {
            // The session closed in the middle of reading: ignore
        }
    }

    private static async Task<byte[]?> ReadThumbnailAsync(IRandomAccessStreamReference? thumb)
    {
        if (thumb == null) return null;
        try
        {
            using IRandomAccessStreamWithContentType stream = await thumb.OpenReadAsync();
            uint size = (uint)stream.Size;
            if (size == 0 || size > 8 * 1024 * 1024) return null;

            var buffer = new byte[size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync(size);
            reader.ReadBytes(buffer);
            return buffer;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Progress / seek ----------

    /// <summary>Current position and total length in seconds. Duration 0 means seeking is not supported.</summary>
    public (double Position, double Duration) GetProgress(bool playing)
    {
        var s = _session;
        if (s == null) return (0, 0);

        try
        {
            var t = s.GetTimelineProperties();
            double dur = (t.EndTime - t.StartTime).TotalSeconds;
            if (dur <= 0) return (0, 0);

            double pos = (t.Position - t.StartTime).TotalSeconds;

            // Windows only updates the position now and then, so add the time since the last update
            if (playing && t.LastUpdatedTime.Year > 2000)
            {
                double delta = (DateTimeOffset.Now - t.LastUpdatedTime).TotalSeconds;
                if (delta > 0 && delta < dur) pos += delta;
            }

            return (Math.Clamp(pos, 0, dur), dur);
        }
        catch
        {
            return (0, 0);
        }
    }

    public async Task SeekAsync(double seconds)
    {
        var s = _session;
        if (s == null) return;

        try
        {
            var t = s.GetTimelineProperties();
            long ticks = t.StartTime.Ticks + (long)(seconds * TimeSpan.TicksPerSecond);
            await s.TryChangePlaybackPositionAsync(ticks);
        }
        catch
        {
            // This app does not support seeking
        }
    }

    // ---------- Buttons ----------

    public async Task TogglePlayPauseAsync()
    {
        if (_session != null) await _session.TryTogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_session != null) await _session.TrySkipNextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_session != null) await _session.TrySkipPreviousAsync();
    }
}
