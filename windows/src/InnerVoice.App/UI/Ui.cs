using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace InnerVoice.App.UI;

/// Small builders so pages read like their layout.
public static class Ui
{
    public static TextBlock Text(string text, double size = 13, FontWeight weight = FontWeight.Normal, IBrush? brush = null,
                                 TextWrapping wrap = TextWrapping.NoWrap, int maxLines = 0)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight, TextWrapping = wrap };
        if (brush is not null) t.Foreground = brush;
        if (maxLines > 0) { t.MaxLines = maxLines; t.TextTrimming = TextTrimming.CharacterEllipsis; }
        return t;
    }

    public static TextBlock Title(string text) => Text(text, 20, FontWeight.SemiBold);
    public static TextBlock Headline(string text) => Text(text, 14, FontWeight.SemiBold);
    public static TextBlock Caption(string text, TextWrapping wrap = TextWrapping.NoWrap) =>
        Text(text, 12, brush: Look.Brush(Look.SecondaryInk), wrap: wrap);

    public static Button Button(string label, Action onClick, bool primary = false, string? tip = null)
    {
        var b = new Button { Content = label };
        if (primary) b.Classes.Add("accent");
        b.Click += (_, _) => onClick();
        if (tip is not null) ToolTip.SetTip(b, tip);
        return b;
    }

    public static StackPanel Row(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal, Spacing = spacing, VerticalAlignment = VerticalAlignment.Center };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Column(double spacing, params Control[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Vertical, Spacing = spacing };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static Border Dot(string? hex, double size = 10) => new()
    {
        Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
        Background = Look.Brush(Look.Hex(hex)), VerticalAlignment = VerticalAlignment.Center,
    };

    /// A round badge with a character's initials in their color.
    public static Border Avatar(string? hex, string initials, double size = 26)
    {
        var c = Look.Hex(hex);
        return new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = Look.Brush(c.WithAlpha(Look.IsDark ? 0.35 : 0.22)),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = initials, FontSize = size * 0.4, FontWeight = FontWeight.Bold,
                Foreground = Look.Brush(Look.IsDark ? c.Blend(Colors.White, 0.35) : c.Blend(Colors.Black, 0.3)),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    public static Border Divider(bool vertical = false) => new()
    {
        Background = Look.Brush(Look.Separator),
        Height = vertical ? double.NaN : 1,
        Width = vertical ? 1 : double.NaN,
    };

    /// The empty-state message a page shows when it has nothing yet.
    public static Control Empty(string title, string message, Control? action = null)
    {
        var s = Column(Look.Sm, Text(title, 17, FontWeight.SemiBold), Caption(message, TextWrapping.Wrap));
        s.HorizontalAlignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        s.MaxWidth = 420;
        foreach (var c in s.Children.OfType<TextBlock>()) c.TextAlignment = TextAlignment.Center;
        if (action is not null)
        {
            action.HorizontalAlignment = HorizontalAlignment.Center;
            action.Margin = new Thickness(0, Look.Sm, 0, 0);
            s.Children.Add(action);
        }
        return s;
    }

    public static Border Card(Control child, double padding = Look.Lg) => new()
    {
        Child = child,
        Padding = new Thickness(padding),
        CornerRadius = new CornerRadius(8),
        Background = Look.Brush(Look.Chrome),
        BorderBrush = Look.Brush(Look.Separator),
        BorderThickness = new Thickness(1),
    };

    public static Border Pill(string text, Color color) => new()
    {
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(8, 2),
        Background = Look.Brush(color.WithAlpha(Look.IsDark ? 0.28 : 0.16)),
        Child = new TextBlock
        {
            Text = text, FontSize = 11, FontWeight = FontWeight.SemiBold,
            Foreground = Look.Brush(Look.IsDark ? color.Blend(Colors.White, 0.35) : color.Blend(Colors.Black, 0.25)),
        },
    };

    public static ScrollViewer Scroll(Control content) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
}
