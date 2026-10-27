using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace LightIsland;

// Pages inside the expanded island: Media, Favorite apps, Clock + Weather, Calendar, System, Downloads, Clipboard.
// Swap pages with the mouse wheel or the dots.
public partial class MainWindow
{
    private const int ViewCount = 7;
    private const int ViewMedia = 0, ViewApps = 1, ViewClock = 2, ViewCal = 3, ViewSys = 4, ViewDl = 5, ViewClip = 6;

    private static readonly FontFamily GlyphFont = new("Segoe MDL2 Assets");
    private static Brush TileBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x28)));
    private static Brush HoverBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    private static readonly Brush GreenBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x7C, 0xFF, 0xB2)));
    private static readonly Brush RedBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x6B)));
    private static readonly Brush DimBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77)));
    private static readonly Brush BadgeBrush = MakeFrozen(new SolidColorBrush(Color.FromArgb(0xE0, 0x55, 0x55, 0x58)));
    private static readonly Brush BadgeHoverBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)));
    private static readonly Brush SoftBrush = MakeFrozen(new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA)));

    private Grid[] _pages = Array.Empty<Grid>();
    private int _view;
    private long _lastWheel;

    // ---------- Page switching ----------

    private void InitViews()
    {
        _pages = new[] { PageMedia, PageApps, PageClock, PageCal, PageSys, PageDl, PageClip };

        for (int i = 0; i < ViewCount; i++)
        {
            int index = i;
            var dot = new System.Windows.Shapes.Ellipse
            {
                Width = 6,
                Height = 6,
                Margin = new Thickness(3, 0, 3, 0),
                Opacity = i == 0 ? 1.0 : 0.3,
                Cursor = Cursors.Hand
            };
            dot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "TextBrush");
            dot.MouseLeftButtonUp += (_, _) => GoToView(index, index > _view ? 1 : -1);
            Dots.Children.Add(dot);
        }
    }

    private void UpdateDots()
    {
        for (int i = 0; i < Dots.Children.Count; i++)
            Dots.Children[i].Opacity = i == _view ? 1.0 : 0.3;
    }

    private void GoToView(int index, int direction)
    {
        index = ((index % ViewCount) + ViewCount) % ViewCount;
        if (index == _view) return;

        PerfLog.Mark("page " + _view + " -> " + index, 800);
        Grid oldPage = _pages[_view];
        Grid newPage = _pages[index];
        _view = index;
        UpdateDots();

        const double shift = 22;

        // The new page is built BEFORE the movement starts (animations run on the clock: a build in the middle
        // would make the first frames jump), and both pages are moved as pictures (cheap) until they settle.
        newPage.Opacity = 0;
        newPage.Visibility = Visibility.Visible;
        OnViewShown(index);
        PageCache(oldPage, true);
        PageCache(newPage, true);
        newPage.UpdateLayout();

        // Old page fades out and slides away
        var oldMove = (TranslateTransform)oldPage.RenderTransform;
        var fadeOut = new DoubleAnimation(0, Dur(130));
        fadeOut.Completed += (_, _) =>
        {
            if (!ReferenceEquals(_pages[_view], oldPage)) oldPage.Visibility = Visibility.Collapsed;
            PageCache(oldPage, false);
        };
        oldPage.BeginAnimation(OpacityProperty, fadeOut);
        oldMove.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(-direction * shift, Dur(200))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });

        // New page fades in and slides into place
        var newMove = (TranslateTransform)newPage.RenderTransform;
        newPage.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, Dur(220)));
        var slideIn = new DoubleAnimation(direction * shift, 0, Dur(360))
        {
            EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut }
        };
        slideIn.Completed += (_, _) => PageCache(newPage, false); // settled: live content again (buttons, bars)
        newMove.BeginAnimation(TranslateTransform.XProperty, slideIn);
    }

    private void PageCache(Grid page, bool on)
    {
        try { page.CacheMode = on ? new BitmapCache(_dpi) { SnapsToDevicePixels = true } : null; }
        catch { }
    }

    // Called when the island is closed (and hidden): go back to the music page without any
    // animation, and stop the timers of the page that was open (System page timer etc.)
    private void ResetToMediaPage()
    {
        if (_view == ViewMedia) return;

        for (int i = 0; i < _pages.Length; i++)
        {
            Grid pg = _pages[i];
            pg.BeginAnimation(OpacityProperty, null);
            PageCache(pg, false);
            if (pg.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
            }
            pg.Opacity = 1;
            pg.Visibility = i == ViewMedia ? Visibility.Visible : Visibility.Collapsed;
        }

        _view = ViewMedia;
        UpdateDots();
        UpdateStatsTimer();
        UpdateProgressTimer();
    }

    // Switches to a page without any animation (used while the island is closed)
    private void ShowViewInstant(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
        {
            Grid pg = _pages[i];
            pg.BeginAnimation(OpacityProperty, null);
            PageCache(pg, false);
            if (pg.RenderTransform is TranslateTransform tt)
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
            }
            pg.Opacity = 1;
            pg.Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        }
        _view = index;
        UpdateDots();
        OnViewShown(index);
    }

    private void OnViewShown(int index)
    {
        UpdateStatsTimer(); // measures only while the System page is open
        switch (index)
        {
            case ViewCal: BuildCalendar(); break;
            case ViewApps: _editApps = false; RefreshApps(); break;
            case ViewClock: UpdateClock(); break;
            case ViewDl: RenderDl(); break;
            case ViewClip: RenderClip(); break;
        }
    }

    // Mouse wheel swaps pages, unless the mouse is over a list that can still scroll
    private void Island_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_expanded) return;

        bool up = e.Delta > 0;
        var sv = FindScrollViewer(e.OriginalSource as DependencyObject);
        if (sv != null)
        {
            bool canScroll = up ? sv.VerticalOffset > 0 : sv.VerticalOffset < sv.ScrollableHeight;
            if (canScroll) return; // let the list scroll
        }

        e.Handled = true;

        long now = Environment.TickCount64;
        if (now - _lastWheel < 300) return; // one swap per wheel flick
        _lastWheel = now;

        int dir = up ? -1 : 1;
        GoToView(_view + dir, dir);
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject? d)
    {
        while (d != null)
        {
            if (d is ScrollViewer sv) return sv;
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    // ---------- Drag and drop ----------
    // Drop an app or file on the island: it is added to Favorite apps.

    private void Island_DragEnter(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        e.Effects = DragDropEffects.Copy;
        e.Handled = true;

        _leaveTimer.Stop();
        if (!_expanded) Animate(true);
        if (_view != ViewApps) GoToView(ViewApps, 1);
    }

    private void Island_DragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        e.Effects = DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Island_DragLeave(object sender, DragEventArgs e)
    {
        if (_holdOpen) return;

        Point p = e.GetPosition(Island);
        bool inside = p.X >= 0 && p.Y >= 0 && p.X < Island.ActualWidth && p.Y < Island.ActualHeight;
        if (!inside) Animate(false);
    }

    private void Island_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        e.Handled = true;

        foreach (string f in files) AddUnique(_s.FavoriteApps, f, 40);
        _s.Save();
        if (_view != ViewApps) GoToView(ViewApps, 1);
        else RefreshApps();
    }

    // ---------- Helpers ----------

    private static void AddUnique(List<string> list, string path, int max)
    {
        if (list.Count >= max) return;
        if (list.Exists(x => string.Equals(x, path, StringComparison.OrdinalIgnoreCase))) return;
        list.Add(path);
    }

    private static void Launch(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch
        {
            // File was moved or deleted, or no app can open it
        }
    }

    // Opens a favorite app or tray file. The island stays open for a moment so a new window
    // covering the island does not make it close at once.
    private void OpenItem(string path)
    {
        Launch(path);
        _holdUntil = Environment.TickCount64 + 2500;

        // Keep the island above the newly opened window
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            Topmost = false;
            Topmost = true;
        };
        t.Start();
    }

    private static void ShowInFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "/select,\"" + path + "\"") { UseShellExecute = true });
        }
        catch
        {
            // ignore
        }
    }

    private static string DisplayName(string path)
    {
        string n = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrEmpty(n) ? path : n;
    }

    // Keeps the island open while a context menu is showing
    private void AttachMenu(FrameworkElement el, ContextMenu menu)
    {
        menu.Opened += (_, _) => _holdOpen = true;
        menu.Closed += (_, _) =>
        {
            _holdOpen = false;
            if (!Island.IsMouseOver) Animate(false);
        };
        el.ContextMenu = menu;
    }

    private static TextBlock GlyphBlock(string glyph, double size, Brush? color)
    {
        var tb = new TextBlock
        {
            Text = glyph,
            FontFamily = GlyphFont,
            FontSize = size,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (color != null) tb.Foreground = color;
        else tb.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush"); // follows the text color setting
        return tb;
    }

    private static Grid ThreeColumnRow(double height)
    {
        var row = new Grid { Height = height, Background = Brushes.Transparent };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        return row;
    }

    // ---------- Page 2: Favorite apps ----------
    // Click = open. "Edit" (or right click on an app) = edit mode: tap the red x to remove an app,
    // drag an app to a new place to rearrange. No pop-up menu is used (pop-ups were the cause of the crash).

    private bool _editApps;
    private Border? _dragTile;
    private bool _dragMoved;

    private void SetEditApps(bool on)
    {
        _editApps = on;
        if (!on) EndDrag();
        RefreshApps();
    }

    private void AppsEdit_Click(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        SetEditApps(!_editApps);
    }

    private void EndDrag()
    {
        try
        {
            if (_dragTile != null && _dragTile.IsMouseCaptured) _dragTile.ReleaseMouseCapture();
        }
        catch { }
        _dragTile = null;
        _holdOpen = false;
    }

    // Saves the order that is currently shown
    private void SaveAppOrder()
    {
        var order = new List<string>();
        foreach (UIElement c in AppsPanel.Children)
            if (c is Border b && b.Tag is string s) order.Add(s);
        if (order.Count == _s.FavoriteApps.Count)
        {
            _s.FavoriteApps.Clear();
            _s.FavoriteApps.AddRange(order);
            _s.Save();
        }
    }

    private void RefreshApps()
    {
        try
        {
            AppsPanel.Children.Clear();
            AppsHint.Visibility = _s.FavoriteApps.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (_s.FavoriteApps.Count == 0) _editApps = false;

            AppsEditBtn.Text = _editApps ? "Done" : "Edit";
            AppsEditBtn.Visibility = _s.FavoriteApps.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            AppsTitle.Text = _editApps ? "Drag to rearrange \u00B7 \u2715 to remove" : "Favorite apps";

            foreach (string path in _s.FavoriteApps.ToArray())
                AppsPanel.Children.Add(MakeAppTile(path));
        }
        catch
        {
            // never let a refresh problem close the app
        }
    }

    private Border MakeAppTile(string p)
    {
        var tile = new Border
        {
            Width = 44,
            Height = 44,
            CornerRadius = new CornerRadius(12),
            Background = TileBrush,
            BorderBrush = (Brush)Resources["TileStroke"],
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = _editApps ? Cursors.SizeAll : Cursors.Hand,
            ToolTip = DisplayName(p),
            Tag = p
        };

        var inner = new Grid();

        ImageSource? icon = IconHelper.Get(p);
        if (icon != null)
        {
            inner.Children.Add(new Image
            {
                Source = icon,
                Width = 26,
                Height = 26,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            });
        }
        else
        {
            var g = GlyphBlock("\uE8A5", 18, null);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            inner.Children.Add(g);
        }

        if (_editApps)
        {
            // Small red x in the corner: removes the app
            var x = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Background = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, -6, -6, 0),
                Cursor = Cursors.Hand,
                ToolTip = "Remove"
            };
            var xg = GlyphBlock("\uE711", 8, Brushes.White);
            xg.HorizontalAlignment = HorizontalAlignment.Center;
            x.Child = xg;
            x.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                RemoveFavorite(p);
            };
            inner.Children.Add(x);
        }

        tile.Child = inner;

        tile.MouseEnter += (_, _) => { if (_dragTile == null) tile.Background = HoverBrush; };
        tile.MouseLeave += (_, _) => { if (_dragTile != tile) tile.Background = TileBrush; };

        if (!_editApps)
        {
            tile.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                OpenItem(p);
            };
            // Right click = edit mode (no pop-up menu)
            tile.MouseRightButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                SetEditApps(true);
            };
        }
        else
        {
            tile.MouseLeftButtonDown += (_, ev) =>
            {
                ev.Handled = true;
                _dragTile = tile;
                _dragMoved = false;
                _holdOpen = true; // the island stays open while dragging
                tile.CaptureMouse();
            };
            tile.MouseMove += (_, ev) =>
            {
                if (_dragTile != tile || !tile.IsMouseCaptured) return;
                Point pos = ev.GetPosition(AppsPanel);
                int from = AppsPanel.Children.IndexOf(tile);
                for (int i = 0; i < AppsPanel.Children.Count; i++)
                {
                    if (AppsPanel.Children[i] is not FrameworkElement c || ReferenceEquals(c, tile)) continue;
                    Point o = c.TranslatePoint(new Point(0, 0), AppsPanel);
                    if (pos.X >= o.X && pos.X < o.X + c.ActualWidth + 8 && pos.Y >= o.Y && pos.Y < o.Y + c.ActualHeight + 8)
                    {
                        AppsPanel.Children.RemoveAt(from);
                        AppsPanel.Children.Insert(i, tile);
                        _dragMoved = true;
                        break;
                    }
                }
            };
            tile.MouseLeftButtonUp += (_, ev) =>
            {
                ev.Handled = true;
                if (_dragTile != tile) return;
                bool moved = _dragMoved;
                EndDrag();
                if (moved) SaveAppOrder();
                tile.Background = TileBrush;
                if (!Island.IsMouseOver) Animate(false);
            };
            tile.LostMouseCapture += (_, _) =>
            {
                if (_dragTile == tile)
                {
                    bool moved = _dragMoved;
                    _dragTile = null;
                    _holdOpen = false;
                    if (moved) SaveAppOrder();
                }
            };
        }

        return tile;
    }

    private void RemoveFavorite(string path)
    {
        EndDrag();
        _s.FavoriteApps.RemoveAll(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
        _s.Save();
        RefreshApps();
    }

    private void AddApp_Click(object sender, MouseButtonEventArgs e)
    {
        _holdOpen = true;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Add favorite apps",
            Filter = "Apps and shortcuts|*.exe;*.lnk;*.bat;*.url|All files|*.*",
            CheckFileExists = true,
            Multiselect = true
        };
        bool? ok = dlg.ShowDialog(this);

        _holdOpen = false;

        if (ok == true)
        {
            foreach (string f in dlg.FileNames) AddUnique(_s.FavoriteApps, f, 40);
            _s.Save();
            RefreshApps();
        }

        if (!Island.IsMouseOver) Animate(false);
    }
}
