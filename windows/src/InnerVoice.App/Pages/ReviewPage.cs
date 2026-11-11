using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Every line that needs the narrator, in book order, with the paragraph around it and the
/// best guess to confirm.
public sealed class ReviewPage : Page
{
    public override PageKind Kind => PageKind.Review;
    const int PageSize = 150;
    int shown = PageSize;

    public ReviewPage(ProjectDocument document, Workspace workspace) : base(document, workspace) => Refresh(true);

    public override string Subtitle => Document.Project.LinesNeedingYou is var n && n == 0 ? "All confirmed" : $"{n:N0} to review";

    public override void Refresh(bool textChanged)
    {
        var p = Document.Project;
        var index = Document.Index;
        var needing = p.Lines.Where(p.NeedsYou).ToList();
        if (needing.Count == 0)
        {
            Content = Ui.Empty(p.LastTagged is null && p.Lines.Count > 0 ? "Nothing Tagged Yet" : "All Lines Confirmed",
                p.LastTagged is null && p.Lines.Count > 0 ? "Tag Speakers finds who says each line; the ones it isn't sure of show up here." : "Every line has a speaker you can rely on.");
            return;
        }
        var list = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Xl), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        int lastChapter = -1;
        foreach (var line in needing.Take(shown))
        {
            if (!index.Positions.TryGetValue(line.ParagraphID, out var pos)) continue;
            if (pos.Chapter != lastChapter)
            {
                lastChapter = pos.Chapter;
                list.Children.Add(Ui.Headline(p.Chapters[pos.Chapter].ShownTitle()).Also(h => h.Margin = new Thickness(0, Look.Md, 0, 0)));
            }
            list.Children.Add(Row(line, pos));
        }
        if (needing.Count > shown)
            list.Children.Add(Ui.Button($"Show {Math.Min(PageSize, needing.Count - shown)} More", () => { shown += PageSize; Refresh(false); }));
        Content = Ui.Scroll(list);
    }

    Control Row(Line line, BookIndex.Position pos)
    {
        var p = Document.Project;
        var para = p.Chapters[pos.Chapter].Paragraphs[pos.Paragraph];
        var guess = p.Character(line.SpeakerID);
        var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, FontFamily = Look.ReadingFamily(AppSettings.Current.ReadingSerif), FontSize = 14 };
        var inlines = new InlineCollection();
        int start = Math.Clamp(line.Range.Location, 0, para.Text.Length), end = Math.Clamp(line.Range.End, start, para.Text.Length);
        if (start > 0) inlines.Add(new Run(para.Text[..start]) { Foreground = Look.Brush(Look.SecondaryInk) });
        inlines.Add(new Run(para.Text[start..end]) { Background = Look.Brush(Look.Attention.WithAlpha(0.2)) });
        if (end < para.Text.Length) inlines.Add(new Run(para.Text[end..]) { Foreground = Look.Brush(Look.SecondaryInk) });
        text.Inlines = inlines;

        var buttons = Ui.Row(Look.Sm);
        if (guess is not null)
        {
            var confirm = Ui.Button($"Confirm {guess.Name}", () => Document.Assign([line.Id], guess.Id), primary: true,
                tip: $"The best guess, {line.Confidence:P0} sure");
            buttons.Children.Add(confirm);
        }
        var assign = new Button { Content = "Assign…" };
        assign.Click += (_, _) =>
        {
            var flyout = new Flyout { Placement = PlacementMode.Bottom };
            flyout.Content = new AssignPanel(Document, [line.Id], (_, _) => flyout.Hide(), () => flyout.Hide());
            flyout.ShowAt(assign);
        };
        buttons.Children.Add(assign);
        buttons.Children.Add(Ui.Button("Show", () => Workspace.Show(line.ParagraphID), tip: "Show it in the manuscript"));

        var header = Ui.Row(Look.Sm, Ui.Caption(Document.Index.Reference(line.ParagraphID)),
            Ui.Pill(guess is null ? "Who's Speaking?" : $"{guess.Name}?", Look.Attention),
            Ui.Caption(line.Kind == LineKind.Spoken ? "" : line.Kind.Title()));
        return Ui.Card(Ui.Column(Look.Sm, header, text, buttons), Look.Md);
    }
}

static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
