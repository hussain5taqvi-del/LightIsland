using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace LightIsland;

internal sealed class SnippetItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N").Substring(0, 8);
    public string Title { get; set; } = "";
    public string Folder { get; set; } = "";
    public string Text { get; set; } = "";
    public bool Pinned { get; set; }
}

/// <summary>
/// Saved snippets with folders and search, plus the recent clipboard items handed over by the island.
/// Placeholders {date} {time} {clipboard} are filled in when a snippet is used.
/// </summary>
internal sealed class SnippetsTool : Window
{
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private static string F => ToolHost.DataFile("snippets.json");

    private List<SnippetItem> _items = new();
    private readonly List<string> _recent = new();
    private readonly Ui.Chips _tabs = new(new[] { "Snippets", "Recent clips" }, 0);
    private readonly Ui.Input _search = new("Search...", false, 0);
    private readonly WrapPanel _folders = new();
    private readonly StackPanel _list = new();
    private readonly StackPanel _editor = new();
    private readonly Ui.Input _eTitle = new("Title", false, 0), _eFolder = new("Folder (optional)", false, 0), _eText = new("Text. You can use {date} {time} {clipboard}", true, 140);
    private readonly Ui.Status _st = new();
    private string _folder = "";
    private SnippetItem? _editing;

    public SnippetsTool(string arg)
    {
        Ui.Setup(this, "Snippets & Clips", 560, 720, true);
        Load();
        LoadHandoff();

        var root = new Grid { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var top = new StackPanel();
        top.Children.Add(_tabs);
        _search.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(_search);
        top.Children.Add(_folders);
        top.Children.Add(Ui.Row(Ui.Btn("+ New snippet", () => Edit(new SnippetItem { Folder = _folder }, true), true), Ui.Btn("Save clipboard as snippet", FromClipboard)));
        _st.Margin = new Thickness(0, 4, 0, 6);
        top.Children.Add(_st);

        _editor.Visibility = Visibility.Collapsed;
        _eTitle.Margin = new Thickness(0, 0, 0, 8);
        _eFolder.Margin = new Thickness(0, 0, 0, 8);
        _editor.Children.Add(_eTitle);
        _editor.Children.Add(_eFolder);
        _editor.Children.Add(_eText);
        var er = Ui.Row(Ui.Btn("Save", SaveEdit, true), Ui.Btn("Cancel", () => _editor.Visibility = Visibility.Collapsed));
        er.Margin = new Thickness(0, 8, 0, 0);
        _editor.Children.Add(er);
        var ec = Ui.Card(_editor, 12);
        ec.Visibility = Visibility.Collapsed;
        _editor.Tag = ec;
        ec.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(ec);
        root.Children.Add(top);

        var sv = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _list };
        Grid.SetRow(sv, 1);
        root.Children.Add(sv);
        Content = root;

        _tabs.Selected += _ => Fill();
        _search.Changed += Fill;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && _editor.Visibility == Visibility.Visible) { HideEditor(); e.Handled = true; } };
        if (_recent.Count > 0 && arg == "recent") _tabs.Select(1, false);
        Fill();
        Loaded += (_, _) => _search.Focus();
    }

    private void Load()
    {
        try { if (File.Exists(F)) _items = JsonSerializer.Deserialize<List<SnippetItem>>(File.ReadAllText(F)) ?? new(); }
        catch { _items = new(); }
    }

    private void Save()
    {
        try
        {
            string tmp = F + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items));
            File.Move(tmp, F, true);
        }
        catch { _st.Say("Could not save.", true); }
    }

    /// <summary>The island writes its in-memory clipboard history to a temp file right before it starts this tool. Read it once and delete it.</summary>
    private void LoadHandoff()
    {
        string f = ToolHost.DataFile("clips-handoff.json");
        try
        {
            if (File.Exists(f))
            {
                var l = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(f));
                if (l != null) _recent.AddRange(l);
            }
        }
        catch { }
        try { File.Delete(f); } catch { }
    }

    private void Fill()
    {
        _list.Children.Clear();
        _folders.Children.Clear();
        bool snippets = _tabs.Index == 0;
        _search.Visibility = Visibility.Visible;
        string q = _search.Text.Trim();

        if (!snippets)
        {
            _folders.Visibility = Visibility.Collapsed;
            var rec = _recent.Where(r => q.Length == 0 || r.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (rec.Count == 0) { _list.Children.Add(Ui.Label(_recent.Count == 0 ? "No recent clips. Open this from the island's Toolbox to see what you copied." : "Nothing matches.", 13, true)); return; }
            foreach (var r in rec) AddCard(null, r);
            return;
        }

        var names = _items.Select(i => i.Folder).Where(f => f.Length > 0).Distinct().OrderBy(f => f).ToList();
        if (_folder.Length > 0 && !names.Contains(_folder)) _folder = "";
        _folders.Visibility = names.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (names.Count > 0)
        {
            var all = new List<string> { "All" };
            all.AddRange(names);
            var chips = new Ui.Chips(all.ToArray(), _folder.Length == 0 ? 0 : all.IndexOf(_folder));
            chips.Selected += i => { _folder = i == 0 ? "" : all[i]; Fill(); };
            _folders.Children.Add(chips);
        }

        var list = _items.Where(i => (_folder.Length == 0 || i.Folder == _folder) &&
                                     (q.Length == 0 || i.Title.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || i.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                         .OrderByDescending(i => i.Pinned).ThenBy(i => i.Title).ToList();
        if (list.Count == 0) { _list.Children.Add(Ui.Label(_items.Count == 0 ? "No snippets yet. Add text you type often: addresses, replies, code, links." : "Nothing matches.", 13, true)); return; }
        foreach (var s in list) AddCard(s, s.Text);
    }

    private void AddCard(SnippetItem? s, string text)
    {
        var sp = new StackPanel();
        if (s != null)
        {
            var t = Ui.Label((s.Pinned ? "★ " : "") + (s.Title.Length > 0 ? s.Title : "(untitled)") + (s.Folder.Length > 0 ? "   ·   " + s.Folder : ""), 14, false, true);
            sp.Children.Add(t);
        }
        var prev = Ui.Label(text.Length > 300 ? text.Substring(0, 300) + "..." : text, 12.5, true);
        prev.MaxHeight = 52;
        prev.TextTrimming = TextTrimming.CharacterEllipsis;
        sp.Children.Add(prev);

        var btns = Ui.Row(Ui.Btn("Copy", () => { Ui.Copy(Expand(text)); _st.Say("Copied"); }, true), Ui.Btn("Paste", () => PasteText(Expand(text))));
        btns.Margin = new Thickness(0, 8, 0, 0);
        if (s != null)
        {
            btns.Children.Add(Ui.Btn(s.Pinned ? "Unpin" : "Pin", () => { s.Pinned = !s.Pinned; Save(); Fill(); }));
            btns.Children.Add(Ui.Btn("Edit", () => Edit(s, false)));
            btns.Children.Add(Ui.Btn("Delete", () => { _items.Remove(s); Save(); Fill(); }));
        }
        else
        {
            btns.Children.Add(Ui.Btn("Save as snippet", () => { Edit(new SnippetItem { Text = text, Title = FirstWords(text) }, true); }));
        }
        sp.Children.Add(btns);
        var card = Ui.Card(sp, 12);
        card.Margin = new Thickness(0, 0, 0, 8);
        _list.Children.Add(card);
    }

    private static string FirstWords(string t)
    {
        t = t.Replace("\r", " ").Replace("\n", " ").Trim();
        return t.Length > 32 ? t.Substring(0, 32).Trim() + "..." : t;
    }

    private static string Expand(string t)
    {
        if (t.IndexOf('{') < 0) return t;
        var now = DateTime.Now;
        t = t.Replace("{date}", now.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture))
             .Replace("{time}", now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
        if (t.Contains("{clipboard}"))
        {
            string c = "";
            try { if (Clipboard.ContainsText()) c = Clipboard.GetText(); } catch { }
            t = t.Replace("{clipboard}", c);
        }
        return t;
    }

    private void PasteText(string text)
    {
        Ui.Copy(text);
        IntPtr prev = ToolHost.PreviousForeground;
        if (prev == IntPtr.Zero) { _st.Say("Copied. Press Ctrl+V where you want it."); return; }
        Hide();
        SetForegroundWindow(prev);
        var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            keybd_event(0x11, 0, 0, UIntPtr.Zero);          // Ctrl down
            keybd_event(0x56, 0, 0, UIntPtr.Zero);          // V down
            keybd_event(0x56, 0, 2, UIntPtr.Zero);          // V up
            keybd_event(0x11, 0, 2, UIntPtr.Zero);          // Ctrl up
            Close();
        };
        t.Start();
    }

    private void FromClipboard()
    {
        string c = "";
        try { if (Clipboard.ContainsText()) c = Clipboard.GetText(); } catch { }
        if (c.Length == 0) { _st.Say("The clipboard has no text.", true); return; }
        Edit(new SnippetItem { Text = c, Title = FirstWords(c), Folder = _folder }, true);
    }

    private void Edit(SnippetItem s, bool isNew)
    {
        _editing = isNew ? s : s;
        _editorIsNew = isNew;
        _eTitle.Text = s.Title;
        _eFolder.Text = s.Folder;
        _eText.Text = s.Text;
        ((Border)_editor.Tag!).Visibility = Visibility.Visible;
        _editor.Visibility = Visibility.Visible;
        _eTitle.Focus();
    }

    private bool _editorIsNew;

    private void HideEditor()
    {
        _editor.Visibility = Visibility.Collapsed;
        ((Border)_editor.Tag!).Visibility = Visibility.Collapsed;
    }

    private void SaveEdit()
    {
        if (_editing == null) return;
        if (_eText.Text.Length == 0) { _st.Say("The text is empty.", true); return; }
        _editing.Title = _eTitle.Text.Trim();
        _editing.Folder = _eFolder.Text.Trim();
        _editing.Text = _eText.Text;
        if (_editing.Title.Length == 0) _editing.Title = FirstWords(_editing.Text);
        if (_editorIsNew) _items.Add(_editing);
        Save();
        _editing = null;
        HideEditor();
        _tabs.Select(0, false);
        Fill();
        _st.Say("Saved");
    }
}
