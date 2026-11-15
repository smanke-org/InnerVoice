using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Casting Style › First-Person Narrator (the Mac's Views/FirstPersonOptions.swift):
/// Narrator + Two Voices plus the "I" character, who can sound the same as the narrator, a
/// variant of them, or entirely different. The narrator chooses who that is, what they read,
/// and who tells each chapter. Shown on the Cast page (Narration Tools) and the Voices page.
public static class FirstPersonOptions
{
    /// `narration`: Narration Tools, where people read, so the voice choice is a cue.
    public static Control Build(ProjectDocument d, bool narration)
    {
        var p = d.Project;
        var fp = p.Settings.FirstPerson ?? new FirstPersonSettings();
        var characters = Characters(d);
        var col = new StackPanel { Spacing = Look.Sm };

        // The "I" character.
        var who = new ComboBox { MinWidth = 200, ItemsSource = new[] { "Not Chosen" }.Concat(characters.Select(c => c.Name)).ToList() };
        who.SelectedIndex = p.Settings.PovCharacterID is { } pov ? characters.FindIndex(c => c.Id == pov) + 1 : 0;
        who.SelectionChanged += (_, _) =>
        {
            if (who.SelectedIndex < 0) return;
            d.SetPointOfView(who.SelectedIndex == 0 ? null : characters[who.SelectedIndex - 1].Id);
        };
        ToolTip.SetTip(who, "The character who tells the story in the first person");

        // Their voice.
        var modes = Enum.GetValues<FirstPersonVoice>();
        var voice = new ComboBox { MinWidth = 180, ItemsSource = modes.Select(m => m.Title()).ToList(), SelectedIndex = Array.IndexOf(modes, fp.VoiceMode) };
        voice.SelectionChanged += (_, _) => { if (voice.SelectedIndex >= 0 && modes[voice.SelectedIndex] != fp.VoiceMode) d.SetFirstPersonVoice(modes[voice.SelectedIndex]); };
        ToolTip.SetTip(voice, narration
            ? "Read them in your narrator voice, a shaded version of it, or a different voice"
            : "The narrator’s voice, a variant of it (shaded below), or a voice of their own chosen on the Voices page");
        col.Children.Add(Ui.Row(Look.Md, Labeled("“I” Character", who), Labeled("Their Voice", voice)));

        // Variant of Narrator: how far it moves.
        if (fp.VoiceMode == FirstPersonVoice.Variant)
        {
            var s = fp.VariantShade;
            var pitch = Ui.Row(Look.Xs,
                Ui.Button("−", () => d.SetFirstPersonShade(Math.Max(Shade.MinPitch, s.Pitch - 1), s.Pace)),
                Ui.Button("+", () => d.SetFirstPersonShade(Math.Min(Shade.MaxPitch, s.Pitch + 1), s.Pace)),
                Ui.Text($"Pitch {s.Pitch}"));
            var pace = Ui.Row(Look.Xs,
                Ui.Button("−", () => d.SetFirstPersonShade(s.Pitch, Math.Max(Shade.MinPace, s.Pace - 1))),
                Ui.Button("+", () => d.SetFirstPersonShade(s.Pitch, Math.Min(Shade.MaxPace, s.Pace + 1))),
                Ui.Text($"Pace {s.Pace}"));
            var shade = Ui.Row(Look.Lg, pitch, pace);
            ToolTip.SetTip(shade, "How far their voice moves from the narrator’s: pitch in semitones, pace slower or quicker");
            col.Children.Add(shade);
        }

        var readsNarration = new CheckBox { Content = "Their voice reads the narration", IsChecked = fp.ReadsNarration == true };
        readsNarration.IsCheckedChanged += (_, _) => d.SetFirstPersonReadsNarration(readsNarration.IsChecked == true);
        ToolTip.SetTip(readsNarration, "On: the whole book is in their voice, as they tell it. Off: the narrator reads the narration and they speak only their own lines.");
        var thoughts = new CheckBox { Content = "Their voice reads their thoughts", IsChecked = fp.VoicesThoughts ?? true };
        thoughts.IsCheckedChanged += (_, _) => d.SetFirstPersonVoicesThoughts(thoughts.IsChecked == true);
        ToolTip.SetTip(thoughts, "Off: the narrator reads their thoughts");
        col.Children.Add(Ui.Row(Look.Lg, readsNarration, thoughts));

        int count = fp.ChapterNarrators?.Count ?? 0;
        var chapters = new Button { Content = count == 0 ? "Narrator by Chapter…" : $"Narrator by Chapter ({count})…" };
        chapters.Flyout = new Flyout { Content = ChapterList(d, characters), Placement = PlacementMode.BottomEdgeAlignedLeft };
        ToolTip.SetTip(chapters, "For books with more than one first-person narrator: choose who tells each chapter");
        col.Children.Add(chapters);

        col.Children.Add(Ui.Caption(narration
            ? "Men and women are read by their narrators, as with Three Narrators. The “I” character is read as chosen here."
            : "Men and women share a voice each, as with Narrator + Two Voices. The “I” character sounds as chosen here.", TextWrapping.Wrap));
        return col;
    }

    /// Everyone, most lines first (as on the Mac).
    static List<BookCharacter> Characters(ProjectDocument d) =>
        d.Project.Characters.OrderByDescending(c => d.Index.StatsFor(c.Id)?.Lines ?? 0).ToList();

    /// Who tells each chapter.
    internal static Control ChapterList(ProjectDocument d, List<BookCharacter> characters)
    {
        var p = d.Project;
        var map = p.Settings.FirstPerson?.ChapterNarrators;
        var rows = new StackPanel { Spacing = Look.Xs };
        var names = new[] { "Book’s “I” Character" }.Concat(characters.Select(c => c.Name)).ToList();
        foreach (var ch in p.Chapters)
        {
            var chapterID = ch.Id;
            var box = new ComboBox { Width = 180, ItemsSource = names };
            box.SelectedIndex = map is not null && map.TryGetValue(chapterID, out var teller) ? characters.FindIndex(c => c.Id == teller) + 1 : 0;
            box.SelectionChanged += (_, _) =>
            {
                if (box.SelectedIndex < 0) return;
                d.SetChapterNarrator(chapterID, box.SelectedIndex == 0 ? null : characters[box.SelectedIndex - 1].Id);
            };
            var row = new DockPanel();
            DockPanel.SetDock(box, Dock.Right);
            row.Children.Add(box);
            row.Children.Add(new TextBlock
            {
                Text = ch.Title.Length == 0 ? "Opening" : ch.Title, VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, Look.Sm, 0),
            });
            rows.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = rows, MaxHeight = 340 };
        return new StackPanel
        {
            Width = 360, Spacing = Look.Sm,
            Children =
            {
                Ui.Headline("Who Tells Each Chapter"),
                Ui.Caption("Chapters set to the book’s “I” character follow the choice above.", TextWrapping.Wrap),
                scroll,
            },
        };
    }

    static Control Labeled(string label, Control c) => Ui.Column(Look.Xs, Ui.Caption(label), c);
}
