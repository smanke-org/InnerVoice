using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// What a revised manuscript changed, to look through one by one.
public sealed class RevisionsPage : Page
{
    public override PageKind Kind => PageKind.Revisions;

    public RevisionsPage(ProjectDocument document, Workspace workspace) : base(document, workspace) => Refresh(true);

    public override string Subtitle => Document.Project.Revisions.LastOrDefault() is { } r
        ? $"{r.Changed.Count + r.Added.Count - r.Reviewed.Count(id => r.Changed.Contains(id) || r.Added.Contains(id))} to look at" : "";

    public override void Refresh(bool textChanged)
    {
        var p = Document.Project;
        if (p.Revisions.LastOrDefault() is not { } record)
        {
            Content = Ui.Empty("No Revisions", "Choose File › Import Revised Manuscript… when the author sends a new draft.");
            return;
        }
        var col = new StackPanel { Spacing = Look.Md, Margin = new Thickness(Look.Xl), MaxWidth = 900, HorizontalAlignment = HorizontalAlignment.Left };
        col.Children.Add(Ui.Caption($"“{record.FileName}”, {record.Date.ToLocal():MMM d, yyyy h:mm tt}"));
        col.Children.Add(Ui.Row(Look.Xxl, Stat(record.Unchanged, "unchanged", Look.SecondaryInk), Stat(record.Changed.Count, "changed", Look.Retake),
            Stat(record.Added.Count, "added", Look.Recorded), Stat(record.Removed.Count, "removed", Look.Attention)));
        col.Children.Add(Ui.Text("Tags and notes were kept on unchanged paragraphs. Changed paragraphs keep the speakers of lines whose words still match.", 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap));
        if (p.UntaggedCount > 0) col.Children.Add(Ui.Button("Tag New Lines", Workspace.RequestTag, primary: true));
        Section(col, "Changed", record.Changed, record);
        Section(col, "Added", record.Added, record);
        if (record.Removed.Count > 0)
        {
            col.Children.Add(Ui.Headline("Removed"));
            foreach (var r in record.Removed)
                col.Children.Add(Ui.Column(2, Ui.Caption(r.ChapterTitle),
                    new TextBlock { Text = r.Text, TextDecorations = TextDecorations.Strikethrough, Foreground = Look.Brush(Look.SecondaryInk), TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.WordEllipsis }));
        }
        Content = Ui.Scroll(col);
    }

    static Control Stat(int n, string label, Color tint) =>
        Ui.Column(0, Ui.Text($"{n:N0}", 22, FontWeight.SemiBold, Look.Brush(tint)), Ui.Caption(label));

    void Section(StackPanel col, string title, List<Guid> ids, RevisionRecord record)
    {
        var shown = ids.Where(id => Document.Index.Positions.ContainsKey(id)).ToList();
        if (shown.Count == 0) return;
        col.Children.Add(Ui.Headline(title));
        foreach (var id in shown)
        {
            bool reviewed = record.Reviewed.Contains(id);
            var b = new Button
            {
                Background = Brushes.Transparent, BorderThickness = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Content = Ui.Row(Look.Md,
                    Ui.Text(reviewed ? "✓" : "○", 14, brush: Look.Brush(reviewed ? Look.Recorded : Look.SecondaryInk)),
                    Ui.Caption(Document.Index.Reference(id)).Also(t => t.Width = 72),
                    Ui.Text(Document.Project.Paragraph(id, Document.Index)?.Text ?? "", 13, wrap: TextWrapping.Wrap, maxLines: 2).Also(t => t.MaxWidth = 720)),
            };
            ToolTip.SetTip(b, "Show it in the manuscript");
            b.Click += (_, _) =>
            {
                if (!reviewed) Document.Change("Review Change", p => p.Revisions[^1].Reviewed.Add(id));
                Workspace.Show(id);
            };
            col.Children.Add(b);
        }
    }
}
