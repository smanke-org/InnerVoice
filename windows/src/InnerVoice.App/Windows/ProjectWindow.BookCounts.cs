using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// A small "123" button right of the page title (the Mac's sits right of the book's name);
/// click it for the book's words and billable characters and what the whole book would
/// cost with each cloud voice engine. Read from `BookIndex`, so it costs nothing on refresh.
public sealed partial class ProjectWindow
{
    Button? bookCounts;

    Control BookCountsButton()
    {
        var button = new Button
        {
            Content = new TextBlock { Text = "123", FontWeight = FontWeight.SemiBold },
            Padding = new Thickness(Look.Sm, 2),
            Margin = new Thickness(Look.Sm, 0, 0, 0),
            CornerRadius = new CornerRadius(12),
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(button, "Words, billable characters and what cloud voices would cost");
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = CountsPanel();
        button.Flyout = flyout;
        bookCounts = button;
        RefreshBookCounts();
        return button;
    }

    Control CountsPanel()
    {
        TextBlock Big(int n) => new() { Text = n.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("en-US")), FontSize = 20, FontWeight = FontWeight.SemiBold };
        var cost = CostHelp();
        var cut = cost.IndexOf("\n\n", StringComparison.Ordinal);
        return new StackPanel
        {
            Spacing = Look.Sm, Width = 380, Margin = new Thickness(Look.Md),
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.Sm, Children = { Big(Document.Index.BookWords), new TextBlock { Text = "words", VerticalAlignment = VerticalAlignment.Bottom } } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.Sm, Children = { Big(Document.Index.BillableCharacters), new TextBlock { Text = "billable characters: letters, numbers, spaces and punctuation", TextWrapping = TextWrapping.Wrap, MaxWidth = 260, VerticalAlignment = VerticalAlignment.Bottom } } },
                new Separator(),
                new TextBlock { Text = cut < 0 ? cost : cost[(cut + 2)..], TextWrapping = TextWrapping.Wrap, Opacity = 0.75 },
            },
        };
    }

    string CostHelp() => BookCounts.CostHelp(Document.Index.BillableCharacters, Document.Project.EngineInUse(), GeminiVoices.Shared.Model, eleven: ElevenLabsEngine.Shared.CurrentModel);

    void RefreshBookCounts()
    {
        if (bookCounts is null) return;
        Avalonia.Automation.AutomationProperties.SetName(bookCounts,
            $"Word count: {Document.Index.BookWords} words, {Document.Index.BillableCharacters} billable characters. Shows what the book would cost with cloud voices.");
    }
}
