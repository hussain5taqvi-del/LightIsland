using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace LightIsland;

/// <summary>The small permission card of a voice command: Allow once / Always allow / Cancel.</summary>
internal sealed class VoiceConfirm : Window
{
    private readonly TaskCompletionSource<int> _result = new();
    private readonly DispatcherTimer _timeout = new() { Interval = TimeSpan.FromSeconds(15) };

    /// <summary>0 = cancel, 1 = allow once, 2 = always allow.</summary>
    public static Task<int> AskAsync(string heard, string what, bool light, Color accent)
    {
        var w = new VoiceConfirm(heard, what, light, accent);
        w.Show();
        return w._result.Task;
    }

    private VoiceConfirm(string heard, string what, bool light, Color accent)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.Manual;

        Color bg = light ? Color.FromRgb(0xF7, 0xF5, 0xF3) : Color.FromRgb(0x12, 0x12, 0x14);
        Color fg = light ? Color.FromRgb(0x1C, 0x1C, 0x1E) : Colors.White;
        Color mutedC = Color.FromRgb(0x8E, 0x8E, 0x93);
        Color pill = light ? Color.FromRgb(0xE6, 0xE2, 0xDE) : Color.FromRgb(0x2A, 0x2A, 0x30);
        double lum = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255.0;
        Color onAccent = lum > 0.62 ? Color.FromRgb(0x10, 0x10, 0x12) : Colors.White;

        var card = new Border
        {
            Background = new SolidColorBrush(bg),
            BorderBrush = new SolidColorBrush(light ? Color.FromRgb(0xDD, 0xD9, 0xD5) : Color.FromRgb(0x2A, 0x2A, 0x30)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(20),
            Padding = new Thickness(18, 14, 18, 14),
            MinWidth = 300,
            MaxWidth = 420,
            Margin = new Thickness(14)
        };
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };

        var sp = new StackPanel();
        sp.Children.Add(new TextBlock { Text = "“" + heard + "”", FontSize = 11.5, Foreground = new SolidColorBrush(mutedC), TextTrimming = TextTrimming.CharacterEllipsis });
        sp.Children.Add(new TextBlock
        {
            Text = what, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(fg),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 12)
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Button("Allow once", 1, accent, onAccent, true));
        row.Children.Add(Button("Always allow", 2, pill, fg, false));
        row.Children.Add(Button("Cancel", 0, pill, fg, false));
        sp.Children.Add(row);
        sp.Children.Add(new TextBlock { Text = "Enter = allow once   Esc = cancel", FontSize = 10, Foreground = new SolidColorBrush(mutedC), Margin = new Thickness(0, 9, 0, 0) });
        card.Child = sp;
        Content = card;

        Loaded += (_, _) =>
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + (wa.Width - ActualWidth) / 2;
            Top = wa.Top + 64;
            GlassWindow.ForceForeground(this);
            card.Opacity = 0;
            card.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; Finish(1); }
            else if (e.Key == Key.Escape) { e.Handled = true; Finish(0); }
        };
        Deactivated += (_, _) => Finish(0);
        _timeout.Tick += (_, _) => Finish(0);
        _timeout.Start();
    }

    private UIElement Button(string text, int result, Color bg, Color fg, bool primary)
    {
        var b = new Border
        {
            Background = new SolidColorBrush(bg),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 0),
            Cursor = Cursors.Hand,
            Child = new TextBlock { Text = text, FontSize = 12.5, FontWeight = primary ? FontWeights.Bold : FontWeights.SemiBold, Foreground = new SolidColorBrush(fg) }
        };
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; Finish(result); };
        return b;
    }

    private void Finish(int r)
    {
        if (_result.Task.IsCompleted) return;
        _timeout.Stop();
        _result.TrySetResult(r);
        try { Close(); } catch { }
    }
}
