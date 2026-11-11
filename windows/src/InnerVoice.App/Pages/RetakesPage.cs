using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Paragraphs marked to read again, in book order.
public sealed class RetakesPage : Page
{
    public override PageKind Kind => PageKind.Retakes;
    readonly CheckBox showDone = new() { Content = "Show Done" };

    public RetakesPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        showDone.IsCheckedChanged += (_, _) => Refresh(false);
        ToolTip.SetTip(showDone, "Show retakes you have finished");
        Refresh(true);
    }

    public override Control Toolbar => showDone;
    public override string Subtitle => $"{Document.Project.Retakes.Count(r => !r.Done):N0} to do";

    public override void Refresh(bool textChanged)
    {
        var p = Document.Project;
        var index = Document.Index;
        var retakes = p.Retakes.Where(r => showDone.IsChecked == true || !r.Done)
            .OrderBy(r => index.Positions.TryGetValue(r.ParagraphID, out var pos) ? pos.Global : int.MaxValue).ToList();
        if (retakes.Count == 0)
        {
            Content = Ui.Empty("No Retakes", "Press R in Record to mark a paragraph to read again.");
            return;
        }
        var list = new StackPanel { Margin = new Thickness(Look.Xl), Spacing = Look.Sm };
        foreach (var r in retakes)
        {
            var done = new CheckBox { IsChecked = r.Done, VerticalAlignment = VerticalAlignment.Top };
            ToolTip.SetTip(done, "Done");
            var id = r.Id;
            done.IsCheckedChanged += (_, _) => { if ((done.IsChecked == true) != r.Done) Document.SetRetakeDone(id, done.IsChecked == true); };
            var text = p.Paragraph(r.ParagraphID, index)?.Text ?? "";
            var info = Ui.Column(Look.Xs,
                Ui.Caption(index.Reference(r.ParagraphID)),
                Ui.Text(r.Note.Length == 0 ? "No note" : r.Note, 13, brush: r.Note.Length == 0 ? Look.Brush(Look.SecondaryInk) : null, wrap: TextWrapping.Wrap),
                Ui.Text(text, 12, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap, maxLines: 2));
            var pid = r.ParagraphID;
            var record = Ui.Button("Record", () => { Workspace.RecordParagraph = pid; Workspace.Go(PageKind.Record); });
            var show = Ui.Button("Show", () => Workspace.Show(pid));
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = Look.Md };
            row.Children.Add(done);
            Grid.SetColumn(info, 1);
            row.Children.Add(info);
            var buttons = Ui.Row(Look.Sm, record, show);
            Grid.SetColumn(buttons, 2);
            row.Children.Add(buttons);
            var card = Ui.Card(row, Look.Md);
            var menu = new ContextMenu();
            var delete = new MenuItem { Header = "Delete Retake" };
            delete.Click += (_, _) => Document.DeleteRetake(id);
            menu.Items.Add(delete);
            card.ContextMenu = menu;
            list.Children.Add(card);
        }
        Content = Ui.Scroll(list);
    }
}
