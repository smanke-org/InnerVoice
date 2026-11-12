using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// The teleprompter for the booth: one paragraph at a time in large type, the speaker's
/// color and voice cue, the next speaker below. Space or → moves on, ← goes back; foot pedals
/// that send keys work too. It opens where the narrator left off in this book (remembered on
/// this PC, in the app's settings). InnerVoice doesn't record sound.
public sealed class TeleprompterPage : Page
{
    public override PageKind Kind => PageKind.Teleprompter;
    readonly StackPanel toolbar;
    Guid? shownParagraph;

    public TeleprompterPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        Focusable = true;
        var smaller = Ui.Button("A−", () => AppSettings.Update(s => s.RecordFontSize = Math.Max(18, s.RecordFontSize - 4)), tip: "Smaller text");
        var larger = Ui.Button("A+", () => AppSettings.Update(s => s.RecordFontSize = Math.Min(64, s.RecordFontSize + 4)), tip: "Bigger text");
        toolbar = Ui.Row(Look.Sm, smaller, larger);
        AppSettings.Changed += () => Refresh(false);
        Refresh(true);
    }

    public override Control Toolbar => toolbar;

    /// Every readable paragraph in book order (headings and scene breaks are skipped).
    List<(int Chapter, Paragraph Paragraph)> All() =>
        Document.Project.Chapters.SelectMany((ch, c) => ch.Paragraphs
            .Where(p => p.Style is ParagraphStyle.Body or ParagraphStyle.BlockQuote).Select(p => (c, p))).ToList();

    /// The paragraph shown in this window, else the one the narrator left off at in this book,
    /// else the first.
    int CurrentIndex(List<(int Chapter, Paragraph Paragraph)> all)
    {
        Guid?[] candidates = [Workspace.TeleprompterParagraph, RememberedParagraph()];
        foreach (var candidate in candidates)
        {
            if (candidate is not { } id) continue;
            int i = all.FindIndex(x => x.Paragraph.Id == id);
            if (i >= 0) return i;
        }
        return 0;
    }

    /// The book's key in Settings: projects have no id of their own, so it's the file's path.
    string? PlaceKey => Document.FilePath is { } path ? Path.GetFullPath(path) : null;

    Guid? RememberedParagraph() =>
        PlaceKey is { } key && AppSettings.Current.TeleprompterPlaces.TryGetValue(key, out var id) ? id : null;

    /// Shows a paragraph and remembers it as this book's place on this PC (test runs never save).
    void Remember(Guid paragraphID)
    {
        Workspace.TeleprompterParagraph = paragraphID;
        if (PlaceKey is not { } key) return;
        if (AppSettings.Current.TeleprompterPlaces.TryGetValue(key, out var old) && old == paragraphID) return;
        AppSettings.UpdateQuietly(s => s.TeleprompterPlaces[key] = paragraphID);
    }

    (int Chapter, Paragraph Paragraph)? Current()
    {
        var all = All();
        return all.Count == 0 ? null : all[CurrentIndex(all)];
    }

    public override string Subtitle
    {
        get
        {
            int n = All().Count;
            return $"{n:N0} {(n == 1 ? "paragraph" : "paragraphs")}";
        }
    }

    public override void Refresh(bool textChanged)
    {
        var all = All();
        if (all.Count == 0)
        {
            shownParagraph = null;
            Content = Ui.Empty("Nothing to Read", "This manuscript has no paragraphs to read.");
            return;
        }
        int i = CurrentIndex(all);
        var chapterIndex = all[i].Chapter;
        shownParagraph = all[i].Paragraph.Id;

        var root = new DockPanel { Background = Look.Brush(Look.Paper) };
        var header = PositionHeader(all, i, chapterIndex);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var controls = Controls();
        DockPanel.SetDock(controls, Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(Prompter(all, i));
        Content = root;
    }

    Control PositionHeader(List<(int Chapter, Paragraph Paragraph)> all, int i, int chapterIndex)
    {
        var p = Document.Project;
        var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, all.Count), Value = i + 1, Width = 220, VerticalAlignment = VerticalAlignment.Center };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(Look.Xl, Look.Md) };
        grid.Children.Add(Ui.Column(2, Ui.Headline(p.Chapters[chapterIndex].ShownTitle()), Ui.Caption($"Paragraph {i + 1:N0} of {all.Count:N0}")));
        Grid.SetColumn(bar, 1);
        grid.Children.Add(bar);
        return new Border { Child = grid, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 0, 0, 1), Background = Look.Brush(Look.Chrome) };
    }

    Control Prompter(List<(int Chapter, Paragraph Paragraph)> all, int i)
    {
        var p = Document.Project;
        var index = Document.Index;
        var current = all[i].Paragraph;
        var lines = index.LinesIn(current.Id).Select(x => p.Lines[x]).ToList();
        var next = i + 1 < all.Count ? all[i + 1].Paragraph : null;
        double size = AppSettings.Current.RecordFontSize;
        var family = Look.ReadingFamily(AppSettings.Current.ReadingSerif);
        var col = new StackPanel { Spacing = Look.Xxl, Margin = new Thickness(Look.Section * 2, Look.Section), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Center };
        if (i > 0)
            col.Children.Add(new TextBlock { Text = all[i - 1].Paragraph.Text, FontFamily = family, FontSize = size * 0.55, Foreground = Look.Brush(Look.TertiaryInk), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis });
        var speakers = UniqueSpeakers(lines);
        if (speakers.Count > 0)
        {
            var chips = new WrapPanel { ItemSpacing = Look.Sm, LineSpacing = Look.Sm };
            foreach (var id in speakers) chips.Children.Add(SpeakerChip(id));
            col.Children.Add(chips);
        }
        col.Children.Add(Colored(current, lines, size, family));
        if (next is not null)
        {
            var nextSpeakers = UniqueSpeakers(index.LinesIn(next.Id).Select(x => p.Lines[x]).ToList());
            col.Children.Add(Ui.Column(Look.Xs,
                Ui.Text(nextSpeakers.Count == 0 ? "Next: narration" : "Next: " + string.Join(", ", nextSpeakers.Select(s => p.Character(s)?.Name
                    + (p.Settings.CastingStyle is not null && Document.Shades().TryGetValue(s, out var sh) && sh.Cue is { } cue ? $" ({cue})" : ""))), 12, FontWeight.SemiBold, Look.Brush(Look.SecondaryInk)),
                new TextBlock { Text = next.Text, FontFamily = family, FontSize = size * 0.55, Foreground = Look.Brush(Look.TertiaryInk), TextWrapping = TextWrapping.Wrap, MaxLines = 2, TextTrimming = TextTrimming.WordEllipsis }));
        }
        return Ui.Scroll(col);
    }

    static List<Guid> UniqueSpeakers(List<Line> lines)
    {
        var seen = new List<Guid>();
        foreach (var l in lines) if (l.SpeakerID is { } s && !seen.Contains(s)) seen.Add(s);
        return seen;
    }

    Control SpeakerChip(Guid id)
    {
        var c = Document.Project.Character(id);
        // Casting Style: how to shade them first ("Shade: lower, gravelly"), then the summary.
        var shade = c is not null && Document.Project.Settings.CastingStyle is not null && Document.Shades().TryGetValue(c.Id, out var s) ? s.Cue : null;
        var cue = string.Join(" · ", new[] { shade is null ? null : "Shade: " + shade, c is null ? null : ProjectActions.VoiceCue(c) }.Where(x => !string.IsNullOrEmpty(x)));
        var inner = Ui.Row(Look.Sm, Ui.Avatar(c?.ColorHex, c?.Initials ?? "?", 26),
            Ui.Column(0, Ui.Headline(c?.Name ?? ""), Ui.Text(cue, 11, brush: Look.Brush(Look.SecondaryInk), maxLines: 1)));
        return new Border
        {
            Child = inner,
            Padding = new Thickness(Look.Md, Look.Sm),
            CornerRadius = new CornerRadius(20),
            Background = Look.Brush(Look.Hex(c?.ColorHex).WithAlpha(0.14)),
            MaxWidth = 480,
        };
    }

    /// The paragraph with each line tinted in its speaker's color.
    Control Colored(Paragraph p, List<Line> lines, double size, FontFamily family)
    {
        var tb = new SelectableTextBlock { FontFamily = family, FontSize = size, TextWrapping = TextWrapping.Wrap, LineHeight = size * 1.35, Foreground = Look.Brush(Look.Ink) };
        var project = Document.Project;
        int cursor = 0;
        var inlines = new InlineCollection();
        foreach (var line in lines.OrderBy(l => l.Range.Location))
        {
            int start = Math.Clamp(line.Range.Location, 0, p.Text.Length), end = Math.Clamp(line.Range.End, start, p.Text.Length);
            if (start < cursor) continue;
            if (start > cursor) inlines.Add(new Run(p.Text[cursor..start]));
            var color = project.NeedsYou(line) ? Look.Attention : Look.Hex(project.Character(line.SpeakerID)?.ColorHex);
            var run = new Run(p.Text[start..end]) { Background = Look.Brush(color.WithAlpha(0.22)) };
            if (line.Kind == LineKind.Thought) run.FontStyle = FontStyle.Italic;
            inlines.Add(run);
            cursor = end;
        }
        if (cursor < p.Text.Length) inlines.Add(new Run(p.Text[cursor..]));
        tb.Inlines = inlines;
        return tb;
    }

    Control Controls()
    {
        var previous = Ui.Button("‹ Previous", () => Move(-1), tip: "Go back a paragraph (←)");
        var next = Ui.Button("Next ›", () => Move(1), primary: true, tip: "Next paragraph (Space or →)");
        var row = new DockPanel { Margin = new Thickness(Look.Xl, Look.Md) };
        var left = Ui.Row(Look.Md, previous, next);
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        return new Border { Child = row, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 1, 0, 0), Background = Look.Brush(Look.Chrome) };
    }

    void Move(int delta)
    {
        var all = All();
        if (all.Count == 0) return;
        int next = Math.Clamp(CurrentIndex(all) + delta, 0, all.Count - 1);
        Remember(all[next].Paragraph.Id);
        Refresh(false);
        Focus();
    }

    bool MatchesNext(Key key) => AppSettings.Current.RecordNextKey switch
    {
        NextKey.Down => key == Key.Down,
        NextKey.PageDown => key == Key.PageDown,
        NextKey.Return => key == Key.Enter,
        _ => key == Key.Space,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Source is TextBox) { base.OnKeyDown(e); return; }
        if (MatchesNext(e.Key) || e.Key == Key.Right) { Move(1); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Up or Key.PageUp) { Move(-1); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    public override void Shown()
    {
        // Opening the page (from the sidebar, a shortcut or Open Teleprompter Here) keeps its place.
        if (Current() is { } c)
        {
            Remember(c.Paragraph.Id);
            if (c.Paragraph.Id != shownParagraph) Refresh(false);
        }
        Focus();
    }
}
