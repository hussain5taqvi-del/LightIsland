using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>Add and manage reminders. The island itself rings them (also when this window is closed).</summary>
internal sealed class RemindTool : Window
{
    private readonly Ui.Input _in = new("e.g.  call mom in 20 min  /  tomorrow 9am pay the bill  /  fri 18:30 gym", false, 0, 14);
    private readonly TextBlock _preview = new() { FontSize = 12.5, Margin = new Thickness(2, 6, 0, 6), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _list = new();
    private readonly Ui.Status _st = new();
    private Ui.Chips _repeat = null!;
    private readonly DispatcherTimer _refresh = new() { Interval = TimeSpan.FromSeconds(20) };

    public RemindTool()
    {
        Ui.Setup(this, "Reminders", 560, 680, true);
        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel();
        top.Children.Add(_in);
        top.Children.Add(_preview);
        var quick = Ui.Row(Ui.Btn("in 10 min", () => Quick("in 10 min")), Ui.Btn("in 1 hour", () => Quick("in 1 hour")), Ui.Btn("tonight 9pm", () => Quick("tonight 9pm")), Ui.Btn("tomorrow 9am", () => Quick("tomorrow 9am")));
        top.Children.Add(quick);
        _repeat = new Ui.Chips(new[] { "Once", "Every day", "Every week" }, 0) { Margin = new Thickness(0, 10, 0, 0) };
        top.Children.Add(_repeat);
        top.Children.Add(Ui.Row(Ui.Btn("Add reminder", Add, true)));
        _st.Margin = new Thickness(0, 6, 0, 8);
        top.Children.Add(_st);
        root.Children.Add(top);

        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        Content = root;

        _in.Changed += UpdatePreview;
        _in.Box.KeyDown += (_, e) => { if (e.Key == Key.Enter) Add(); };
        _refresh.Tick += (_, _) => Fill();
        _refresh.Start();
        Fill();
        UpdatePreview();
        Loaded += (_, _) => _in.Focus();
    }

    private void Quick(string when)
    {
        string t = _in.Text.Trim();
        // keep the words the user already typed, replace any time words they typed
        var p = Reminders.Parse(t, DateTime.Now);
        _in.Text = (p?.Text ?? t) + " " + when;
        _in.Box.CaretIndex = _in.Text.Length;
        _in.Focus();
    }

    private void UpdatePreview()
    {
        string t = _in.Text.Trim();
        if (t.Length == 0) { _preview.Text = "Say when, in your own words."; _preview.Foreground = Ui.Dim; return; }
        var p = Reminders.Parse(t, DateTime.Now);
        if (p == null) { _preview.Text = "No time found yet. Add something like \"in 20 min\", \"at 6pm\" or \"tomorrow 9am\"."; _preview.Foreground = Ui.Dim; return; }
        _preview.Text = "Will ring " + Human(p.Value.Due) + ":  " + p.Value.Text;
        _preview.Foreground = Ui.Good;
    }

    private static string Human(DateTime local)
    {
        DateTime now = DateTime.Now;
        string day = local.Date == now.Date ? "today" : local.Date == now.Date.AddDays(1) ? "tomorrow" : local.ToString("ddd d MMM", System.Globalization.CultureInfo.InvariantCulture);
        return day + " at " + local.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Add()
    {
        var p = Reminders.Parse(_in.Text.Trim(), DateTime.Now);
        if (p == null) { _st.Say("Add a time, for example \"in 20 min\" or \"tomorrow 9am\".", true); return; }
        var l = Reminders.Load();
        l.Add(new ReminderItem { Text = p.Value.Text, DueUtc = p.Value.Due.ToUniversalTime(), Repeat = _repeat.Index == 1 ? "daily" : _repeat.Index == 2 ? "weekly" : "none" });
        Reminders.Save(l);
        _in.Text = "";
        _st.Say("Added");
        Fill();
    }

    private void Fill()
    {
        _list.Children.Clear();
        var l = Reminders.Load().OrderBy(r => r.Fired).ThenBy(r => r.DueUtc).ToList();
        if (l.Count == 0) { _list.Children.Add(Ui.Label("No reminders yet.", 13, true)); return; }
        foreach (var r in l)
        {
            var it = r;
            var g = new Grid { Margin = new Thickness(0, 0, 0, 8) };
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var info = new StackPanel();
            var tx = Ui.Label(it.Text, 14, false, true);
            if (it.Fired) tx.Foreground = Ui.Dim;
            info.Children.Add(tx);
            DateTime local = it.DueUtc.ToLocalTime();
            string sub = it.Fired ? "done" : (local < DateTime.Now ? "overdue, " : "") + Human(local) + (it.Repeat == "daily" ? "  -  every day" : it.Repeat == "weekly" ? "  -  every week" : "");
            info.Children.Add(Ui.Label(sub, 12, true));
            g.Children.Add(info);
            var del = Ui.Btn("Delete", () => { var all = Reminders.Load(); all.RemoveAll(x => x.Id == it.Id); Reminders.Save(all); Fill(); });
            del.Margin = new Thickness(8, 0, 0, 0);
            Grid.SetColumn(del, 1);
            g.Children.Add(del);
            _list.Children.Add(Ui.Card(g, 12));
            _list.Children[_list.Children.Count - 1].SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 8));
        }
    }
}

/// <summary>The small window that pops up when a reminder rings.</summary>
internal sealed class RemindAlert : Window
{
    public RemindAlert(string id)
    {
        Ui.Setup(this, "Reminder", 380, 210);
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        var r = Reminders.Load().FirstOrDefault(x => x.Id == id);
        string text = r?.Text ?? "Reminder";
        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(Ui.Label("REMINDER", 11, true, true));
        var t = Ui.Label(text, 20, false, true);
        t.Margin = new Thickness(0, 6, 0, 16);
        root.Children.Add(t);
        var row = Ui.Row(
            Ui.Btn("Done", () => { Reminders.Complete(id); Close(); }, true),
            Ui.Btn("In 10 min", () => { Reminders.Snooze(id, 10); Close(); }),
            Ui.Btn("In 1 hour", () => { Reminders.Snooze(id, 60); Close(); }));
        root.Children.Add(row);
        Content = root;
        Loaded += (_, _) =>
        {
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - Width - 20;
            Top = wa.Bottom - Height - 20;
        };
        Closed += (_, _) => { if (r != null && !r.Fired) Reminders.Complete(id); };
    }
}
