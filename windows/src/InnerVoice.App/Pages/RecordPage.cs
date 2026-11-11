using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// The teleprompter for the booth: one paragraph at a time in large type, the speaker's
/// color and voice cue, the next speaker below. Space or → moves on, ← goes back, R marks a
/// retake, M marks recorded. Foot pedals that send keys work too.
public sealed class RecordPage : Page
{
    public override PageKind Kind => PageKind.Record;
    readonly StackPanel toolbar;
    readonly ToggleButton recordedToggle = new() { Content = "Recorded" };
    bool updating;

    public RecordPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        Focusable = true;
        recordedToggle.IsCheckedChanged += (_, _) =>
        {
            if (updating || Current() is not { } p) return;
            Document.SetRecorded(p.Paragraph.Id, recordedToggle.IsChecked == true);
        };
        ToolTip.SetTip(recordedToggle, "Mark this paragraph recorded (M)");
        var smaller = Ui.Button("A−", () => AppSettings.Update(s => s.RecordFontSize = Math.Max(18, s.RecordFontSize - 4)), tip: "Smaller text");
        var larger = Ui.Button("A+", () => AppSettings.Update(s => s.RecordFontSize = Math.Min(64, s.RecordFontSize + 4)), tip: "Bigger text");
        toolbar = Ui.Row(Look.Sm, recordedToggle, smaller, larger);
        AppSettings.Changed += () => Refresh(false);
        Refresh(true);
    }

    public override Control Toolbar => toolbar;

    /// Every readable paragraph in book order (headings and scene breaks are skipped).
    List<(int Chapter, Paragraph Paragraph)> All() =>
        Document.Project.Chapters.SelectMany((ch, c) => ch.Paragraphs
            .Where(p => p.Style is ParagraphStyle.Body or ParagraphStyle.BlockQuote).Select(p => (c, p))).ToList();

    int CurrentIndex(List<(int Chapter, Paragraph Paragraph)> all)
    {
        if (Workspace.RecordParagraph is { } id)
        {
            int i = all.FindIndex(x => x.Paragraph.Id == id);
            if (i >= 0) return i;
        }
        // Start at the first paragraph not yet recorded.
        int first = all.FindIndex(x => !Document.Project.Recorded.Contains(x.Paragraph.Id));
        return first < 0 ? 0 : first;
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
            var all = All();
            int done = all.Count(x => Document.Project.Recorded.Contains(x.Paragraph.Id));
            return $"{done:N0} of {all.Count:N0} paragraphs recorded";
        }
    }

    public override void Refresh(bool textChanged)
    {
        var all = All();
        if (all.Count == 0)
        {
            Content = Ui.Empty("Nothing to Read", "This manuscript has no paragraphs to record.");
            return;
        }
        int i = CurrentIndex(all);
        var (chapterIndex, current) = all[i];
        var p = Document.Project;
        updating = true;
        recordedToggle.IsChecked = p.Recorded.Contains(current.Id);
        updating = false;

        var root = new DockPanel { Background = Look.Brush(Look.Paper) };
        var header = ProgressHeader(all, i, chapterIndex);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var controls = Controls(all, i);
        DockPanel.SetDock(controls, Dock.Bottom);
        root.Children.Add(controls);
        root.Children.Add(Prompter(all, i));
        Content = root;
    }

    Control ProgressHeader(List<(int Chapter, Paragraph Paragraph)> all, int i, int chapterIndex)
    {
        var p = Document.Project;
        var inChapter = all.Where(x => x.Chapter == chapterIndex).ToList();
        int done = inChapter.Count(x => p.Recorded.Contains(x.Paragraph.Id));
        int total = all.Count(x => p.Recorded.Contains(x.Paragraph.Id));
        var bar = new ProgressBar { Minimum = 0, Maximum = Math.Max(1, inChapter.Count), Value = done, Width = 220 };
        var right = Ui.Column(Look.Xs,
            Ui.Caption($"{done * 100 / Math.Max(1, inChapter.Count)}% of chapter · {total * 100 / Math.Max(1, all.Count)}% of book recorded"),
            bar);
        right.HorizontalAlignment = HorizontalAlignment.Right;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(Look.Xl, Look.Md) };
        grid.Children.Add(Ui.Column(2, Ui.Headline(p.Chapters[chapterIndex].ShownTitle()), Ui.Caption($"Paragraph {i + 1} of {all.Count}")));
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
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
        if (p.Recorded.Contains(current.Id))
            col.Children.Add(Ui.Text("✓ Recorded", 14, FontWeight.SemiBold, Look.Brush(Look.Recorded)));
        if (p.Retakes.FirstOrDefault(r => r.ParagraphID == current.Id && !r.Done) is { } retake)
            col.Children.Add(Ui.Text("↺ Retake" + (retake.Note.Length == 0 ? "" : ": " + retake.Note), 14, FontWeight.SemiBold, Look.Brush(Look.Retake), TextWrapping.Wrap));
        if (next is not null)
        {
            var nextSpeakers = UniqueSpeakers(index.LinesIn(next.Id).Select(x => p.Lines[x]).ToList());
            col.Children.Add(Ui.Column(Look.Xs,
                Ui.Text(nextSpeakers.Count == 0 ? "Next: narration" : "Next: " + string.Join(", ", nextSpeakers.Select(s => p.Character(s)?.Name)), 12, FontWeight.SemiBold, Look.Brush(Look.SecondaryInk)),
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
        var cue = c is null ? "" : ProjectActions.VoiceCue(c);
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

    Control Controls(List<(int Chapter, Paragraph Paragraph)> all, int i)
    {
        var previous = Ui.Button("‹ Previous", () => Move(-1), tip: "Go back a paragraph (←)");
        var next = Ui.Button("Next ›", () => Move(1), primary: true, tip: "Move on and mark this paragraph recorded (Space or →)");
        var retake = Ui.Button("↺ Retake…", () => _ = MarkRetake(), tip: "Mark this paragraph to read again, with a note (R)");
        var row = new DockPanel { Margin = new Thickness(Look.Xl, Look.Md) };
        var left = Ui.Row(Look.Md, previous, next);
        DockPanel.SetDock(left, Dock.Left);
        row.Children.Add(left);
        retake.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(retake);
        return new Border { Child = row, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 1, 0, 0), Background = Look.Brush(Look.Chrome) };
    }

    void Move(int delta)
    {
        var all = All();
        if (all.Count == 0) return;
        int i = CurrentIndex(all);
        if (delta > 0 && AppSettings.Current.RecordMarkOnAdvance) Document.SetRecorded(all[i].Paragraph.Id, true);
        int next = Math.Clamp(i + delta, 0, all.Count - 1);
        Workspace.RecordParagraph = all[next].Paragraph.Id;
        Refresh(false);
        Focus();
    }

    async Task MarkRetake()
    {
        if (Current() is not { } c) return;
        var existing = Document.Project.Retakes.FirstOrDefault(r => r.ParagraphID == c.Paragraph.Id && !r.Done);
        var note = await Dialogs.Ask(this, "Mark Retake", "", "Mark Retake", existing?.Note ?? "", "What to fix (optional)");
        if (note is not null) Document.AddRetake(c.Paragraph.Id, note.Trim());
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
        else if (e.Key == Key.R && e.KeyModifiers == KeyModifiers.None) { _ = MarkRetake(); e.Handled = true; }
        else if (e.Key == Key.M && e.KeyModifiers == KeyModifiers.None && Current() is { } c) { Document.ToggleRecorded(c.Paragraph.Id); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    public override void Shown() => Focus();
}
