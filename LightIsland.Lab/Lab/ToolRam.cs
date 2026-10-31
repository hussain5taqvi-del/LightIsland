using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>
/// Live memory / CPU numbers, the biggest programs, and a Boost button that asks Windows to take back unused memory
/// from every program. It reports the real before / after numbers, nothing is faked.
/// </summary>
internal sealed class RamTool : Window
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMSTATUS
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }
    [DllImport("kernel32.dll")] private static extern bool GlobalMemoryStatusEx(ref MEMSTATUS m);
    [DllImport("kernel32.dll")] private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
    [DllImport("psapi.dll")] private static extern bool EmptyWorkingSet(IntPtr h);

    private static readonly HashSet<string> Protected = new(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "svchost", "dwm", "explorer",
        "fontdrvhost", "memcompression", "memory compression", "sihost", "ctfmon", "taskhostw", "runtimebroker", "searchhost", "startmenuexperiencehost",
        "shellexperiencehost", "textinputhost", "securityhealthservice", "msmpeng", "audiodg", "spoolsv", "lsaiso"
    };

    private readonly Grid _ramBar = new(), _cpuBar = new();
    private readonly TextBlock _ramTx = new() { FontSize = 13, Foreground = Ui.Text }, _cpuTx = new() { FontSize = 13, Foreground = Ui.Text };
    private readonly StackPanel _procs = new();
    private readonly TextBlock _result = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Good, Margin = new Thickness(0, 10, 0, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(2) };
    private long _pi, _pk, _pu;
    private bool _busy;
    private Border _boost = null!;
    private int _tick;

    public RamTool()
    {
        Ui.Setup(this, "Performance", 560, 720, true);
        var root = new StackPanel { Margin = new Thickness(18) };

        root.Children.Add(Ui.Label("MEMORY", 11, true, true));
        _ramTx.Margin = new Thickness(0, 4, 0, 6);
        root.Children.Add(_ramTx);
        BuildBar(_ramBar);
        root.Children.Add(_ramBar);

        var c = Ui.Label("PROCESSOR", 11, true, true);
        c.Margin = new Thickness(0, 14, 0, 0);
        root.Children.Add(c);
        _cpuTx.Margin = new Thickness(0, 4, 0, 6);
        root.Children.Add(_cpuTx);
        BuildBar(_cpuBar);
        root.Children.Add(_cpuBar);

        var row = Ui.Row(Ui.Btn("Boost RAM", () => _ = BoostAsync(), true, 120), Ui.Btn("Clean junk files", () => _ = CleanAsync()));
        row.Margin = new Thickness(0, 16, 0, 0);
        _boost = (Border)row.Children[0];
        root.Children.Add(row);
        root.Children.Add(_result);

        var h = Ui.Label("BIGGEST PROGRAMS", 11, true, true);
        h.Margin = new Thickness(0, 16, 0, 6);
        root.Children.Add(h);
        root.Children.Add(_procs);
        Content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = root };

        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => { Update(); _timer.Start(); };
        Closed += (_, _) => _timer.Stop();
        Deactivated += (_, _) => _timer.Interval = TimeSpan.FromSeconds(5);
        Activated += (_, _) => _timer.Interval = TimeSpan.FromSeconds(2);
    }

    private static void BuildBar(Grid g)
    {
        g.Height = 12;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(100, GridUnitType.Star) });
        var track = new Border { Background = Ui.Panel2, CornerRadius = new CornerRadius(6) };
        Grid.SetColumnSpan(track, 2);
        g.Children.Add(track);
        var fill = new Border { Background = Ui.Accent, CornerRadius = new CornerRadius(6) };
        g.Children.Add(fill);
    }

    private static void SetBar(Grid g, double pct)
    {
        pct = Math.Clamp(pct, 0, 100);
        g.ColumnDefinitions[0].Width = new GridLength(pct, GridUnitType.Star);
        g.ColumnDefinitions[1].Width = new GridLength(100 - pct + 0.0001, GridUnitType.Star);
        ((Border)g.Children[1]).Background = pct > 90 ? Ui.Bad : Ui.Accent;
    }

    private static (double TotalGb, double UsedGb, double AvailMb, double Pct) Mem()
    {
        var m = new MEMSTATUS { dwLength = (uint)Marshal.SizeOf<MEMSTATUS>() };
        GlobalMemoryStatusEx(ref m);
        double total = m.ullTotalPhys, avail = m.ullAvailPhys;
        return (total / 1073741824.0, (total - avail) / 1073741824.0, avail / 1048576.0, total > 0 ? (total - avail) / total * 100 : 0);
    }

    private void Update()
    {
        var m = Mem();
        _ramTx.Text = m.UsedGb.ToString("0.0") + " GB used of " + m.TotalGb.ToString("0.0") + " GB   (" + m.Pct.ToString("0") + "%)";
        SetBar(_ramBar, m.Pct);

        if (GetSystemTimes(out long i, out long k, out long u))
        {
            if (_pk != 0)
            {
                long di = i - _pi, dk = k - _pk, du = u - _pu, tot = dk + du;
                double cpu = tot > 0 ? (tot - di) * 100.0 / tot : 0;
                _cpuTx.Text = cpu.ToString("0") + "% busy";
                SetBar(_cpuBar, cpu);
            }
            _pi = i; _pk = k; _pu = u;
        }

        if (_tick++ % 2 == 0 && !_busy) FillProcs();
    }

    private void FillProcs()
    {
        var groups = new Dictionary<string, (long Bytes, int Count)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                string n = p.ProcessName;
                long b = p.WorkingSet64;
                groups[n] = groups.TryGetValue(n, out var g) ? (g.Bytes + b, g.Count + 1) : (b, 1);
            }
            catch { }
            finally { p.Dispose(); }
        }
        _procs.Children.Clear();
        foreach (var kv in groups.OrderByDescending(x => x.Value.Bytes).Take(12))
        {
            string name = kv.Key;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 6) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.Children.Add(Ui.Label(name + (kv.Value.Count > 1 ? "  x" + kv.Value.Count : ""), 13, false, true));
            var mb = Ui.Label((kv.Value.Bytes / 1048576.0).ToString("#,0") + " MB", 13, true);
            mb.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(mb, 1);
            g.Children.Add(mb);
            bool canEnd = !Protected.Contains(name) && !name.StartsWith("LightIsland", StringComparison.OrdinalIgnoreCase);
            if (canEnd)
            {
                var end = Ui.Btn("End", () => EndTask(name));
                end.Margin = new Thickness(10, 0, 0, 0);
                end.Padding = new Thickness(10, 3, 10, 3);
                Grid.SetColumn(end, 2);
                g.Children.Add(end);
            }
            _procs.Children.Add(g);
        }
    }

    private void EndTask(string name)
    {
        var r = MessageBox.Show(this, "End every \"" + name + "\" process?\nUnsaved work in it will be lost.", "End program", MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (r != MessageBoxResult.Yes) return;
        int n = 0;
        foreach (var p in Process.GetProcessesByName(name))
        {
            try { p.Kill(true); n++; } catch { }
            finally { p.Dispose(); }
        }
        _result.Foreground = n > 0 ? Ui.Good : Ui.Bad;
        _result.Text = n > 0 ? "Ended " + name : "Could not end " + name + " (it may need administrator rights).";
        FillProcs();
    }

    private async Task BoostAsync()
    {
        if (_busy) return;
        _busy = true;
        _boost.IsEnabled = false;
        _result.Foreground = Ui.Dim;
        _result.Text = "Working...";
        double before = Mem().AvailMb;
        int touched = await Task.Run(() =>
        {
            int n = 0;
            int self = Environment.ProcessId;
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id == self || p.Id <= 4) continue;
                    IntPtr h = OpenProcess(0x0100 | 0x0400, false, p.Id); // SET_QUOTA | QUERY_INFORMATION
                    if (h == IntPtr.Zero) continue;
                    if (EmptyWorkingSet(h)) n++;
                    CloseHandle(h);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return n;
        });
        await Task.Delay(900);
        double after = Mem().AvailMb;
        double freed = after - before;
        _result.Foreground = Ui.Good;
        _result.Text = freed > 1
            ? "Freed " + freed.ToString("#,0") + " MB of RAM (" + touched + " programs trimmed). Programs take what they need back when you use them."
            : "Memory was already tight and clean: nothing more to free right now (" + touched + " programs checked).";
        _boost.IsEnabled = true;
        _busy = false;
        Update();
    }

    private async Task CleanAsync()
    {
        if (_busy) return;
        _busy = true;
        _result.Foreground = Ui.Dim;
        _result.Text = "Cleaning temporary files...";
        try
        {
            var r = await Cleaner.RunAsync();
            _result.Foreground = Ui.Good;
            _result.Text = "Removed " + r.Files.ToString("#,0") + " files, " + (r.Bytes / 1048576.0).ToString("#,0.#") + " MB freed on disk.";
        }
        catch { _result.Foreground = Ui.Bad; _result.Text = "Cleaning failed."; }
        _busy = false;
    }
}
