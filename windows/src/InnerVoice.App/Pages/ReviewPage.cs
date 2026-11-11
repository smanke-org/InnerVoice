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

/// Every line that needs the narrator, in book order. Each card is one paragraph with the
/// lines in it to review, the paragraphs around it for context (as many as the Context
/// control asks for, within the chapter), and the best guess for each line to confirm.
public sealed class ReviewPage : Page
{
    public override PageKind Kind => PageKind.Review;
    const int PageSize = 100;
    const int MaxContext = 5;
    int shown = PageSize;
    readonly Button less = new() { Content = "−", Width = 30, Padding = new Thickness(0, 2), HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly Button more = new() { Content = "+", Width = 30, Padding = new Thickness(0, 2), HorizontalContentAlignment = HorizontalAlignment.Center };
    readonly TextBlock contextLabel = Ui.Text("", 13, brush: Look.Brush(Look.SecondaryInk));
    readonly StackPanel toolbar;

    public ReviewPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        ToolTip.SetTip(less, "Show fewer paragraphs around each line");
        ToolTip.SetTip(more, "Show more paragraphs around each line");
        less.Click += (_, _) => SetContext(Context - 1);
        more.Click += (_, _) => SetContext(Context + 1);
        contextLabel.VerticalAlignment = VerticalAlignment.Center;
        contextLabel.MinWidth = 150;
        var label = Ui.Text("Context", 13);
        label.VerticalAlignment = VerticalAlignment.Center;
        toolbar = Ui.Row(Look.Sm, label, less, more, contextLabel);
        Refresh(true);
    }

    static int Context => Math.Clamp(AppSettings.Current.ReviewContext, 0, MaxContext);

    void SetContext(int n)
    {
        n = Math.Clamp(n, 0, MaxContext);
        if (n == Context) return;
        AppSettings.Update(s => s.ReviewContext = n);
        Refresh(false);
    }

    public override Control Toolbar => toolbar;

    public override string Subtitle => Document.Project.LinesNeedingYou is var n && n == 0 ? "All confirmed" : $"{n:N0} to review";

    void UpdateControl()
    {
        int n = Context;
        contextLabel.Text = n == 0 ? "This paragraph only" : $"{n} {(n == 1 ? "paragraph" : "paragraphs")} around";
        less.IsEnabled = n > 0;
        more.IsEnabled = n < MaxContext;
    }

    public override void Refresh(bool textChanged)
    {
        UpdateControl();
        var p = Document.Project;
        var index = Document.Index;
        var needing = p.Lines.Where(p.NeedsYou).ToList();
        if (needing.Count == 0)
        {
            bool untagged = p.LastTagged is null && p.Lines.Count > 0;
            Content = Ui.Empty(untagged ? "Nothing Tagged Yet" : "All Lines Confirmed",
                untagged ? "Tag Speakers finds who says each line; the ones it isn't sure of show up here." : "Every line has a speaker you can rely on.");
            return;
        }
        // One card per paragraph, in book order.
        var groups = needing.Where(l => index.Positions.ContainsKey(l.ParagraphID))
            .GroupBy(l => l.ParagraphID)
            .OrderBy(g => index.Positions[g.Key].Global)
            .ToList();
        var list = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Xl), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        int lastChapter = -1;
        foreach (var group in groups.Take(shown))
        {
            var pos = index.Positions[group.Key];
            if (pos.Chapter != lastChapter)
            {
                lastChapter = pos.Chapter;
                var heading = Ui.Headline(p.Chapters[pos.Chapter].ShownTitle());
                heading.Margin = new Thickness(0, Look.Md, 0, 0);
                list.Children.Add(heading);
            }
            list.Children.Add(Card(pos, group.OrderBy(l => l.Range.Location).ToList()));
        }
        if (groups.Count > shown)
            list.Children.Add(Ui.Button($"Show {Math.Min(PageSize, groups.Count - shown)} More Paragraphs", () => { shown += PageSize; Refresh(false); }));
        Content = Ui.Scroll(list);
    }

    Control Card(BookIndex.Position pos, List<Line> lines)
    {
        var p = Document.Project;
        var chapter = p.Chapters[pos.Chapter];
        int n = Context;
        var column = new StackPanel { Spacing = Look.Sm };
        column.Children.Add(Ui.Caption($"{chapter.ShownTitle()} · ¶{pos.Paragraph + 1}"));
        // The paragraphs before, the paragraph, the paragraphs after: never past the chapter.
        for (int i = Math.Max(0, pos.Paragraph - n); i < pos.Paragraph; i++) column.Children.Add(ParagraphText(chapter.Paragraphs[i], [], context: true));
        column.Children.Add(ParagraphText(chapter.Paragraphs[pos.Paragraph], lines.Select(l => l.Id).ToHashSet(), context: false));
        for (int i = pos.Paragraph + 1; i <= Math.Min(chapter.Paragraphs.Count - 1, pos.Paragraph + n); i++) column.Children.Add(ParagraphText(chapter.Paragraphs[i], [], context: true));
        // One row per line to review.
        var rows = new StackPanel { Spacing = Look.Sm, Margin = new Thickness(0, Look.Xs, 0, 0) };
        foreach (var line in lines) rows.Children.Add(LineRow(line));
        column.Children.Add(new Border { Child = rows, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, Look.Sm, 0, 0) });
        return Ui.Card(column, Look.Md);
    }

    /// A paragraph with each line tinted in its speaker's color. The lines under review are
    /// red; context paragraphs are smaller and dimmer so the paragraph under review stands out.
    Control ParagraphText(Paragraph para, HashSet<Guid> reviewing, bool context)
    {
        var p = Document.Project;
        var index = Document.Index;
        var text = new SelectableTextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontFamily = Look.ReadingFamily(AppSettings.Current.ReadingSerif),
            FontSize = context ? 13 : 15,
            LineHeight = context ? 19 : 22,
            Foreground = Look.Brush(context ? Look.SecondaryInk : Look.Ink),
            TextAlignment = para.Style == ParagraphStyle.SceneBreak ? TextAlignment.Center : TextAlignment.Left,
            Margin = new Thickness(para.Style == ParagraphStyle.BlockQuote ? 20 : 0, 0, 0, 0),
        };
        var inlines = new InlineCollection();
        // Who speaks in a context paragraph, in their color before the text, so the
        // turn-taking is easy to follow.
        if (context)
        {
            var names = index.LinesIn(para.Id).Select(i => p.Lines[i]).Select(l => p.Character(l.SpeakerID))
                .Where(c => c is not null).Cast<BookCharacter>().DistinctBy(c => c.Id).ToList();
            foreach (var c in names)
            {
                var hue = Look.Hex(c.ColorHex);
                inlines.Add(new Run(c.Name.ToUpperInvariant()) { FontFamily = Look.UIFamily, FontSize = 10, FontWeight = FontWeight.SemiBold,
                    Foreground = Look.Brush(Look.IsDark ? hue.Blend(Colors.White, 0.35) : hue.Blend(Colors.Black, 0.25)) });
                inlines.Add(new Run("  ") { FontSize = 10 });
            }
        }
        int cursor = 0;
        foreach (var line in index.LinesIn(para.Id).Select(i => p.Lines[i]).OrderBy(l => l.Range.Location))
        {
            int start = Math.Clamp(line.Range.Location, cursor, para.Text.Length), end = Math.Clamp(line.Range.End, start, para.Text.Length);
            if (start > cursor) inlines.Add(new Run(para.Text[cursor..start]));
            if (end > start)
            {
                bool under = reviewing.Contains(line.Id);
                var color = under || p.NeedsYou(line) ? Look.Attention : Look.Hex(p.Character(line.SpeakerID)?.ColorHex);
                var run = new Run(para.Text[start..end]) { Background = Look.Brush(color.WithAlpha(under ? 0.24 : context ? 0.12 : 0.16)) };
                if (under) { run.Foreground = Look.Brush(Look.Ink); run.FontWeight = FontWeight.Medium; }
                if (line.Kind == LineKind.Thought) run.FontStyle = FontStyle.Italic;
                inlines.Add(run);
            }
            cursor = end;
        }
        if (cursor < para.Text.Length) inlines.Add(new Run(para.Text[cursor..]));
        text.Inlines = inlines;
        return text;
    }

    Control LineRow(Line line)
    {
        var p = Document.Project;
        var guess = p.Character(line.SpeakerID);
        var quote = Ui.Text("“" + p.TextOf(line, Document.Index) + "”", 12, brush: Look.Brush(Look.SecondaryInk), maxLines: 1);
        quote.VerticalAlignment = VerticalAlignment.Center;
        quote.MaxWidth = 360;
        var left = Ui.Row(Look.Sm, Ui.Pill(guess is null ? "Who's Speaking?" : $"{guess.Name}?", Look.Attention), quote);
        if (line.Kind != LineKind.Spoken) left.Children.Add(Ui.Caption(line.Kind.Title()));
        var buttons = Ui.Row(Look.Sm);
        if (guess is not null)
            buttons.Children.Add(Ui.Button($"Confirm {guess.Name}", () => Document.Assign([line.Id], guess.Id), primary: true,
                tip: $"The best guess, {line.Confidence:P0} sure"));
        var assign = new Button { Content = "Assign…" };
        assign.Click += (_, _) =>
        {
            var flyout = new Flyout { Placement = PlacementMode.Bottom };
            flyout.Content = new AssignPanel(Document, [line.Id], (_, _) => flyout.Hide(), () => flyout.Hide());
            flyout.ShowAt(assign);
        };
        buttons.Children.Add(assign);
        buttons.Children.Add(Ui.Button("Show", () => Workspace.Show(line.ParagraphID), tip: "Show it in the manuscript"));
        var row = new DockPanel();
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(left);
        return row;
    }
}

static class ControlExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
