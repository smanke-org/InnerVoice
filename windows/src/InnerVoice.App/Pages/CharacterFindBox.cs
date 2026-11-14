using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// The "Find a character" field the Cast and Voices pages show inside the page (as on the Mac):
/// a magnifier, a clear button while it has text, and Esc clears it. Ctrl+F on those pages focuses it.
/// The pages keep one instance and never rebuild it, so it keeps focus while the lists refill.
public sealed class CharacterFindBox
{
    public TextBox Box { get; }
    readonly Button clear;

    /// Raised on every change of the text (typing, the clear button, Esc).
    public event Action? Changed;

    public CharacterFindBox()
    {
        var magnifier = new Avalonia.Controls.Shapes.Path
        {
            // A ring and a handle, drawn with a stroke so it reads at 14 px.
            Data = Geometry.Parse("M 6,1 A 5,5 0 1 1 5.99,1 Z M 9.6,9.6 L 14,14"),
            Stroke = Look.Brush(Look.SecondaryInk), StrokeThickness = 1.5, Width = 14, Height = 14, Stretch = Stretch.Uniform,
            Margin = new Thickness(Look.Sm, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false,
        };
        clear = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 11, Foreground = Look.Brush(Look.SecondaryInk) },
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), Padding = new Thickness(Look.Sm, 2),
            VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Focusable = false, Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(clear, "Clear");
        Box = new TextBox
        {
            PlaceholderText = "Find a character",
            InnerLeftContent = magnifier,
            InnerRightContent = clear,
        };
        ToolTip.SetTip(Box, "Find a character by name, nickname or initials, words in their notes, or the voice they're cast with (Ctrl+F)");
        clear.Click += (_, _) => { Clear(); Box.Focus(); };
        Box.TextChanged += (_, _) =>
        {
            clear.IsVisible = Query.Length > 0;
            Changed?.Invoke();
        };
        Box.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || string.IsNullOrEmpty(Box.Text)) return;
            Clear();
            e.Handled = true;
        };
    }

    /// What the narrator typed, trimmed.
    public string Query => Box.Text?.Trim() ?? "";

    public void Clear() => Box.Text = "";

    /// Ctrl+F: puts the cursor in the field with its text selected.
    public void FocusField()
    {
        Box.Focus();
        Box.SelectAll();
    }

    /// The characters the query finds (everyone when it's empty).
    public bool Matches(BookCharacter c) =>
        Query is not { Length: > 0 } q || c.SearchMatches(q, c.Voices?.Values.Select(v => InnerVoice.Core.Speech.VoiceNames.Name(v.Choice)) ?? []);

    /// The words shown when nothing matches.
    public string NoMatchText => $"No characters match “{Query}”.";
}
