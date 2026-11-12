using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Several characters picked at once (Ctrl-click, Shift-click): set their gender, who reads
/// them, or main character in one go, one undo step each.
public sealed partial class CastPage
{
    readonly HashSet<Guid> picked = [];
    Guid? pickAnchor;
    List<Guid> shownOrder = [];

    /// The characters picked, in the order shown.
    public IReadOnlyList<Guid> Picked => shownOrder.Where(picked.Contains).ToList();

    /// A click opens the character; Ctrl-click adds or removes them from the picked
    /// characters, Shift-click picks the run from the last one.
    public void CardClicked(Guid id, KeyModifiers mods)
    {
        if (mods.HasFlag(KeyModifiers.Control) || mods.HasFlag(KeyModifiers.Meta))
        {
            if (!picked.Remove(id)) picked.Add(id);
            pickAnchor = id;
        }
        else if (mods.HasFlag(KeyModifiers.Shift))
        {
            int b = shownOrder.IndexOf(id);
            if (pickAnchor is { } anchor && shownOrder.IndexOf(anchor) is var a && a >= 0 && b >= 0)
                foreach (var x in shownOrder.GetRange(Math.Min(a, b), Math.Abs(a - b) + 1)) picked.Add(x);
            else { picked.Add(id); pickAnchor = id; }
        }
        else
        {
            picked.Clear();
            ShowCharacter(id);
            return;
        }
        Refresh(false);
    }

    void ClearPicked()
    {
        picked.Clear();
        Refresh(false);
    }

    /// After a change: settled genders leave the Genders to Review list.
    void AfterPickedChange()
    {
        if (genderFilter) picked.Clear();
        Refresh(false);
    }

    /// "Male 3 · From the book 2": what the picked characters are set to now.
    string GenderSummary(IReadOnlyList<Guid> ids) => string.Join(" · ", ids.Select(i => Document.Project.Character(i)).OfType<BookCharacter>()
        .GroupBy(c => c.Gender).Select(g => $"{g.Key?.Title() ?? "From the book"} {g.Count()}"));

    Control PickedBar(IReadOnlyList<Guid> ids)
    {
        var p = Document.Project;
        var names = string.Join(", ", ids.Select(i => p.Character(i)).OfType<BookCharacter>().Select(c => c.FirstName));
        var left = Ui.Column(Look.Xs,
            Ui.Headline(ids.Count == 1 ? "1 Character Selected" : $"{ids.Count} Characters Selected"),
            Ui.Caption(names, TextWrapping.Wrap),
            Ui.Button("Clear Selection", ClearPicked, tip: "Go back to clicking a card to open it (Esc)"));
        left.Width = 240;
        var genders = Ui.Row(Look.Sm);
        foreach (var g in Enum.GetValues<CharacterGender>())
        {
            var gg = g;
            genders.Children.Add(Ui.Button(g.Title(), () => { Document.SetGender(ids.ToList(), gg); AfterPickedChange(); },
                tip: $"Set all {ids.Count} to {(g == CharacterGender.Other ? "Other (neither male nor female)" : g.Title())}"));
        }
        genders.Children.Add(Ui.Button("From the Book", () => { Document.SetGender(ids.ToList(), null); AfterPickedChange(); },
            tip: "Go back to what the book says about each of them"));
        var right = Ui.Column(Look.Sm, Ui.Headline("Gender"), Ui.Caption("Now: " + GenderSummary(ids)), genders);
        if (p.Settings.CastingStyle is { } style)
        {
            var roles = style == CastingStyle.OneVoice ? new[] { VoiceRole.Narrator, VoiceRole.Own } : Enum.GetValues<VoiceRole>();
            VoiceRole?[] choices = [null, .. roles.Select(r => (VoiceRole?)r)];
            var current = ids.Select(i => p.Character(i)).OfType<BookCharacter>().Select(c => c.Role).Distinct().ToList();
            var box = new ComboBox { ItemsSource = choices.Select(r => r is { } x ? x.NarratorTitle() : "Automatic").ToArray(), Width = 220,
                SelectedIndex = current.Count == 1 ? Array.IndexOf(choices, current[0]) : -1, PlaceholderText = "Mixed" };
            box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) { Document.SetRole(ids.ToList(), choices[box.SelectedIndex]); AfterPickedChange(); } };
            var row = Ui.Row(Look.Sm, Ui.Text("Read By", 13), box);
            if (style == CastingStyle.FullCast)
            {
                row.Children.Add(Ui.Button("Mark as Main", () => { Document.SetMain(ids.ToList(), true); AfterPickedChange(); }));
                row.Children.Add(Ui.Button("Not Main", () => { Document.SetMain(ids.ToList(), false); AfterPickedChange(); }));
            }
            right.Children.Add(row);
        }
        return new Border
        {
            Padding = new Thickness(Look.Lg),
            Background = Look.Brush(Look.Chrome),
            BorderBrush = Look.Brush(Look.Separator),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Child = Ui.Row(Look.Xl, left, right),
        };
    }

    /// Right-click on a picked card: the same choices for every picked character.
    void AttachManyMenu(Control card)
    {
        var ids = Picked.ToList();
        var menu = new ContextMenu();
        var gender = new MenuItem { Header = $"Gender of {ids.Count} Characters" };
        foreach (var g in Enum.GetValues<CharacterGender>())
        {
            var gg = g;
            gender.Items.Add(Item(g.Title(), () => { Document.SetGender(ids, gg); AfterPickedChange(); }));
        }
        gender.Items.Add(new Separator());
        gender.Items.Add(Item("From the Book", () => { Document.SetGender(ids, null); AfterPickedChange(); }));
        menu.Items.Add(gender);
        if (Document.Project.Settings.CastingStyle is { } style)
        {
            var readBy = new MenuItem { Header = "Read By" };
            readBy.Items.Add(Item("Automatic", () => { Document.SetRole(ids, null); AfterPickedChange(); }));
            var roles = style == CastingStyle.OneVoice ? new[] { VoiceRole.Narrator, VoiceRole.Own } : Enum.GetValues<VoiceRole>();
            foreach (var r in roles) { var rr = r; readBy.Items.Add(Item(r.NarratorTitle(), () => { Document.SetRole(ids, rr); AfterPickedChange(); })); }
            menu.Items.Add(readBy);
            if (style == CastingStyle.FullCast)
            {
                menu.Items.Add(Item($"Mark {ids.Count} as Main Characters", () => { Document.SetMain(ids, true); AfterPickedChange(); }));
                menu.Items.Add(Item($"Unmark {ids.Count} as Main Characters", () => { Document.SetMain(ids, false); AfterPickedChange(); }));
            }
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Clear Selection", ClearPicked));
        card.ContextMenu = menu;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && picked.Count > 0) { ClearPicked(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }
}
