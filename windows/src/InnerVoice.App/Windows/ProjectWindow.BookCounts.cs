using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Words and billable characters beside the page title (the Mac shows them beside the book's
/// name). Hover for what the whole book would cost with each cloud voice engine; click to
/// keep that breakdown open. Read from `BookIndex`, so it costs nothing on refresh.
public sealed partial class ProjectWindow
{
    Button? bookCounts;
    readonly TextBlock bookCountsText = Ui.Caption("");

    Control BookCountsButton()
    {
        var button = new Button
        {
            Content = bookCountsText,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Look.Sm, 0),
            Margin = new Thickness(Look.Sm, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        flyout.Opening += (_, _) => flyout.Content = new TextBlock
        {
            Text = CostHelp(), MaxWidth = 420, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(Look.Md),
        };
        button.Flyout = flyout;
        bookCounts = button;
        RefreshBookCounts();
        return button;
    }

    string CostHelp() => BookCounts.CostHelp(Document.Index.BillableCharacters, Document.Project.EngineInUse(), GeminiVoices.Shared.Model, eleven: ElevenLabsEngine.Shared.CurrentModel);

    void RefreshBookCounts()
    {
        if (bookCounts is null) return;
        bookCountsText.Text = BookCounts.Label(Document.Index.BookWords, Document.Index.BillableCharacters);
        ToolTip.SetTip(bookCounts, CostHelp());
        Avalonia.Automation.AutomationProperties.SetName(bookCounts,
            $"{Document.Index.BookWords} words, {Document.Index.BillableCharacters} billable characters. Shows what the book would cost with cloud voices.");
    }
}
