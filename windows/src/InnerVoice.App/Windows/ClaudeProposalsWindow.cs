using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.AI;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.AI;

namespace InnerVoice.App.Windows;

/// What Claude proposed in Clean Up Text, Write Chapter Recaps, Suggest Delivery Hints or
/// Suggest Spellings, to keep or leave out one by one (recaps, hints and spellings can be
/// edited first). Nothing changes until Apply, which is one Undo step. Spellings start
/// unchecked: the narrator confirms each one. The Mac's ClaudeProposalsSheet.
public sealed class ClaudeProposalsWindow : Window
{
    readonly AIRunner runner;
    readonly AIRunner.ClaudeProposals proposals;
    /// Ids left out (or, for spellings, the ones kept).
    readonly HashSet<Guid> marked = [];
    readonly Dictionary<Guid, string> edits = [];
    readonly List<CheckBox> boxes = [];
    readonly Button applyButton = new() { IsDefault = true };
    readonly Button toggleAll = new();

    public static Task Show(Window owner, ProjectDocument document, AIRunner runner, AIRunner.ClaudeProposals proposals) =>
        new ClaudeProposalsWindow(document, runner, proposals).ShowDialog(owner);

    bool ConfirmEach => proposals.Job == AIRunner.ClaudeJob.Spellings;

    List<Guid> Ids => proposals.Job switch
    {
        AIRunner.ClaudeJob.Cleanup => proposals.Fixes.Select(f => f.Id).ToList(),
        AIRunner.ClaudeJob.Recaps => proposals.Recaps.Select(r => r.ChapterID).ToList(),
        AIRunner.ClaudeJob.Delivery => proposals.Hints.Select(h => h.LineID).ToList(),
        _ => proposals.Spellings.Select(s => s.PronunciationID).ToList(),
    };

    bool Kept(Guid id) => ConfirmEach ? marked.Contains(id) : !marked.Contains(id);
    int KeptCount => Ids.Count(Kept);

    public ClaudeProposalsWindow(ProjectDocument document, AIRunner runner, AIRunner.ClaudeProposals proposals)
    {
        this.runner = runner;
        this.proposals = proposals;
        Title = proposals.Job switch
        {
            AIRunner.ClaudeJob.Cleanup => "Review Text Fixes",
            AIRunner.ClaudeJob.Recaps => "Review Chapter Recaps",
            AIRunner.ClaudeJob.Delivery => "Review Delivery Hints",
            _ => "Suggested Spellings",
        };
        Width = 720;
        Height = 580;
        MinWidth = 520;
        MinHeight = 360;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var p = document.Project;
        var index = document.Index;
        var list = new StackPanel { Spacing = Look.Md };
        switch (proposals.Job)
        {
            case AIRunner.ClaudeJob.Cleanup:
                foreach (var fix in proposals.Fixes)
                {
                    var top = Ui.Row(Look.Sm, Ui.Caption(index.Reference(fix.ParagraphID)), Ui.Text(fix.Issue.Title(), 12, FontWeight.SemiBold));
                    if (fix.Note.Length > 0) top.Children.Add(Ui.Caption(fix.Note));
                    var (before, after) = TextCleanup.Preview(fix, p.Paragraph(fix.ParagraphID, index)?.Text ?? "");
                    list.Children.Add(Row(fix.Id, Ui.Column(2, top, Struck(before, 3), Ui.Text(after.Length == 0 ? "(removed)" : after, 13, wrap: TextWrapping.Wrap, maxLines: 3))));
                }
                break;
            case AIRunner.ClaudeJob.Recaps:
                foreach (var r in proposals.Recaps)
                {
                    var col = Ui.Column(4, Ui.Text(p.Chapters.Find(c => c.Id == r.ChapterID)?.Title ?? "Chapter", 12, FontWeight.SemiBold));
                    if (r.Before is { } old) col.Children.Add(Struck(old, 2));
                    col.Children.Add(Editor(r.ChapterID, r.Text, multiline: true));
                    list.Children.Add(Row(r.ChapterID, col));
                }
                break;
            case AIRunner.ClaudeJob.Delivery:
                foreach (var h in proposals.Hints)
                {
                    var line = index.LineIndexByID.TryGetValue(h.LineID, out var li) ? p.Lines[li] : null;
                    var top = Ui.Row(Look.Sm);
                    if (line is not null) top.Children.Add(Ui.Caption(index.Reference(line.ParagraphID)));
                    top.Children.Add(Ui.Text(p.Character(line?.SpeakerID)?.Name ?? "", 12, FontWeight.SemiBold));
                    if (line is not null && Tones.ToneOf(line) is { } t && t != Tone.Neutral) top.Children.Add(Ui.Caption(t.Title()));
                    var col = Ui.Column(4, top);
                    if (line is not null) col.Children.Add(Ui.Text(p.TextOf(line, index), 13, wrap: TextWrapping.Wrap, maxLines: 2));
                    var hint = Ui.Row(Look.Sm, Editor(h.LineID, h.Hint, multiline: false));
                    if (h.Before is { } before) hint.Children.Add(Ui.Caption($"was “{before}”"));
                    col.Children.Add(hint);
                    list.Children.Add(Row(h.LineID, col));
                }
                break;
            default:
                foreach (var s in proposals.Spellings)
                {
                    var top = Ui.Row(Look.Sm, Ui.Text(s.Term, 14, FontWeight.SemiBold), Editor(s.PronunciationID, s.SayItAs, multiline: false), Ui.Caption($"{s.Confidence}% sure"));
                    var col = Ui.Column(4, top);
                    if (s.Note.Length > 0) col.Children.Add(Ui.Caption(s.Note, TextWrapping.Wrap));
                    list.Children.Add(Row(s.PronunciationID, col));
                }
                break;
        }

        toggleAll.Click += (_, _) =>
        {
            bool checkAll = KeptCount != Ids.Count;
            foreach (var b in boxes) b.IsChecked = checkAll;
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        applyButton.Classes.Add("accent");
        applyButton.Click += (_, _) => Apply();
        var right = Ui.Row(Look.Sm, cancel, applyButton);
        var buttons = new DockPanel { Margin = new Thickness(0, Look.Md, 0, 0) };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new Border { Child = toggleAll, HorizontalAlignment = HorizontalAlignment.Left });

        int n = proposals.Count;
        var explanation = proposals.Job switch
        {
            AIRunner.ClaudeJob.Cleanup => $"{proposals.Model} found {n:N0} import " + (n == 1 ? "problem" : "problems") + ". Uncheck any fix you don't want. Only the project changes; your manuscript file never does, and lines you assigned keep their speakers.",
            AIRunner.ClaudeJob.Recaps => $"{proposals.Model} wrote {n:N0} “Previously…” " + (n == 1 ? "recap" : "recaps") + ". Edit or uncheck any you don't want. Recaps you wrote yourself were kept.",
            AIRunner.ClaudeJob.Delivery => $"{proposals.Model} suggested how {n:N0} " + (n == 1 ? "line is" : "lines are") + " performed, beyond their tone. Hints go to ElevenLabs only, as audio tags. Edit or uncheck any you don't want; your own hints were kept.",
            _ => $"{proposals.Model} suggested {n:N0} Say It As " + (n == 1 ? "spelling" : "spellings") + ". Check each one you want to use, after hearing or checking it. Confirmed (checked with the author) stays yours to tick.",
        };
        var head = Ui.Column(Look.Md, Ui.Row(Look.Sm, Ui.Title(Title), SettingsWindow.CloudBadge()),
            Ui.Text(explanation, 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap));
        head.Margin = new Thickness(0, 0, 0, Look.Md);
        var root = new DockPanel { Margin = new Thickness(Look.Xl) };
        DockPanel.SetDock(head, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(head);
        root.Children.Add(buttons);
        root.Children.Add(new Border
        {
            BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(Look.Md), Child = Ui.Scroll(list),
        });
        Content = root;
        Refresh();
    }

    static TextBlock Struck(string text, int lines)
    {
        var t = Ui.Text(text, 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: lines);
        t.TextDecorations = TextDecorations.Strikethrough;
        return t;
    }

    TextBox Editor(Guid id, string text, bool multiline)
    {
        var box = new TextBox { Text = text, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
        if (multiline) box.MinHeight = 56; else box.MinWidth = 220;
        box.TextChanged += (_, _) => edits[id] = box.Text ?? "";
        return box;
    }

    Control Row(Guid id, Control content)
    {
        var box = new CheckBox { IsChecked = Kept(id), VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(box, ConfirmEach ? "Use this spelling" : "Keep this one");
        box.IsCheckedChanged += (_, _) =>
        {
            if ((box.IsChecked == true) == ConfirmEach) marked.Add(id); else marked.Remove(id);
            Refresh();
        };
        boxes.Add(box);
        var row = new DockPanel();
        DockPanel.SetDock(box, Dock.Left);
        row.Children.Add(box);
        row.Children.Add(content);
        return row;
    }

    void Refresh()
    {
        int k = KeptCount;
        toggleAll.Content = k == Ids.Count ? "Uncheck All" : "Check All";
        applyButton.Content = proposals.Job switch
        {
            AIRunner.ClaudeJob.Cleanup => k == 1 ? "Apply 1 Fix" : $"Apply {k:N0} Fixes",
            AIRunner.ClaudeJob.Recaps => k == 1 ? "Use 1 Recap" : $"Use {k:N0} Recaps",
            AIRunner.ClaudeJob.Delivery => k == 1 ? "Use 1 Hint" : $"Use {k:N0} Hints",
            _ => k == 1 ? "Use 1 Spelling" : $"Use {k:N0} Spellings",
        };
        applyButton.IsEnabled = k > 0;
    }

    string Edited(Guid id, string original) => edits.TryGetValue(id, out var t) && !string.IsNullOrWhiteSpace(t) ? t.Trim() : original;

    void Apply()
    {
        var kept = proposals with
        {
            Fixes = proposals.Fixes.Where(f => Kept(f.Id)).ToList(),
            Recaps = proposals.Recaps.Where(r => Kept(r.ChapterID)).Select(r => r with { Text = Edited(r.ChapterID, r.Text) }).ToList(),
            Hints = proposals.Hints.Where(h => Kept(h.LineID))
                .Select(h => DeliveryPrompt.Clean(Edited(h.LineID, h.Hint)) is { } clean ? h with { Hint = clean } : null)
                .OfType<DeliveryProposal>().ToList(),
            Spellings = proposals.Spellings.Where(s => Kept(s.PronunciationID)).Select(s => s with { SayItAs = Edited(s.PronunciationID, s.SayItAs) }).ToList(),
        };
        runner.Apply(kept);
        Close();
    }
}
