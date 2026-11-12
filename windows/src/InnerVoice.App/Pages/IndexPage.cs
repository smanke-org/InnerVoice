using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Every shown character's lines, by chapter, each a link into the manuscript. Main
/// characters (over 15% of the lines) start hidden, so the index stays short.
public sealed class IndexPage : Page
{
    public override PageKind Kind => PageKind.Index;
    readonly TextBox filter = new() { PlaceholderText = "Filter characters" };
    readonly ComboBox narratorBox = new() { Width = 180 };
    readonly HashSet<string> collapsed = [];
    readonly ContentControl left = new(), right = new();
    Guid? narrator;
    bool updating;

    public IndexPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        filter.TextChanged += (_, _) => Refresh(false);
        narratorBox.SelectionChanged += (_, _) =>
        {
            if (updating) return;
            narrator = narratorBox.SelectedIndex > 0 ? Document.Project.Narrators[narratorBox.SelectedIndex - 1].Id : null;
            Refresh(false);
        };
        ToolTip.SetTip(narratorBox, "Show only the characters one narrator reads");
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("270,*") };
        var leftBorder = new Border { Child = left, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 0, 1, 0) };
        grid.Children.Add(leftBorder);
        Grid.SetColumn(right, 1);
        grid.Children.Add(right);
        var leftDock = new DockPanel();
        var filterBox = new Border { Padding = new Thickness(Look.Md), Child = filter };
        DockPanel.SetDock(filterBox, Dock.Top);
        leftDock.Children.Add(filterBox);
        left.Content = leftDock;
        Content = grid;
        Refresh(true);
    }

    public override Control? Toolbar => Document.Project.Narrators.Count > 0 ? narratorBox : null;
    public override string Subtitle => $"{Cast().Count(c => Document.Index.ShowsInIndex(c))} of {Document.Project.Characters.Count} characters shown";

    List<BookCharacter> Cast()
    {
        var q = filter.Text ?? "";
        return Document.Project.Characters
            .Where(c => narrator is null || Document.EffectiveNarrator(c)?.Id == narrator)
            .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase))
            .OrderBy(c => Document.Index.StatsFor(c.Id)?.Lines ?? 0).ToList();
    }

    public override void Refresh(bool textChanged)
    {
        updating = true;
        narratorBox.ItemsSource = new[] { "All Narrators" }.Concat(Document.Project.Narrators.Select(n => n.Name)).ToList();
        narratorBox.SelectedIndex = narrator is { } id ? Document.Project.Narrators.FindIndex(n => n.Id == id) + 1 : 0;
        updating = false;
        var index = Document.Index;
        var cast = Cast();

        var leftDock = (DockPanel)left.Content!;
        while (leftDock.Children.Count > 1) leftDock.Children.RemoveAt(1);
        var buttons = Ui.Row(Look.Sm, Ui.Button("Show All", () => SetAll(cast, true)), Ui.Button("Hide All", () => SetAll(cast, false)));
        buttons.Margin = new Thickness(Look.Md, Look.Sm);
        DockPanel.SetDock(buttons, Dock.Bottom);
        leftDock.Children.Add(buttons);
        var list = new StackPanel { Spacing = 2, Margin = new Thickness(Look.Md, 0) };
        list.Children.Add(Ui.Caption("Show in Index"));
        foreach (var c in cast)
        {
            var box = new CheckBox { IsChecked = index.ShowsInIndex(c) };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = Look.Sm };
            row.Children.Add(Ui.Dot(c.ColorHex));
            var name = Ui.Text(c.Name, 13, maxLines: 1);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            var count = Ui.Caption($"{index.StatsFor(c.Id)?.Lines ?? 0}");
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            box.Content = row;
            box.HorizontalAlignment = HorizontalAlignment.Stretch;
            var cid = c.Id;
            box.IsCheckedChanged += (_, _) =>
            {
                bool on = box.IsChecked == true;
                if (on != Document.Index.ShowsInIndex(Document.Project.Character(cid)!)) Document.UpdateCharacter(cid, "Show in Index", x => x.ShowInIndex = on);
            };
            list.Children.Add(box);
        }
        list.Children.Add(Ui.Caption("Main characters (over 15% of the lines) start hidden, so the index stays short.", TextWrapping.Wrap));
        leftDock.Children.Add(Ui.Scroll(list));

        var shown = cast.Where(index.ShowsInIndex).Reverse().ToList();
        if (shown.Count == 0)
        {
            right.Content = Ui.Empty("No Characters Shown", "Turn on characters at the left to list their lines.");
            return;
        }
        var p = Document.Project;
        var column = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Xl) };
        foreach (var c in shown)
        {
            column.Children.Add(Ui.Row(Look.Sm, Ui.Avatar(c.ColorHex, c.Initials, 24), Ui.Headline(c.Name),
                Ui.Caption($"{index.StatsFor(c.Id)?.Lines ?? 0} lines")));
            var lines = p.Lines.Where(l => l.SpeakerID == c.Id).ToList();
            foreach (var group in lines.GroupBy(l => index.Positions.TryGetValue(l.ParagraphID, out var pos) ? pos.Chapter : 0).OrderBy(g => g.Key))
            {
                var key = $"{c.Id}|{group.Key}";
                var expander = new Expander
                {
                    Header = Ui.Row(Look.Sm, Ui.Text(p.Chapters[group.Key].ShownTitle(), 13), Ui.Caption($"{group.Count()}")),
                    IsExpanded = !collapsed.Contains(key),
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                };
                expander.Expanded += (_, _) => collapsed.Remove(key);
                expander.Collapsed += (_, _) => collapsed.Add(key);
                var rows = new StackPanel { Spacing = 2 };
                foreach (var line in group)
                {
                    var pid = line.ParagraphID;
                    var b = new Button
                    {
                        Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                        HorizontalContentAlignment = HorizontalAlignment.Left,
                        Content = Ui.Row(Look.Md, Ui.Caption(index.Reference(pid)).Also(t => t.Width = 72),
                            Ui.Text("“" + p.TextOf(line, index) + "”", 13, wrap: TextWrapping.Wrap, maxLines: 2)),
                    };
                    ToolTip.SetTip(b, "Show in the manuscript");
                    b.Click += (_, _) => Workspace.Show(pid);
                    rows.Children.Add(b);
                }
                expander.Content = rows;
                column.Children.Add(expander);
            }
        }
        right.Content = Ui.Scroll(column);
    }

    void SetAll(List<BookCharacter> cast, bool on)
    {
        var ids = cast.Select(c => c.Id).ToHashSet();
        Document.Change(on ? "Show All in Index" : "Hide All in Index", p => { foreach (var c in p.Characters.Where(c => ids.Contains(c.Id))) c.ShowInIndex = on; });
    }
}
