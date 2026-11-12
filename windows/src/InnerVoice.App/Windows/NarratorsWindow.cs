using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// The people reading the book, and which characters each one reads.
public sealed class NarratorsWindow : Window
{
    readonly ProjectDocument document;

    public static void Show(Window owner, ProjectDocument document) => new NarratorsWindow(document).ShowDialog(owner);

    NarratorsWindow(ProjectDocument document)
    {
        this.document = document;
        Title = "Narrators";
        Width = 720;
        Height = 560;
        FontFamily = Look.UIFamily;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        document.Changed += OnChanged;
        Closed += (_, _) => document.Changed -= OnChanged;
        Build();
    }

    void OnChanged(bool _)
    {
        if (FocusManager?.GetFocusedElement() is TextBox) return;
        Build();
    }

    void Build()
    {
        var p = document.Project;
        var col = new StackPanel { Spacing = Look.Lg, Margin = new Thickness(Look.Xl) };
        col.Children.Add(Ui.Text("Cast each character to the narrator who reads them. Narrator Scripts (File › Export) list one narrator's lines with their cues.", 13, wrap: TextWrapping.Wrap));
        foreach (var n in p.Narrators)
        {
            var id = n.Id;
            var name = new DraftTextBox(n.Name, v => document.Change("Rename Narrator", x => { if (x.Narrators.Find(y => y.Id == id) is { } y) y.Name = v.Trim(); })) { Width = 220, FontWeight = FontWeight.SemiBold };
            var notes = new DraftTextBox(n.Notes, v => document.Change("Edit Narrator Notes", x => { if (x.Narrators.Find(y => y.Id == id) is { } y) y.Notes = v; }), watermark: "Notes") { Width = 300 };
            var remove = Ui.Button("Remove…", async () =>
            {
                if (await Dialogs.Confirm(this, $"Remove {n.Name}?", "Their characters won't have a narrator. You can undo this.", "Remove"))
                    document.Change("Remove Narrator", x =>
                    {
                        x.Narrators.RemoveAll(y => y.Id == id);
                        foreach (var c in x.Characters.Where(c => c.NarratorID == id)) c.NarratorID = null;
                    });
            });
            var cast = new WrapPanel { ItemSpacing = Look.Md, LineSpacing = Look.Xs };
            foreach (var c in p.Characters.OrderByDescending(c => document.Index.StatsFor(c.Id)?.Lines ?? 0))
            {
                var cid = c.Id;
                var box = new CheckBox { Content = Ui.Row(Look.Xs, Ui.Dot(c.ColorHex), Ui.Text(c.Name, 12)), IsChecked = c.NarratorID == id };
                if (c.NarratorID is { } other && other != id) box.Opacity = 0.6;
                box.IsCheckedChanged += (_, _) =>
                {
                    bool on = box.IsChecked == true;
                    if (on == (document.Project.Character(cid)?.NarratorID == id)) return;
                    document.UpdateCharacter(cid, "Cast Narrator", x => x.NarratorID = on ? id : null);
                };
                cast.Children.Add(box);
            }
            var row = Ui.Row(Look.Sm, name, notes, remove);
            // Casting Style: the part this narrator reads (one narrator per part).
            if (p.Settings.CastingStyle is not null)
            {
                VoiceRole?[] parts = [null, VoiceRole.Narrator, VoiceRole.Men, VoiceRole.Women, VoiceRole.Other];
                var part = new ComboBox
                {
                    ItemsSource = parts.Select(r => r is null ? "No Part" : r == VoiceRole.Narrator ? "Narration" : r.Value.Title()).ToList(),
                    SelectedIndex = Math.Max(0, Array.IndexOf(parts, n.Role)), Width = 130,
                };
                ToolTip.SetTip(part, $"The part {n.Name} reads: the narration, every man, every woman, or everyone else");
                part.SelectionChanged += (_, _) => { if (part.SelectedIndex >= 0 && parts[part.SelectedIndex] != n.Role) document.SetNarratorRole(id, parts[part.SelectedIndex]); };
                row.Children.Insert(2, part);
            }
            col.Children.Add(Ui.Card(Ui.Column(Look.Md, row, cast), Look.Md));
        }
        col.Children.Add(Ui.Button("Add Narrator", async () =>
        {
            var name = await Dialogs.Ask(this, "Add Narrator", "", "Add", "", "Name");
            if (string.IsNullOrWhiteSpace(name)) return;
            document.Change("Add Narrator", x => x.Narrators.Add(new Narrator { Name = name.Trim() }));
        }, primary: p.Narrators.Count == 0));
        var dock = new DockPanel();
        var close = Ui.Button("Done", Close, primary: true);
        var bottom = new Border { Child = close, Padding = new Thickness(Look.Lg, Look.Md), HorizontalAlignment = HorizontalAlignment.Right };
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(Ui.Scroll(col));
        Content = dock;
    }
}
