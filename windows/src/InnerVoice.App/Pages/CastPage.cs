using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Every character as a card; a click opens their page with the summary.
public sealed partial class CastPage : Page
{
    public override PageKind Kind => PageKind.Cast;
    enum Sort { Lines, Name, Appearance }
    Sort sort = Sort.Lines;
    readonly ComboBox sortBox = new() { ItemsSource = new[] { "Most Lines", "Name", "First Appearance" }, SelectedIndex = 0, Width = 150 };
    readonly Button back;
    readonly StackPanel toolbar;

    public CastPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        sortBox.SelectionChanged += (_, _) => { sort = (Sort)Math.Max(0, sortBox.SelectedIndex); Refresh(false); };
        back = Ui.Button("‹ Cast", () => { Workspace.SelectedCharacter = null; Refresh(false); }, tip: "Back to the cast");
        var narrators = Ui.Button("Narrators…", () => { if (TopLevel.GetTopLevel(this) is Window w) InnerVoice.App.Windows.NarratorsWindow.Show(w, Document); },
            tip: "Who reads the book, and which characters each narrator reads");
        toolbar = Ui.Row(Look.Sm, back, sortBox, narrators);
        Refresh(true);
    }

    public override Control Toolbar => toolbar;
    public override string Subtitle => $"{Document.Project.Characters.Count} characters";

    public void ShowCharacter(Guid id)
    {
        Workspace.SelectedCharacter = id;
        Refresh(false);
    }

    bool pendingRefresh;

    public override void Refresh(bool textChanged)
    {
        // Don't pull a text field out from under the narrator while they type in it.
        if (Workspace.SelectedCharacter is not null && TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox tb && tb.IsVisible &&
            Avalonia.VisualTree.VisualExtensions.IsVisualAncestorOf(this, tb))
        {
            if (!pendingRefresh)
            {
                pendingRefresh = true;
                tb.LostFocus += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => { pendingRefresh = false; Refresh(false); });
            }
            return;
        }
        var p = Document.Project;
        if (Workspace.SelectedCharacter is { } id && p.Character(id) is { } c)
        {
            back.IsVisible = true;
            sortBox.IsVisible = false;
            Content = Detail(c);
            return;
        }
        Workspace.SelectedCharacter = null;
        back.IsVisible = false;
        sortBox.IsVisible = true;
        if (p.Characters.Count == 0)
        {
            Content = Ui.Empty("No Characters Yet", "Tag Speakers finds the cast as it reads the book. You can also assign a line to a new character.");
            return;
        }
        var index = Document.Index;
        IEnumerable<BookCharacter> ordered = sort switch
        {
            Sort.Name => p.Characters.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            Sort.Appearance => p.Characters.OrderBy(x => index.StatsFor(x.Id)?.FirstLine?.Global ?? int.MaxValue),
            _ => p.Characters.OrderByDescending(x => index.StatsFor(x.Id)?.Lines ?? 0),
        };
        var shown = Filtered(ordered).ToList();
        var column = new StackPanel();
        column.Children.Add(StyleBar());
        if (Duplicates.Pairs(p) is { Count: > 0 } dups) column.Children.Add(DuplicatesNote(dups));
        if (p.Settings.CastingStyle == CastingStyle.Shared)
        {
            column.Children.Add(Parts(shown));
        }
        else
        {
            var wrap = new WrapPanel { Margin = new Thickness(Look.Xl), ItemSpacing = Look.Lg, LineSpacing = Look.Lg };
            foreach (var ch in shown) wrap.Children.Add(Card(ch));
            column.Children.Add(wrap);
        }
        Content = Ui.Scroll(column);
    }

    Control Card(BookCharacter c)
    {
        var p = Document.Project;
        var stats = Document.Index.StatsFor(c.Id);
        var subtitle = CardSubtitle(c);
        var cue = ProjectActions.VoiceCue(c);
        var header = Ui.Row(Look.Md, Ui.Avatar(c.ColorHex, c.Initials, 44),
            Ui.Column(2, Ui.Text(c.Name, 15, FontWeight.SemiBold, maxLines: 1), Ui.Caption(subtitle)));
        if (p.Settings.PovCharacterID == c.Id) header.Children.Add(Ui.Pill("Narrator", Look.Gray));
        var body = Ui.Column(Look.Md,
            header,
            Ui.Text(cue.Length == 0 ? "No summary yet" : cue, 12, brush: Look.Brush(cue.Length == 0 ? Look.TertiaryInk : Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 3),
            Ui.Row(Look.Lg,
                Ui.Caption($"{stats?.Lines ?? 0:N0} lines"),
                Ui.Caption($"{stats?.Words ?? 0:N0} words"),
                Ui.Caption(stats?.FirstLine is { } f ? $"First: Ch {f.Chapter + 1}" : "No lines")));
        var card = new Border
        {
            Width = 300,
            Padding = new Thickness(Look.Lg),
            CornerRadius = new CornerRadius(10),
            Background = Look.Brush(Look.Chrome),
            BorderBrush = Look.Brush(Look.Hex(c.ColorHex).WithAlpha(0.45)),
            BorderThickness = new Thickness(1),
            Child = body,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        ToolTip.SetTip(card, $"Open {c.Name}’s page");
        card.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(card).Properties.IsLeftButtonPressed) ShowCharacter(c.Id);
        };
        AttachCardMenu(card, c);
        return card;
    }

    Control DuplicatesNote(List<Duplicates.Pair> pairs)
    {
        var p = Document.Project;
        var text = string.Join("; ", pairs.Take(3).Select(d => $"“{p.Character(d.Merge)?.Name}” and “{p.Character(d.Keep)?.Name}”"));
        var row = Ui.Row(Look.Md, Ui.Text($"These may be the same person: {text}.", 13, wrap: TextWrapping.Wrap));
        AddMergeButtons(row, pairs);
        return new Border
        {
            Margin = new Thickness(Look.Xl, Look.Xl, Look.Xl, 0),
            Padding = new Thickness(Look.Md),
            CornerRadius = new CornerRadius(8),
            Background = Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.2 : 0.08)),
            Child = row,
        };
    }

    // MARK: Character page

    Control Detail(BookCharacter c)
    {
        var p = Document.Project;
        var stats = Document.Index.StatsFor(c.Id);
        var col = new StackPanel { Spacing = Look.Lg, Margin = new Thickness(Look.Section, Look.Xl), MaxWidth = 860, HorizontalAlignment = HorizontalAlignment.Left };
        col.Children.Add(Ui.Row(Look.Lg, Ui.Avatar(c.ColorHex, c.Initials, 56),
            Ui.Column(4, Ui.Title(c.Name),
                Ui.Caption(c.Aliases.Count > 0 ? "Also: " + string.Join(", ", c.Aliases) : "No other names"),
                Ui.Caption($"{stats?.Lines ?? 0:N0} lines · {stats?.Words ?? 0:N0} words" +
                           (stats?.FirstLine is { } f ? $" · first speaks {Document.Index.Reference(p.Chapters[f.Chapter].Paragraphs[f.Paragraph].Id)}" : "")))));
        AddDetailActions(col, c);
        AddShadeEditor(col, c);

        col.Children.Add(Ui.Headline("Summary"));
        if (c.Facts.Count == 0)
            col.Children.Add(Ui.Caption("No summary yet. Build Summaries reads the book for what it says about each character.", TextWrapping.Wrap));
        var first = stats?.FirstLine;
        foreach (var kind in FactKinds.NarratorOrder)
        {
            var facts = c.Facts.Where(x => x.Kind == kind).ToList();
            for (int i = 0; i < facts.Count; i++) col.Children.Add(FactRow(c, facts[i], i == 0, first));
        }
        AddFactEditor(col, c);

        col.Children.Add(Ui.Headline("Voice Notes"));
        col.Children.Add(NotesField(c, voice: true));
        col.Children.Add(Ui.Headline("Notes"));
        col.Children.Add(NotesField(c, voice: false));
        return Ui.Scroll(col);
    }

    Control FactRow(BookCharacter c, Fact fact, bool showKind, BookIndex.Position? firstLine)
    {
        var index = Document.Index;
        BookIndex.Position? source = fact.SourceParagraphID is { } sp && index.Positions.TryGetValue(sp, out var pos) ? pos : null;
        bool late = source is { } s && firstLine is { } fl && s.Chapter > fl.Chapter;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("120,*"), ColumnSpacing = Look.Md };
        var kind = Ui.Text(showKind ? fact.Kind.Title() : "", 13, FontWeight.Medium, Look.Brush(Look.SecondaryInk));
        grid.Children.Add(kind);
        var right = new StackPanel { Spacing = Look.Xs };
        right.Children.Add(FactText(c, fact));
        var links = Ui.Row(Look.Sm);
        if (fact.FromAI && !fact.Edited) links.Children.Add(Ui.Caption("✦ Found by the AI"));
        if (fact.SourceParagraphID is { } id && source is not null)
        {
            var link = new Button { Content = index.Reference(id), Classes = { "link" }, Padding = new Thickness(0) };
            ToolTip.SetTip(link, fact.Quote.Length == 0 ? "Show the source in the manuscript" : $"“{fact.Quote}”");
            link.Click += (_, _) => Workspace.Show(id);
            links.Children.Add(link);
        }
        if (late && source is { } so && firstLine is { } f2)
        {
            var badge = Ui.Pill("Revealed Late", Look.Retake);
            ToolTip.SetTip(badge, $"First speaks Ch {f2.Chapter + 1}, revealed Ch {so.Chapter + 1}");
            links.Children.Add(badge);
        }
        if (links.Children.Count > 0) right.Children.Add(links);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        return grid;
    }
}
