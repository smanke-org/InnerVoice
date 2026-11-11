using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Step through one character's lines, each with the line before it as a cue.
public sealed class RehearseWindow : Window
{
    readonly ProjectDocument document;
    readonly Guid characterID;
    int position;

    public static void Show(Window? owner, ProjectDocument document, Guid characterID)
    {
        var w = new RehearseWindow(document, characterID);
        if (owner is not null) w.ShowDialog(owner); else w.Show();
    }

    RehearseWindow(ProjectDocument document, Guid characterID)
    {
        this.document = document;
        this.characterID = characterID;
        Title = $"Rehearse {document.Project.Character(characterID)?.Name}";
        Width = 760;
        Height = 520;
        FontFamily = Look.UIFamily;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Build();
    }

    List<int> Lines() =>
        Enumerable.Range(0, document.Project.Lines.Count).Where(i => document.Project.Lines[i].SpeakerID == characterID).ToList();

    void Build()
    {
        var p = document.Project;
        var index = document.Index;
        var lines = Lines();
        var c = p.Character(characterID);
        if (lines.Count == 0 || c is null)
        {
            Content = Ui.Empty("No Lines", "This character has no lines to rehearse.");
            return;
        }
        position = Math.Clamp(position, 0, lines.Count - 1);
        var i = lines[position];
        var line = p.Lines[i];
        var family = Look.ReadingFamily(AppSettings.Current.ReadingSerif);
        var col = new StackPanel { Spacing = Look.Lg, Margin = new Thickness(Look.Section) };
        col.Children.Add(Ui.Row(Look.Sm, Ui.Avatar(c.ColorHex, c.Initials, 30), Ui.Headline(c.Name),
            Ui.Caption($"Line {position + 1} of {lines.Count} · {index.Reference(line.ParagraphID)}")));
        var cue = ProjectActions.VoiceCue(c);
        if (cue.Length > 0) col.Children.Add(Ui.Caption(cue, TextWrapping.Wrap));
        if (i > 0)
        {
            var prev = p.Lines[i - 1];
            var who = prev.SpeakerID == characterID ? "You" : p.Character(prev.SpeakerID)?.Name ?? "?";
            col.Children.Add(Ui.Column(Look.Xs, Ui.Text($"Cue · {who}", 12, FontWeight.SemiBold, Look.Brush(Look.SecondaryInk)),
                new TextBlock { Text = "“" + p.TextOf(prev, index) + "”", FontFamily = family, FontSize = 16, Foreground = Look.Brush(Look.SecondaryInk), TextWrapping = TextWrapping.Wrap }));
        }
        col.Children.Add(new Border
        {
            Padding = new Thickness(Look.Lg),
            CornerRadius = new CornerRadius(10),
            Background = Look.Brush(Look.Hex(c.ColorHex).WithAlpha(Look.IsDark ? 0.24 : 0.14)),
            Child = new SelectableTextBlock { Text = "“" + p.TextOf(line, index) + "”", FontFamily = family, FontSize = 24, TextWrapping = TextWrapping.Wrap, Foreground = Look.Brush(Look.Ink) },
        });
        if (line.Kind != LineKind.Spoken) col.Children.Add(Ui.Caption(line.Kind.Title()));
        var buttons = Ui.Row(Look.Sm,
            Ui.Button("‹ Previous", () => Step(-1), tip: "Previous line (←)"),
            Ui.Button("Next ›", () => Step(1), primary: true, tip: "Next line (Space or →)"),
            Ui.Button("Close", Close));
        var dock = new DockPanel();
        var bottom = new Border { Child = buttons, Padding = new Thickness(Look.Lg, Look.Md), BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 1, 0, 0) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(Ui.Scroll(col));
        Content = dock;
    }

    void Step(int delta)
    {
        position += delta;
        Build();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Right or Key.Down) { Step(1); e.Handled = true; }
        else if (e.Key is Key.Left or Key.Up) { Step(-1); e.Handled = true; }
        else if (e.Key == Key.Escape) { Close(); e.Handled = true; }
        else base.OnKeyDown(e);
    }
}
