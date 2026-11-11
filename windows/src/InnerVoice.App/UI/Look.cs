using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using InnerVoice.Core;

namespace InnerVoice.App.UI;

/// Colors, fonts and spacing. The accent is deep indigo; red is reserved for Who's
/// Speaking? and Unsure. Character colors come from CharacterPalette.
public static class Look
{
    public static readonly Color Accent = Color.Parse("#4F46E5");
    public static readonly Color Attention = Color.Parse(CharacterPalette.AttentionHex);
    public static readonly Color Gray = Color.Parse("#888888");

    public static bool IsDark => Application.Current?.ActualThemeVariant == ThemeVariant.Dark;

    public static Color Hex(string? hex) => hex is not null && Color.TryParse(hex, out var c) ? c : Gray;

    public static Color WithAlpha(this Color c, double a) => Color.FromArgb((byte)Math.Round(Math.Clamp(a, 0, 1) * 255), c.R, c.G, c.B);

    public static Color Blend(this Color a, Color b, double t) => Color.FromArgb(255,
        (byte)Math.Round(a.R + (b.R - a.R) * t), (byte)Math.Round(a.G + (b.G - a.G) * t), (byte)Math.Round(a.B + (b.B - a.B) * t));

    public static IBrush Brush(Color c) => new SolidColorBrush(c);

    /// The text color on the reading surface.
    public static Color Ink => IsDark ? Color.Parse("#ECECEE") : Color.Parse("#1D1D1F");
    public static Color SecondaryInk => IsDark ? Color.Parse("#A1A1A6") : Color.Parse("#6E6E73");
    public static Color TertiaryInk => IsDark ? Color.Parse("#6E6E73") : Color.Parse("#AEAEB2");
    public static Color Paper => IsDark ? Color.Parse("#1E1E20") : Color.Parse("#FFFFFF");
    public static Color Chrome => IsDark ? Color.Parse("#2A2A2D") : Color.Parse("#F5F5F7");
    public static Color Separator => IsDark ? Color.Parse("#3A3A3C") : Color.Parse("#E5E5EA");
    public static Color Recorded => Color.Parse("#16A34A");
    public static Color Retake => Color.Parse("#EA580C");

    /// A serif reading face: Sitka Text on Windows, then Georgia; the system face otherwise.
    public static FontFamily ReadingFamily(bool serif) =>
        serif ? new FontFamily("Sitka Text, Georgia, Cambria, New York, Times New Roman, serif")
              : new FontFamily("Segoe UI Variable Text, Segoe UI, Inter, Helvetica Neue, sans-serif");

    public static readonly FontFamily UIFamily = new("Segoe UI Variable Text, Segoe UI, Inter, Helvetica Neue, sans-serif");

    /// The app icon for every window's title bar and the taskbar.
    public static Avalonia.Controls.WindowIcon? WindowIcon => icon ??= LoadIcon();
    static Avalonia.Controls.WindowIcon? icon;

    static Avalonia.Controls.WindowIcon? LoadIcon()
    {
        try { return new Avalonia.Controls.WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://InnerVoice/Assets/InnerVoice.ico"))); }
        catch { return null; }
    }

    // Spacing (the Mac's Space tokens).
    public const double Xs = 4, Sm = 8, Md = 12, Lg = 16, Xl = 20, Xxl = 28, Section = 36;
}
