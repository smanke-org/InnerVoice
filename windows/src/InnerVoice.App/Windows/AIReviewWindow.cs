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

/// After Tag Speakers with Claude: every line it gave a new speaker, checked to keep.
/// Unchecked lines go back to how they were, in one Undo step. Keep All changes nothing.
/// The Mac's AIReviewSheet (speakers only: Windows has no tones).
public sealed class AIReviewWindow : Window
{
    readonly ProjectDocument document;
    readonly AIRunner.PendingReview review;
    readonly HashSet<Guid> undone = [];
    readonly Button undoButton = new() { IsDefault = true };
    readonly Button toggleAll = new();
    readonly List<CheckBox> boxes = [];

    public static Task Show(Window owner, ProjectDocument document, AIRunner.PendingReview review) =>
        new AIReviewWindow(document, review).ShowDialog(owner);

    public AIReviewWindow(ProjectDocument document, AIRunner.PendingReview review)
    {
        this.document = document;
        this.review = review;
        Title = "Review Claude's Changes";
        Width = 680;
        Height = 560;
        MinWidth = 520;
        MinHeight = 360;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var p = document.Project;
        var index = document.Index;
        var lines = p.Lines.ToDictionary(l => l.Id);
        var list = new StackPanel { Spacing = Look.Sm };
        foreach (var item in review.Items)
        {
            lines.TryGetValue(item.LineID, out var line);
            var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Tag = item.LineID };
            ToolTip.SetTip(box, "Keep this change");
            box.IsCheckedChanged += (_, _) =>
            {
                if (box.IsChecked == true) undone.Remove(item.LineID); else undone.Add(item.LineID);
                Refresh();
            };
            boxes.Add(box);
            var top = Ui.Row(Look.Sm);
            if (line is not null) top.Children.Add(Ui.Caption(index.Reference(line.ParagraphID)));
            top.Children.Add(Change(p.Character(item.SpeakerBefore)?.Name ?? "Who's Speaking?", p.Character(item.SpeakerAfter)?.Name ?? "Who's Speaking?"));
            var col = Ui.Column(2, top);
            if (line is not null) col.Children.Add(Ui.Text(p.TextOf(line, index), 13, wrap: TextWrapping.Wrap, maxLines: 3));
            var row = new DockPanel { Margin = new Thickness(0, 2) };
            DockPanel.SetDock(box, Dock.Left);
            row.Children.Add(box);
            row.Children.Add(col);
            list.Children.Add(row);
        }

        toggleAll.Click += (_, _) =>
        {
            bool checkAll = undone.Count == review.Items.Count;
            foreach (var b in boxes) b.IsChecked = checkAll;
        };
        var keepAll = new Button { Content = "Keep All", IsCancel = true };
        keepAll.Click += (_, _) => Close();
        undoButton.Classes.Add("accent");
        undoButton.Click += (_, _) => Apply();
        var right = Ui.Row(Look.Sm, keepAll, undoButton);
        var buttons = new DockPanel();
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(new Border { Child = toggleAll, HorizontalAlignment = HorizontalAlignment.Left });

        var header = Ui.Row(Look.Sm, Ui.Title("Review Claude's Changes"), SettingsWindow.CloudBadge());
        var intro = Ui.Text($"{review.Model} changed {review.Items.Count:N0} lines. Uncheck any you don't want, and they go back to how they were. Lines you assigned yourself were not touched.",
                            13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap);
        var top2 = Ui.Column(Look.Md, header, intro);
        var root = new DockPanel { Margin = new Thickness(Look.Xl) };
        DockPanel.SetDock(top2, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Thickness(0, Look.Md, 0, 0);
        top2.Margin = new Thickness(0, 0, 0, Look.Md);
        root.Children.Add(top2);
        root.Children.Add(buttons);
        root.Children.Add(new Border
        {
            BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            Padding = new Thickness(Look.Md), Child = Ui.Scroll(list),
        });
        Content = root;
        Refresh();
    }

    static Control Change(string from, string to)
    {
        var text = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
        text.Inlines!.Add(new Run(from) { TextDecorations = TextDecorations.Strikethrough, Foreground = Look.Brush(Look.SecondaryInk) });
        text.Inlines.Add(new Run("  →  ") { Foreground = Look.Brush(Look.SecondaryInk) });
        text.Inlines.Add(new Run(to) { FontWeight = FontWeight.SemiBold, Foreground = Look.Brush(Look.Ink) });
        return text;
    }

    void Refresh()
    {
        toggleAll.Content = undone.Count == review.Items.Count ? "Check All" : "Uncheck All";
        undoButton.Content = undone.Count == 0 ? "Done" : $"Undo {undone.Count:N0} Unchecked";
    }

    void Apply()
    {
        if (undone.Count > 0)
        {
            var ids = undone.ToHashSet();
            document.Change("Undo Claude's Changes", p => AIReview.Undo(ids, review.Before, review.AddedCharacters, p));
        }
        Close();
    }
}
