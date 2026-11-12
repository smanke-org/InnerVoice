using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Assigning lines, the right-click menus, and keyboard commands on the manuscript.
public sealed partial class ManuscriptPage
{
    Flyout? open;

    /// Assign To, at the clicked line; a `paragraph` group (from a name in the margin) gives
    /// one pick to every line in it.
    void ShowLinePopover(Line line, Rect rect, List<Guid>? paragraph = null)
    {
        var ids = paragraph ?? (Canvas.SelectedLines.Count > 1 && Canvas.SelectedLines.Contains(line.Id) ? Canvas.SelectedLines.ToList() : [line.Id]);
        open?.Hide();
        var flyout = new Flyout { Placement = PlacementMode.Bottom, ShowMode = FlyoutShowMode.Standard };
        var panel = new AssignPanel(Document, ids, (speaker, previous) =>
        {
            flyout.Hide();
            if ((ids.Count == 1 || paragraph is not null) && speaker is not null) OfferExchange(ids[^1], previous);
            Canvas.Focus();
        }, () => { flyout.Hide(); Canvas.Focus(); }, isParagraph: paragraph is not null);
        flyout.Content = panel;
        var anchor = AnchorAt(rect);
        flyout.Closed += (_, _) => RemoveAnchor(anchor);
        open = flyout;
        flyout.ShowAt(anchor);
    }

    /// After a line gets a new speaker, the rest of a back-and-forth is usually off by one.
    void OfferExchange(Guid lineID, Guid? previous)
    {
        var proposals = Exchange.Proposals(Document.Project, lineID, previous);
        if (proposals.Count == 0) return;
        var apply = Ui.Button($"Swap {proposals.Count} More", () => { Document.ApplyExchange(proposals); HideNotice(); }, primary: true,
            tip: "Give the following lines of this back-and-forth the alternating speakers");
        var no = Ui.Button("Keep As Is", HideNotice);
        ShowNotice($"The next {(proposals.Count == 1 ? "line alternates" : $"{proposals.Count} lines alternate")} with this one. Swap them to match?", apply, no);
    }

    readonly Border notice = new() { IsVisible = false };

    void ShowNotice(string text, params Control[] buttons)
    {
        var row = new DockPanel { Margin = new Thickness(Look.Lg, Look.Sm) };
        var right = Ui.Row(Look.Sm, buttons);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var label = Ui.Text(text, 13, wrap: TextWrapping.Wrap);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        notice.Child = row;
        notice.Background = Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.22 : 0.10));
        notice.IsVisible = true;
    }

    void HideNotice() => notice.IsVisible = false;

    // MARK: Right-click

    void OnContext(Line? line, Paragraph? paragraph, Point point)
    {
        var menu = new ContextMenu();
        var selection = Canvas.SelectedText();
        if (line is not null && selection.Count == 0)
        {
            if (!Canvas.SelectedLines.Contains(line.Id))
            {
                Canvas.SelectedLines.Clear();
                Canvas.SelectedLines.Add(line.Id);
                Canvas.InvalidateVisual();
            }
            var ids = Canvas.SelectedLines.ToList();
            menu.Items.Add(Item("Assign To…", () => { if (Canvas.RectOf(line) is { } r) ShowLinePopover(line, r); }, "Enter"));
            var speakers = new MenuItem { Header = "Speaker" };
            var (pinned, others) = Document.Project.SpeakerOrder();
            foreach (var c in pinned.Concat(others).Take(40))
            {
                var id = c.Id;
                var it = Item(c.Name, () => Document.Assign(ids, id));
                it.Icon = Ui.Dot(c.ColorHex);
                speakers.Items.Add(it);
            }
            speakers.IsEnabled = speakers.Items.Count > 0;
            menu.Items.Add(speakers);
            var kind = new MenuItem { Header = "Kind" };
            foreach (var k in new[] { LineKind.Spoken, LineKind.Thought, LineKind.Written })
                kind.Items.Add(Item((line.Kind == k ? "✓ " : "") + k.Title(), () => Document.SetKind(k, ids)));
            menu.Items.Add(kind);
            menu.Items.Add(Item(ids.Count == 1 ? "Mark as Narration" : $"Mark {ids.Count} Lines as Narration", () => Document.RemoveLines(ids)));
            menu.Items.Add(Item("Who's Speaking?", () => Document.Assign(ids, null)));
            if (Document.Project.Character(line.SpeakerID) is { } sp)
            {
                menu.Items.Add(new Separator());
                menu.Items.Add(Item($"Open {sp.Name}", () => { Workspace.SelectedCharacter = sp.Id; Workspace.Go(PageKind.Cast); }));
            }
        }
        if (selection.Count > 0)
        {
            menu.Items.Add(Item("Copy", CopySelection, "Ctrl+C"));
            if (selection.Count == 1 && selection[0].Paragraph is { } p1)
            {
                var range = TrimSpan(p1.Text, selection[0].Range);
                if (range.Length > 0)
                    menu.Items.Add(Item("Make Line", () =>
                    {
                        var id = Document.MakeLine(p1.Id, range, null);
                        Canvas.ClearSelection();
                        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                        {
                            if (Document.Index.LineIndexByID.TryGetValue(id, out var li) && Canvas.RectOf(Document.Project.Lines[li]) is { } r)
                                ShowLinePopover(Document.Project.Lines[li], r);
                        });
                    }));
            }
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Delete Text…", () => _ = DeleteSelection(), "Delete"));
        }
        if (menu.Items.Count == 0 && paragraph is not null)
        {
            var pid = paragraph.Id;
            menu.Items.Add(Item("Open Teleprompter Here", () => { Workspace.TeleprompterParagraph = pid; Workspace.Go(PageKind.Teleprompter); }));
        }
        if (menu.Items.Count == 0) return;
        var anchor = AnchorAt(new Rect(point, new Size(1, 1)));
        menu.Closed += (_, _) => RemoveAnchor(anchor);
        menu.Open(anchor);
    }

    void CopySelection()
    {
        var p = Document.Project;
        var text = string.Join("\n", Canvas.SelectedText().Select(s => s.Paragraph?.Substring(s.Range)
            ?? p.Chapters[s.Chapter].Title.Substring(s.Range.Location, Math.Min(s.Range.Length, p.Chapters[s.Chapter].Title.Length - s.Range.Location))));
        _ = TopLevel.GetTopLevel(this)?.Clipboard?.SetTextAsync(text);
    }

    static TextSpan TrimSpan(string text, TextSpan r)
    {
        int loc = r.Location, len = r.Length;
        while (len > 0 && (char.IsWhiteSpace(text[loc]) || "“”\"‘’'«»".Contains(text[loc]))) { loc++; len--; }
        while (len > 0 && (char.IsWhiteSpace(text[loc + len - 1]) || "“”\"«»".Contains(text[loc + len - 1]))) len--;
        return new TextSpan(loc, len);
    }

    static MenuItem Item(string header, Action action, string? gesture = null)
    {
        var m = new MenuItem { Header = header };
        if (gesture is not null) m.InputGesture = KeyGesture.Parse(gesture);
        m.Click += (_, _) => action();
        return m;
    }

    /// Deletes the selected text from the project's copy of the book: paragraph cuts, whole
    /// chapters (heading and all), and heading cuts.
    async Task DeleteSelection()
    {
        var selection = Canvas.SelectedText();
        if (selection.Count == 0) return;
        var p = Document.Project;
        var cuts = new Dictionary<Guid, List<TextSpan>>();
        var headings = new Dictionary<Guid, TextSpan>();
        foreach (var (para, chapter, range) in selection)
        {
            if (para is not null)
            {
                if (!cuts.TryGetValue(para.Id, out var list)) cuts[para.Id] = list = [];
                list.Add(range);
            }
            else headings[p.Chapters[chapter].Id] = range;
        }
        // A chapter selected from its heading to its last word goes whole.
        var whole = new HashSet<Guid>();
        foreach (var ch in p.Chapters)
        {
            if (!headings.TryGetValue(ch.Id, out var h) || h.Length < ch.Title.Length) continue;
            if (ch.Paragraphs.All(x => cuts.TryGetValue(x.Id, out var c) && c.Sum(s => s.Length) >= x.Text.Length)) whole.Add(ch.Id);
        }
        foreach (var id in whole)
        {
            headings.Remove(id);
            foreach (var x in p.Chapters.First(c => c.Id == id).Paragraphs) cuts.Remove(x.Id);
        }
        int words = selection.Sum(s => Words.Count(s.Paragraph?.Substring(s.Range) ?? ""));
        var what = whole.Count > 0 && cuts.Count == 0 && headings.Count == 0 ? (whole.Count == 1 ? "this chapter" : $"these {whole.Count} chapters") : $"the selected text ({words:N0} words)";
        if (!await Dialogs.Confirm(this, "Delete Text?", $"Delete {what} from this project? Lines inside it go too. The manuscript file you imported doesn't change, and you can undo this.", "Delete"))
            return;
        Canvas.ClearSelection();
        Document.DeleteText(cuts, whole, headings);
    }

    // MARK: Keyboard

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Source is TextBox) { base.OnKeyDown(e); return; }
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (e.Key == Key.Enter && Canvas.SelectedLines.Count > 0)
        {
            var first = Document.Project.Lines.FirstOrDefault(l => Canvas.SelectedLines.Contains(l.Id));
            if (first is not null && Canvas.RectOf(first) is { } r) ShowLinePopover(first, r);
            e.Handled = true;
        }
        else if (ctrl && e.Key is Key.Down or Key.Up) { NextNeedingYou(e.Key == Key.Down ? 1 : -1); e.Handled = true; }
        else if (ctrl && e.Key == Key.C && Canvas.HasSelection) { CopySelection(); e.Handled = true; }
        else if (e.Key is Key.Delete or Key.Back && Canvas.HasSelection) { _ = DeleteSelection(); e.Handled = true; }
        else if (e.Key == Key.Escape) { Canvas.ClearSelection(); Canvas.SelectedLines.Clear(); Canvas.InvalidateVisual(); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    /// Next or Previous Line That Needs You (Ctrl+↓ / Ctrl+↑).
    public void NextNeedingYou(int step)
    {
        var p = Document.Project;
        var needing = p.Lines.Where(p.NeedsYou).ToList();
        if (needing.Count == 0) return;
        int current = needing.FindIndex(l => Canvas.SelectedLines.Contains(l.Id));
        if (current < 0)
        {
            int sel = p.Lines.FindIndex(l => Canvas.SelectedLines.Contains(l.Id));
            if (sel < 0) current = step > 0 ? -1 : needing.Count;
            else
            {
                var order = p.Lines.Select((l, i) => (l.Id, i)).ToDictionary(x => x.Id, x => x.i);
                current = needing.FindLastIndex(l => order[l.Id] <= sel);
                if (step < 0) current++;
            }
        }
        int next = ((current + step) % needing.Count + needing.Count) % needing.Count;
        var line = needing[next];
        Canvas.SelectedLines.Clear();
        Canvas.SelectedLines.Add(line.Id);
        Canvas.ScrollToLine(line);
        Canvas.InvalidateVisual();
    }

    /// An invisible control over the clicked words, for a flyout to point at.
    Control AnchorAt(Rect rect)
    {
        var point = Canvas.TranslatePoint(rect.Position, anchorLayer) ?? rect.Position;
        var anchor = new Border { Width = Math.Max(1, rect.Width), Height = Math.Max(1, rect.Height), Background = Brushes.Transparent, IsHitTestVisible = false };
        Avalonia.Controls.Canvas.SetLeft(anchor, point.X);
        Avalonia.Controls.Canvas.SetTop(anchor, point.Y);
        anchorLayer.Children.Add(anchor);
        return anchor;
    }

    readonly Avalonia.Controls.Canvas anchorLayer = new() { IsHitTestVisible = false };

    void RemoveAnchor(Control anchor) => anchorLayer.Children.Remove(anchor);
}
