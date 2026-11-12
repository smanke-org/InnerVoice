using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Editing a character: names, facts, notes, color, pin, merge and delete.
public sealed partial class CastPage
{
    void AttachCardMenu(Control card, BookCharacter c)
    {
        var menu = new ContextMenu();
        menu.Items.Add(Item("Open", () => ShowCharacter(c.Id)));
        menu.Items.Add(Item(c.IsPinned ? "Unpin" : "Pin to the Top of Speaker Lists", () => Document.SetPinned(c.Id, !c.IsPinned)));
        menu.Items.Add(ColorMenu(c));
        menu.Items.Add(MergeMenu(c));
        menu.Items.Add(new Separator());
        AddStyleMenu(menu, c);
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Delete Character…", () => ConfirmDelete(c)));
        card.ContextMenu = menu;
    }

    static MenuItem Item(string header, Action action)
    {
        var m = new MenuItem { Header = header };
        m.Click += (_, _) => action();
        return m;
    }

    MenuItem ColorMenu(BookCharacter c)
    {
        var root = new MenuItem { Header = "Color" };
        root.Items.Add(Item((c.ColorIsAutomatic ? "✓ " : "") + "Automatic", () => Document.SetColor(c.Id, null)));
        root.Items.Add(new Separator());
        foreach (var family in new[] { CharacterPalette.Family.Masculine, CharacterPalette.Family.Feminine, CharacterPalette.Family.Neutral })
        {
            var sub = new MenuItem { Header = family.Title() };
            foreach (var sw in CharacterPalette.SwatchesIn(family))
            {
                var item = Item(sw.Name, () => Document.SetColor(c.Id, sw.Hex));
                item.Icon = Ui.Dot(sw.Hex, 12);
                if (!c.ColorIsAutomatic && string.Equals(sw.Hex, c.ColorHex, StringComparison.OrdinalIgnoreCase)) item.Header = "✓ " + sw.Name;
                sub.Items.Add(item);
            }
            root.Items.Add(sub);
        }
        return root;
    }

    MenuItem MergeMenu(BookCharacter c)
    {
        var root = new MenuItem { Header = $"Merge {c.Name} Into" };
        foreach (var other in Document.Project.Characters.Where(o => o.Id != c.Id).OrderBy(o => o.Name, StringComparer.CurrentCultureIgnoreCase))
            root.Items.Add(Item(other.Name, () => Document.Merge(c.Id, other.Id)));
        root.IsEnabled = root.Items.Count > 0;
        return root;
    }

    void AddMergeButtons(StackPanel row, List<Duplicates.Pair> pairs)
    {
        var first = pairs[0];
        var p = Document.Project;
        if (p.Character(first.Merge) is { } a && p.Character(first.Keep) is { } b)
            row.Children.Add(Ui.Button($"Merge “{a.Name}” into “{b.Name}”", () => Document.Merge(a.Id, b.Id)));
    }

    async void ConfirmDelete(BookCharacter c)
    {
        int lines = Document.Index.StatsFor(c.Id)?.Lines ?? 0;
        if (await Dialogs.Confirm(this, $"Delete {c.Name}?", $"Their {lines:N0} lines become Who's Speaking? lines. You can undo this.", "Delete"))
        {
            Workspace.SelectedCharacter = null;
            Document.DeleteCharacter(c.Id);
        }
    }

    void AddDetailActions(StackPanel col, BookCharacter c)
    {
        var name = new DraftTextBox(c.Name, v => { if (v.Trim().Length > 0) Document.UpdateCharacter(c.Id, "Rename Character", x => x.Name = v.Trim()); }) { Width = 260 };
        var aliases = new DraftTextBox(string.Join(", ", c.Aliases), v => Document.UpdateCharacter(c.Id, "Edit Other Names",
            x => x.Aliases = v.Split(',').Select(a => a.Trim()).Where(a => a.Length > 0).Distinct().ToList()), watermark: "Other names, separated by commas") { Width = 320 };
        var narrators = new ComboBox { Width = 200, PlaceholderText = "No narrator" };
        var list = new List<Narrator?> { null };
        list.AddRange(Document.Project.Narrators);
        narrators.ItemsSource = list.Select(n => n?.Name ?? AutomaticNarratorLabel(c)).ToList();
        narrators.SelectedIndex = Math.Max(0, list.FindIndex(n => n?.Id == c.NarratorID));
        narrators.SelectionChanged += (_, _) =>
        {
            var chosen = narrators.SelectedIndex > 0 ? list[narrators.SelectedIndex]!.Id : (Guid?)null;
            if (chosen != c.NarratorID) Document.UpdateCharacter(c.Id, "Cast Narrator", x => x.NarratorID = chosen);
        };
        var color = new Button { Content = Ui.Row(Look.Xs, Ui.Dot(c.ColorHex, 12), Ui.Text(c.ColorIsAutomatic ? "Automatic" : "Color")) };
        color.Flyout = new MenuFlyout { ItemsSource = new[] { ColorMenu(c) } };
        var pin = new CheckBox { Content = "Pin to the top of speaker lists", IsChecked = c.IsPinned };
        pin.IsCheckedChanged += (_, _) => { if ((pin.IsChecked == true) != c.IsPinned) Document.SetPinned(c.Id, pin.IsChecked == true); };
        var row1 = Ui.Row(Look.Md, Labeled("Name", name), Labeled("Other names", aliases));
        var row2 = Ui.Row(Look.Md, Labeled("Read by", narrators), Labeled("Color", color), pin);
        if (Document.Project.Narrators.Count == 0) row2.Children.RemoveAt(0);
        col.Children.Add(row1);
        col.Children.Add(row2);
    }

    static Control Labeled(string label, Control field) => Ui.Column(Look.Xs, Ui.Caption(label), field);

    Control FactText(BookCharacter c, Fact fact) =>
        new DraftTextBox(fact.Text, v => Document.UpdateCharacter(c.Id, "Edit Fact", x =>
        {
            if (x.Facts.Find(f => f.Id == fact.Id) is { } f)
            {
                f.Text = v;
                f.Edited = true;
            }
        }), multiline: true)
        {
            BorderThickness = new Thickness(0),
            Background = Avalonia.Media.Brushes.Transparent,
            Padding = new Thickness(0),
            MinHeight = 0,
        };

    void AddFactEditor(StackPanel col, BookCharacter c)
    {
        var kinds = new ComboBox { ItemsSource = FactKinds.NarratorOrder.Select(k => k.Title()).ToList(), SelectedIndex = 0, Width = 170 };
        var text = new TextBox { PlaceholderText = "Add a fact", Width = 360 };
        void Add()
        {
            var t = (text.Text ?? "").Trim();
            if (t.Length == 0) return;
            var kind = FactKinds.NarratorOrder[Math.Max(0, kinds.SelectedIndex)];
            Document.UpdateCharacter(c.Id, "Add Fact", x => x.Facts.Add(new Fact { Kind = kind, Text = t, Edited = true }));
        }
        text.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Add(); e.Handled = true; } };
        col.Children.Add(Ui.Row(Look.Sm, kinds, text, Ui.Button("Add", Add)));
    }

    Control NotesField(BookCharacter c, bool voice) =>
        new DraftTextBox(voice ? c.VoiceNotes : c.Notes, v => Document.UpdateCharacter(c.Id, voice ? "Edit Voice Notes" : "Edit Notes", x =>
        {
            if (voice) x.VoiceNotes = v; else x.Notes = v;
        }), multiline: true, watermark: voice ? "How you plan to voice them" : "Anything else to remember")
        {
            MinHeight = 80,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
}
