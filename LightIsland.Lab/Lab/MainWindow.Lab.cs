using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightIsland;

// Lab additions of the island itself. Everything heavy runs in separate tool processes (see ToolHost),
// so the island stays small: this file only holds the Toolbox page, the hotkey, the reminder clock and the auto theme.
public partial class MainWindow
{
    private const int ViewTool = 6;
    private const int LabHotkeyId = 0x4C42;

    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly DispatcherTimer _remTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly HashSet<string> _ringing = new();
    private DateTime _remStamp = DateTime.MinValue;
    private List<ReminderItem> _remCache = new();

    private void InitLab()
    {
        SourceInitialized += (_, _) =>
        {
            try
            {
                var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
                src?.AddHook(LabWndProc);
                // Ctrl + Alt + Space opens the AI bar
                RegisterHotKey(new WindowInteropHelper(this).Handle, LabHotkeyId, 0x0002 | 0x0001 | 0x4000, 0x20);
            }
            catch { }
        };
        Closed += (_, _) =>
        {
            try { UnregisterHotKey(new WindowInteropHelper(this).Handle, LabHotkeyId); } catch { }
        };

        _remTimer.Tick += (_, _) => CheckReminders();
        _remTimer.Start();

        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == Microsoft.Win32.UserPreferenceCategory.General)
                Dispatcher.BeginInvoke(new Action(ApplyAutoThemeNow));
        };
        Dispatcher.BeginInvoke(new Action(ApplyAutoThemeNow), DispatcherPriority.ApplicationIdle);
    }

    private IntPtr LabWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == 0x0312 && wParam.ToInt32() == LabHotkeyId)
        {
            handled = true;
            ToolHost.Launch("ask");
        }
        return IntPtr.Zero;
    }

    // ---------- Auto dark / light ----------

    private static bool WindowsIsLight()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return k?.GetValue("AppsUseLightTheme") is int v && v == 1;
        }
        catch { return false; }
    }

    /// <summary>Follows the Windows dark / light mode when "Follow Windows" is on.</summary>
    public void ApplyAutoThemeNow()
    {
        if (!_s.AutoTheme) return;
        bool light = WindowsIsLight();
        string want = light ? "Light" : "Dark";
        if (_s.Theme == want) return;
        _s.Theme = want;
        _s.IslandColor = light ? "#F2F2F7" : "#000000";
        _s.TextColor = light ? "#1C1C1E" : "#FFFFFF";
        try { _s.Save(); } catch { }
        ApplySettings(false);
    }

    // ---------- Reminders ----------

    private void CheckReminders()
    {
        try
        {
            DateTime w = Reminders.LastWrite();
            if (w != _remStamp) { _remStamp = w; _remCache = Reminders.Load(); }
            DateTime now = DateTime.UtcNow;
            _ringing.RemoveWhere(id => !_remCache.Any(r => r.Id == id && !r.Fired && r.DueUtc <= now));
            foreach (var r in _remCache)
            {
                if (r.Fired || r.DueUtc > now || !_ringing.Add(r.Id)) continue;
                ShowHud("", r.Text.Length > 26 ? r.Text.Substring(0, 26) + "…" : r.Text, null, "Reminder", 4500);
                ToolHost.Launch("remind-alert", r.Id);
            }
        }
        catch { }
    }

    // ---------- Toolbox page ----------

    private sealed record ToolTile(string Icon, string Name, Action Run);

    private void RenderToolbox()
    {
        if (ToolHost2 == null) return;
        ToolHost2.Children.Clear();
        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var head = new Grid { Height = 20, Margin = new Thickness(0, 0, 0, 6) };
        head.Children.Add(new TextBlock { Text = "Toolbox", Style = (Style)FindResource("PageTitle") });
        var hint = Txt("Ctrl + Alt + Space  =  Ask AI", 11, false, true);
        hint.HorizontalAlignment = HorizontalAlignment.Right;
        hint.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(hint);
        root.Children.Add(head);

        var tiles = new List<ToolTile>
        {
            new("✨", "Ask AI", () => ToolHost.Launch("ask")),
            new("📝", "OCR", () => ToolHost.Launch("ocr")),
            new("🎨", "Colors", () => ToolHost.Launch("color")),
            new("⏱", "Timer", () => ToolHost.Launch("timer")),
            new("🌐", "Translate", () => ToolHost.Launch("translate")),
            new("⏰", "Reminders", () => ToolHost.Launch("remind")),
            new("🔳", "QR code", () => ToolHost.Launch("qr")),
            new("🔁", "Convert", () => ToolHost.Launch("convert")),
            new("✉", "Temp mail", () => ToolHost.Launch("mail")),
            new("⚡", "Performance", () => ToolHost.Launch("ram")),
            new("📶", "Wi-Fi / BT", () => ToolHost.Launch("net")),
            new("📋", "Snippets", OpenSnippets),
            new("🌤", "Weather", () => ToolHost.Launch("crypto")),
            new("📌", "Ticker bar", () => ToolHost.Launch("ticker")),
        };

        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var t in tiles)
        {
            var tile = t;
            var b = new Border
            {
                Width = 76,
                Height = 58,
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(0, 0, 6, 6),
                Cursor = Cursors.Hand,
                Background = TileBrush
            };
            b.SetResourceReference(Border.BackgroundProperty, "TileBg");
            var sp = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            sp.Children.Add(new TextBlock { Text = tile.Icon, FontFamily = new FontFamily("Segoe UI Emoji"), FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center });
            var lb = Txt(tile.Name, 10.5, true);
            lb.HorizontalAlignment = HorizontalAlignment.Center;
            lb.Margin = new Thickness(0, 3, 0, 0);
            sp.Children.Add(lb);
            b.Child = sp;
            b.MouseEnter += (_, _) => b.Opacity = 0.8;
            b.MouseLeave += (_, _) => b.Opacity = 1;
            b.MouseLeftButtonUp += (_, e) => { e.Handled = true; try { tile.Run(); } catch { } };
            wrap.Children.Add(b);
        }
        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = wrap };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        ToolHost2.Children.Add(root);
    }

    /// <summary>The clipboard history only lives in RAM, so it is handed to the Snippets tool through a temp file that the tool deletes right away.</summary>
    private void OpenSnippets()
    {
        try
        {
            if (_s.ClipHistory && _clips.Count > 0)
                File.WriteAllText(ToolHost.DataFile("clips-handoff.json"), JsonSerializer.Serialize(_clips.Select(c => c.Text).Take(25).ToList()));
        }
        catch { }
        ToolHost.Launch("snippets", "recent");
    }
}
