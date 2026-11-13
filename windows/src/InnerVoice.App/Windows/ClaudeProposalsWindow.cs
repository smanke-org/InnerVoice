using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.AI;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// What Claude proposed in Clean Up Text or Write Chapter Recaps, to keep or leave out one
/// by one (recaps can be edited first). Nothing changes until Apply, which is one Undo step.
/// The Mac's ClaudeProposalsSheet.
public sealed class ClaudeProposalsWindow : Window
{
    readonly AIRunner runner;
    readonly AIRunner.ClaudeProposals proposals;
    readonly HashSet<Guid> leftOut = [];
    readonly Dictionary<Guid, string> edits = [];
    readonly List<CheckBox> boxes = [];
    readonly Button applyButton = new() { IsDefault = true };
    readonly Button toggleAll = new();

    public static Task Show(Window owner, ProjectDocument document, AIRunner runner, AIRunner.ClaudeProposals proposals) =>
        new ClaudeProposalsWindow(document, runner, proposals).ShowDialog(owner);

    List<Guid> Ids => proposals.Job == AIRunner.ClaudeJob.Cleanup ? proposals.Fixes.Select(f => f.Id).ToList() : proposals.Recaps.Select(r => r.ChapterID).ToList();
    int KeptCount => Ids.Count(id => !leftOut.Contains(id));

    public ClaudeProposalsWindow(ProjectDocument document, AIRunner runner, AIRunner.ClaudeProposals proposals)
    {
        this.runner = runner;
        this.proposals = proposals;
        bool cleanup = proposals.Job == AIRunner.ClaudeJob.Cleanup;
        Title = cleanup ? "Review Text Fixes" : "Review Chapter Recaps";
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
        if (cleanup)
        {
            foreach (var fix in proposals.Fixes)
            {
                var top = Ui.Row(Look.Sm, Ui.Caption(index.Reference(fix.ParagraphID)), Ui.Text(fix.Issue.Title(), 12, FontWeight.SemiBold));
                if (fix.Note.Length > 0) top.Children.Add(Ui.Caption(fix.Note));
                var text = p.Paragraph(fix.ParagraphID, index)?.Text ?? "";
                var (before, after) = TextCleanup.Preview(fix, text);
                var was = Ui.Text(before, 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 3);
                was.TextDecorations = TextDecorations.Strikethrough;
                list.Children.Add(Row(fix.Id, Ui.Column(2, top, was, Ui.Text(after.Length == 0 ? "(removed)" : after, 13, wrap: TextWrapping.Wrap, maxLines: 3))));
            }
        }
        else
        {
            foreach (var r in proposals.Recaps)
            {
                var col = Ui.Column(4, Ui.Text(p.Chapters.Find(c => c.Id == r.ChapterID)?.Title ?? "Chapter", 12, FontWeight.SemiBold));
                if (r.Before is { } old)
                {
                    var was = Ui.Text(old, 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 2);
                    was.TextDecorations = TextDecorations.Strikethrough;
                    col.Children.Add(was);
                }
                var id = r.ChapterID;
                var box = new TextBox { Text = r.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 56 };
                box.TextChanged += (_, _) => edits[id] = box.Text ?? "";
                col.Children.Add(box);
                list.Children.Add(Row(id, col));
            }
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
        var explanation = cleanup
            ? $"{proposals.Model} found {n:N0} import " + (n == 1 ? "problem" : "problems") + ". Uncheck any fix you don't want. Only the project changes; your manuscript file never does, and lines you assigned keep their speakers."
            : $"{proposals.Model} wrote {n:N0} “Previously…” recaps. Edit or uncheck any you don't want. Recaps you wrote yourself were kept.";
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

    Control Row(Guid id, Control content)
    {
        var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top };
        ToolTip.SetTip(box, "Keep this one");
        box.IsCheckedChanged += (_, _) =>
        {
            if (box.IsChecked == true) leftOut.Remove(id); else leftOut.Add(id);
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
        applyButton.Content = proposals.Job == AIRunner.ClaudeJob.Cleanup
            ? (k == 1 ? "Apply 1 Fix" : $"Apply {k:N0} Fixes")
            : (k == 1 ? "Use 1 Recap" : $"Use {k:N0} Recaps");
        applyButton.IsEnabled = k > 0;
    }

    void Apply()
    {
        var kept = proposals with
        {
            Fixes = proposals.Fixes.Where(f => !leftOut.Contains(f.Id)).ToList(),
            Recaps = proposals.Recaps.Where(r => !leftOut.Contains(r.ChapterID))
                .Select(r => edits.TryGetValue(r.ChapterID, out var t) && !string.IsNullOrWhiteSpace(t) ? r with { Text = t.Trim() } : r).ToList(),
        };
        runner.Apply(kept);
        Close();
    }
}
