using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Controls;

/// Pick who says a line: type to filter, arrows and Enter, or 1–9; 0 marks it narration.
/// Pinned speakers first, then the most used in this chapter, then the rest by lines.
/// For a whole paragraph (from a name in the margin) one pick goes to every line in it.
public sealed class AssignPanel : UserControl
{
    readonly ProjectDocument document;
    readonly IReadOnlyList<Guid> lineIDs;
    readonly Action<Guid?, Guid?> onAssigned;
    readonly Action onClose;
    readonly TextBox query = new() { PlaceholderText = "Find or add a character", BorderThickness = new Thickness(0) };
    readonly StackPanel rows = new() { Spacing = 2, Margin = new Thickness(Look.Xs) };
    int highlighted;
    List<BookCharacter> filtered = [];
    bool canCreate, showsNarration;

    Line? Line => document.Index.LineIndexByID.TryGetValue(lineIDs[0], out var i) ? document.Project.Lines[i] : null;

    List<Line> Lines => lineIDs.Where(document.Index.LineIndexByID.ContainsKey).Select(id => document.Project.Lines[document.Index.LineIndexByID[id]]).ToList();

    /// The one speaker every line has now, if they agree.
    Guid? SharedSpeaker => Lines.Select(l => l.SpeakerID).Distinct().Count() == 1 ? Lines[0].SpeakerID : null;

    /// The unsure guess to confirm: the speaker most of the lines have (a tie goes to the one
    /// that speaks first), when any of them is unsure.
    BookCharacter? Guess
    {
        get
        {
            var lines = Lines;
            if (!lines.Any(document.Project.NeedsYou)) return null;
            var counts = new Dictionary<Guid, int>();
            Guid? top = null;
            foreach (var id in lines.Select(l => l.SpeakerID).OfType<Guid>())
            {
                counts[id] = counts.GetValueOrDefault(id) + 1;
                if (top is null || counts[id] > counts[top.Value]) top = id;
            }
            return document.Project.Character(top);
        }
    }

    /// "Cyrus 2, Mara 1, nobody yet 1" for a paragraph whose lines disagree.
    string Breakdown() => string.Join(", ", Lines.GroupBy(l => l.SpeakerID)
        .Select(g => $"{document.Project.Character(g.Key)?.Name ?? "nobody yet"} {g.Count()}"));

    public AssignPanel(ProjectDocument document, IReadOnlyList<Guid> lineIDs, Action<Guid?, Guid?> onAssigned, Action onClose, bool isParagraph = false)
    {
        this.document = document;
        this.lineIDs = lineIDs;
        this.onAssigned = onAssigned;
        this.onClose = onClose;
        Width = 320;
        var header = new StackPanel { Spacing = Look.Xs, Margin = new Thickness(Look.Md) };
        header.Children.Add(Ui.Headline(lineIDs.Count == 1 ? "Assign To" : $"Assign {lineIDs.Count} Lines To"));
        if (isParagraph && lineIDs.Count > 1 && Line is { } first)
        {
            header.Children.Add(Ui.Text("“" + document.Project.TextOf(first, document.Index) + "”", 12, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 2));
            var n = lineIDs.Count;
            header.Children.Add(Ui.Caption(Guess is { } g
                ? $"Best guess: {g.Name}. Enter confirms it for all {n} lines in this paragraph."
                : document.Project.Character(SharedSpeaker) is { } s
                    ? $"All {n} lines in this paragraph are {s.Name}’s."
                    : $"{n} lines in this paragraph: {Breakdown()}. Your pick goes to all of them.", TextWrapping.Wrap));
        }
        else if (Line is { } line)
        {
            header.Children.Add(Ui.Text("“" + document.Project.TextOf(line, document.Index) + "”", 12, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 2));
            header.Children.Add(Guess is { } g
                ? Ui.Caption($"Best guess: {g.Name} ({line.Confidence:P0} sure). Enter confirms it.", TextWrapping.Wrap)
                : Ui.Caption(line.Source == AssignmentSource.None ? "Not assigned yet" : line.Source.Title() + (line.Source == AssignmentSource.Ai ? $" ({line.Confidence:P0} sure)" : "")));
        }
        query.TextChanged += (_, _) => { highlighted = 0; Rebuild(); };
        query.KeyDown += OnKey;
        var clear = new Button { Content = "Who's Speaking?", Classes = { "link" } };
        ToolTip.SetTip(clear, "Clear the speaker; the line shows in red");
        clear.Click += (_, _) => Assign(null);
        var panel = new StackPanel();
        panel.Children.Add(header);
        panel.Children.Add(Ui.Divider());
        panel.Children.Add(new Border { Padding = new Thickness(Look.Sm, Look.Xs), Child = query });
        panel.Children.Add(Ui.Divider());
        panel.Children.Add(new ScrollViewer { Content = rows, MaxHeight = 300 });
        panel.Children.Add(Ui.Divider());
        panel.Children.Add(new Border { Padding = new Thickness(Look.Md, Look.Xs), Child = clear });
        Content = panel;
        Rebuild();
        if (Guess is { } guess && filtered.FindIndex(c => c.Id == guess.Id) is var gi && gi >= 0) { highlighted = gi; Rebuild(); }
        AttachedToVisualTree += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => query.Focus());
    }

    List<BookCharacter> Ordered()
    {
        var p = document.Project;
        var index = document.Index;
        var pinned = p.SpeakerOrder().Pinned;
        var inChapter = new Dictionary<Guid, int>();
        if (Line is { } line && index.Positions.TryGetValue(line.ParagraphID, out var pos))
        {
            var ids = p.Chapters[pos.Chapter].Paragraphs.Select(x => x.Id).ToHashSet();
            foreach (var l in p.Lines)
                if (ids.Contains(l.ParagraphID) && l.SpeakerID is { } s) inChapter[s] = inChapter.GetValueOrDefault(s) + 1;
        }
        var byUse = p.Characters.OrderByDescending(c => inChapter.GetValueOrDefault(c.Id)).ThenByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0);
        return [.. pinned, .. byUse.Where(c => !c.IsPinned)];
    }

    void Rebuild()
    {
        var q = (query.Text ?? "").Trim();
        filtered = Ordered().Where(c => c.Matches(q)).Take(60).ToList();
        showsNarration = q.Length == 0 || "narration".StartsWith(q, StringComparison.OrdinalIgnoreCase) || "not spoken".StartsWith(q, StringComparison.OrdinalIgnoreCase);
        canCreate = q.Length > 0 && !document.Project.Characters.Any(c => string.Equals(c.Name, q, StringComparison.CurrentCultureIgnoreCase));
        rows.Children.Clear();
        if (showsNarration)
            rows.Children.Add(Row(Ui.Row(Look.Sm, Ui.Text("0", 11, brush: Look.Brush(Look.TertiaryInk)), Ui.Text("Narration (not a line)", 13, brush: Look.Brush(Look.SecondaryInk))), highlighted == -1, MarkNarration));
        for (int i = 0; i < filtered.Count; i++)
        {
            var c = filtered[i];
            var number = i < 9 && q.Length == 0 ? $"{i + 1}" : " ";
            var label = Ui.Row(Look.Sm, Ui.Text(number, 11, brush: Look.Brush(Look.TertiaryInk)), Ui.Dot(c.ColorHex), Ui.Text(c.Name + (c.IsPinned ? "  📌" : ""), 13));
            var id = c.Id;
            rows.Children.Add(Row(label, i == highlighted, () => Assign(id)));
        }
        if (canCreate)
            rows.Children.Add(Row(Ui.Text($"＋ New Character “{q}”", 13), highlighted == filtered.Count, AssignNew));
    }

    Control Row(Control content, bool selected, Action click)
    {
        var b = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(Look.Sm, 4),
            Background = selected ? Look.Brush(Look.Accent.WithAlpha(0.18)) : Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(6),
        };
        b.Click += (_, _) => click();
        return b;
    }

    void OnKey(object? sender, KeyEventArgs e)
    {
        int max = filtered.Count - (canCreate ? 0 : 1);
        switch (e.Key)
        {
            case Key.Down: highlighted = Math.Min(highlighted + 1, max); Rebuild(); e.Handled = true; break;
            case Key.Up: highlighted = Math.Max(showsNarration ? -1 : 0, highlighted - 1); Rebuild(); e.Handled = true; break;
            case Key.Escape: onClose(); e.Handled = true; break;
            case Key.Enter: Commit(); e.Handled = true; break;
            default:
                if ((query.Text ?? "").Length == 0 && e.KeyModifiers == KeyModifiers.None && e.Key is >= Key.D0 and <= Key.D9 or >= Key.NumPad0 and <= Key.NumPad9)
                {
                    int n = e.Key >= Key.NumPad0 ? e.Key - Key.NumPad0 : e.Key - Key.D0;
                    if (n == 0) MarkNarration();
                    else if (n <= Math.Min(9, filtered.Count)) Assign(filtered[n - 1].Id);
                    e.Handled = true;
                }
                break;
        }
    }

    void Commit()
    {
        if (highlighted == -1) MarkNarration();
        else if (highlighted < filtered.Count) Assign(filtered[highlighted].Id);
        else if (canCreate) AssignNew();
    }

    void Assign(Guid? speaker)
    {
        var previous = lineIDs.Count == 1 ? Line?.SpeakerID : SharedSpeaker;
        document.Assign(lineIDs, speaker);
        onAssigned(speaker, previous);
    }

    void AssignNew()
    {
        var id = document.AddCharacter((query.Text ?? "").Trim());
        Assign(id);
    }

    void MarkNarration()
    {
        document.RemoveLines(lineIDs);
        onClose();
    }
}
