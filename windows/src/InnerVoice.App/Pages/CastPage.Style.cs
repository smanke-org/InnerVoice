using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

/// Casting Style on the Cast page (docs/CASTING-STYLE.md), as on the Mac: the style and its
/// one follow-up, the cast in parts for Three Narrators, who reads each card, and the shade cue.
public sealed partial class CastPage
{
    /// Show only characters whose gender the book doesn't settle.
    bool genderFilter;

    Control StyleBar()
    {
        var s = Document.Project.Settings;
        var col = new StackPanel { Spacing = Look.Sm };
        CastingStyle?[] styles = [null, CastingStyle.OneVoice, CastingStyle.Shared, CastingStyle.FullCast];
        var styleBox = Combo(styles.Where(x => x is not null || s.CastingStyle is null).Select(x => x?.Title() ?? "Not Chosen").ToArray(),
            Array.IndexOf(styles.Where(x => x is not null || s.CastingStyle is null).ToArray(), s.CastingStyle),
            i => Document.SetCastingStyle(styles.Where(x => x is not null || s.CastingStyle is null).ElementAt(i)), 200);
        ToolTip.SetTip(styleBox, "How the parts are shared: you read everyone, three narrators (narration, men, women), or a narrator per character. Changing it keeps every assignment.");
        col.Children.Add(Labeled("Casting Style", styleBox));
        switch (s.CastingStyle)
        {
            case CastingStyle.OneVoice:
                CharacterGender?[] voices = [null, CharacterGender.Male, CharacterGender.Female, CharacterGender.Other];
                var voice = Combo(voices.Select(g => g?.Title() ?? "Not Set").ToArray(), Array.IndexOf(voices, s.NarratorGender),
                    i => Document.SetNarratorGender(voices[i]), 160);
                ToolTip.SetTip(voice, "Your own voice: characters of the same gender are read in your voice, others shaded lower or higher");
                var strengths = Enum.GetValues<ShadeStrength>();
                var shading = Combo(strengths.Select(x => x.Title()).ToArray(), Array.IndexOf(strengths, s.ShadeStrength ?? ShadeStrength.Clear),
                    i => Document.SetShadeStrength(strengths[i]), 120);
                col.Children.Add(Ui.Row(Look.Md, Labeled("Your voice", voice), Labeled("Shading", shading)));
                col.Children.Add(Ui.Caption("You play every character, shading your voice a little lower for men and higher for women. The cues show in the margin and the Teleprompter.", TextWrapping.Wrap));
                break;
            case CastingStyle.Shared:
                VoiceRole[] parts = [VoiceRole.Narrator, VoiceRole.Men, VoiceRole.Women];
                var narration = Combo(["A Narrator of Its Own", "The Men’s Narrator", "The Women’s Narrator"],
                    Array.IndexOf(parts, s.NarrationRole ?? VoiceRole.Narrator), i => Document.SetNarrationRole(parts[i]), 220);
                ToolTip.SetTip(narration, "Who reads the narration. Choose the men’s or women’s narrator for a book read by two people.");
                var row = Ui.Row(Look.Md, Labeled("Narration", narration));
                if (parts.Any(r => Document.Project.Narrators.All(n => n.Role != r)))
                    row.Children.Add(Ui.Button("Set Up Narrators", Document.SetUpThreeNarrators, tip: "Add a narrator for the narration, the men and the women. Rename them in Narrators…"));
                col.Children.Add(row);
                break;
            case CastingStyle.FullCast:
                var minors = Enum.GetValues<MinorVoices>();
                var minor = Combo(["The Narrator", "Men’s, Women’s or Other Narrator", "A Narrator of Their Own"],
                    Array.IndexOf(minors, s.MinorVoices ?? MinorVoices.Narrator), i => Document.SetMinorVoices(minors[i]), 260);
                ToolTip.SetTip(minor, "Who reads characters you haven’t marked as main. Right-click a character to mark them main.");
                col.Children.Add(Labeled("Minor characters", minor));
                if (!CastingStyleRules.AnyMainMarked(Document.Project) && CastingStyleRules.SuggestedMain(Document.Project, Document.Index) is { Count: > 0 } suggested)
                {
                    var names = string.Join(", ", suggested.Select(id => Document.Project.Character(id)?.FirstName));
                    col.Children.Add(Ui.Row(Look.Md, Ui.Caption($"Main characters always have a narrator of their own. {names} have the most lines."),
                        Ui.Button(suggested.Count == 1 ? "Mark as Main" : $"Mark These {suggested.Count} as Main", () => Document.SetMain(suggested, true))));
                }
                break;
            default:
                col.Children.Add(Ui.Caption("Choose how the parts are shared. Until you do, cast characters to narrators one by one.", TextWrapping.Wrap));
                break;
        }
        int review = CastingStyleRules.GenderToReview(Document.Project, Document.Index).Count;
        if (review > 0 || genderFilter)
        {
            var filter = new ToggleButton { Content = review == 1 ? "1 Gender to Review" : $"{review} Genders to Review", IsChecked = genderFilter };
            ToolTip.SetTip(filter, "Show only characters whose gender the book doesn’t settle. Right-click one to choose Male, Female or Other.");
            filter.IsCheckedChanged += (_, _) => { genderFilter = filter.IsChecked == true; Refresh(false); };
            col.Children.Add(filter);
        }
        return new Border
        {
            Margin = new Thickness(Look.Xl, Look.Xl, Look.Xl, 0),
            Padding = new Thickness(Look.Md),
            CornerRadius = new CornerRadius(10),
            Background = Look.Brush(Look.Chrome),
            MaxWidth = 640,
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = col,
        };
    }

    static ComboBox Combo(string[] items, int selected, Action<int> chosen, double width)
    {
        var box = new ComboBox { ItemsSource = items, SelectedIndex = Math.Max(0, selected), Width = width };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0 && box.SelectedIndex != selected) chosen(box.SelectedIndex); };
        return box;
    }

    /// The cards shown: everyone (or whom the search finds), or with the filter, only genders to review.
    IEnumerable<BookCharacter> Filtered(IEnumerable<BookCharacter> ordered)
    {
        var q = findBox.Text?.Trim() ?? "";
        if (q.Length > 0)
            ordered = ordered.Where(c => c.SearchMatches(q, c.Voices?.Values.Select(v => InnerVoice.Core.Speech.VoiceNames.Name(v.Choice)) ?? [])).ToList();
        if (!genderFilter) return ordered;
        var review = CastingStyleRules.GenderToReview(Document.Project, Document.Index).ToHashSet();
        return ordered.Where(c => review.Contains(c.Id));
    }

    /// Three Narrators: the cast in parts, each headed by the narrator who reads it.
    Control Parts(List<BookCharacter> shown)
    {
        var p = Document.Project;
        var readers = Document.Readers();
        var narrationReader = CastingStyleRules.Reader(VoiceRole.Narrator, p.Settings);
        var col = new StackPanel { Spacing = Look.Lg, Margin = new Thickness(Look.Xl) };
        foreach (var r in new[] { VoiceRole.Narrator, VoiceRole.Men, VoiceRole.Women, VoiceRole.Other, VoiceRole.Own })
        {
            var members = shown.Where(c => (readers.TryGetValue(c.Id, out var x) ? x : VoiceRole.Own) == r).ToList();
            if (members.Count == 0 && (r is VoiceRole.Own or VoiceRole.Other || genderFilter)) continue;
            var who = p.Narrators.FirstOrDefault(n => n.Role == r) ?? (r == VoiceRole.Other ? CastingStyleRules.NarrationNarrator(p) : null);
            var title = r switch { VoiceRole.Narrator => "Read by the Narrator", VoiceRole.Own => "A Narrator of Their Own", _ => r.Title() };
            var lines = members.Sum(c => Document.Index.StatsFor(c.Id)?.Lines ?? 0);
            var head = Ui.Row(Look.Sm, Ui.Text(title, 17, FontWeight.SemiBold));
            if (r != VoiceRole.Own)
                head.Children.Add(Ui.Text(who is null ? "· no narrator yet" : $"· {who.Name}" + (r == narrationReader && r != VoiceRole.Narrator ? ", who also reads the narration" : ""),
                    13, brush: Look.Brush(who is null ? Look.Attention : Look.SecondaryInk)));
            head.Children.Add(Ui.Caption($"{lines:N0} lines"));
            col.Children.Add(head);
            var wrap = new WrapPanel { ItemSpacing = Look.Lg, LineSpacing = Look.Lg };
            foreach (var c in members) wrap.Children.Add(Card(c));
            col.Children.Add(wrap);
        }
        return col;
    }

    /// The card's line under the name: other names, or how to shade them (Just Me), or who reads them.
    string CardSubtitle(BookCharacter c)
    {
        if (c.Aliases.Count > 0) return string.Join(", ", c.Aliases);
        if (Document.Project.Settings.CastingStyle is not null && Document.Shades().TryGetValue(c.Id, out var shade) && shade.Cue is { } cue)
            return (shade.Arrow is { } a ? a + " " : "") + cue;
        return Document.EffectiveNarrator(c) is { } n ? $"Read by {n.Name}" : "";
    }

    /// Right-click: who reads them, main character, gender.
    void AddStyleMenu(ContextMenu menu, BookCharacter c)
    {
        var s = Document.Project.Settings;
        if (s.CastingStyle is { } style)
        {
            var readBy = new MenuItem { Header = "Read By" };
            readBy.Items.Add(Item((c.Role is null ? "✓ " : "") + "Automatic", () => Document.SetRole(c.Id, null)));
            var roles = style == CastingStyle.OneVoice ? [VoiceRole.Narrator, VoiceRole.Own] : Enum.GetValues<VoiceRole>();
            foreach (var r in roles) readBy.Items.Add(Item((c.Role == r ? "✓ " : "") + r.NarratorTitle(), () => Document.SetRole(c.Id, r)));
            menu.Items.Add(readBy);
            if (style == CastingStyle.FullCast)
                menu.Items.Add(Item((c.IsMain == true ? "✓ " : "") + "Main Character", () => Document.SetMain([c.Id], c.IsMain == true ? false : true)));
        }
        var gender = new MenuItem { Header = "Gender" };
        gender.Items.Add(Item((c.Gender is null ? "✓ " : "") + "From the Book", () => Document.SetGender([c.Id], null)));
        foreach (var g in Enum.GetValues<CharacterGender>()) gender.Items.Add(Item((c.Gender == g ? "✓ " : "") + g.Title(), () => Document.SetGender([c.Id], g)));
        menu.Items.Add(gender);
    }

    /// The narrator's shade for a character they play: automatic, or their own pitch, pace and words.
    void AddShadeEditor(StackPanel col, BookCharacter c)
    {
        if (Document.Project.Settings.CastingStyle is null || !Document.Readers().TryGetValue(c.Id, out var r) || r != VoiceRole.Narrator) return;
        var shade = c.Shade ?? (Document.Shades().TryGetValue(c.Id, out var auto) ? auto : Shade.Make());
        var automatic = new CheckBox { Content = "Automatic", IsChecked = c.Shade is null };
        ToolTip.SetTip(automatic, $"Work out {c.FirstName}’s shade from their gender, age and voice brief");
        automatic.IsCheckedChanged += (_, _) => Document.SetShade(c.Id, automatic.IsChecked == true ? null : shade);
        var pitch = new NumericUpDown { Minimum = Shade.MinPitch, Maximum = Shade.MaxPitch, Value = shade.Pitch, Increment = 1, FormatString = "0", Width = 120 };
        ToolTip.SetTip(pitch, "Semitones lower (−) or higher (+) than your own voice");
        pitch.ValueChanged += (_, e) => { if (e.NewValue is { } v && (int)v != shade.Pitch) Document.SetShade(c.Id, Shade.Make((int)v, shade.Pace, shade.Words)); };
        var pace = new NumericUpDown { Minimum = Shade.MinPace, Maximum = Shade.MaxPace, Value = shade.Pace, Increment = 1, FormatString = "0", Width = 120 };
        ToolTip.SetTip(pace, "Slower (−) or quicker (+) than your own pace");
        pace.ValueChanged += (_, e) => { if (e.NewValue is { } v && (int)v != shade.Pace) Document.SetShade(c.Id, Shade.Make(shade.Pitch, (int)v, shade.Words)); };
        var words = new DraftTextBox(shade.Words ?? "", v => Document.SetShade(c.Id, Shade.Make(shade.Pitch, shade.Pace, v)),
            watermark: "A few words: gravelly, clipped, a soft lilt") { Width = 320 };
        col.Children.Add(Ui.Row(Look.Md, Ui.Headline("Shade"), Ui.Caption(shade.Cue ?? "your own voice"), automatic));
        col.Children.Add(Ui.Row(Look.Md, Labeled("Pitch", pitch), Labeled("Pace", pace), Labeled("Words", words)));
    }

    /// "Automatic (Alex)" for the narrator box when the style decides.
    string AutomaticNarratorLabel(BookCharacter c)
    {
        if (Document.Project.Settings.CastingStyle is null) return "No narrator";
        var copy = c.Copy();
        copy.NarratorID = null;
        return CastingStyleRules.NarratorFor(copy, Document.Project, copy.Gender ?? ShadeProfile.Read(c, Document.Project, Document.Index).Gender?.ToCharacterGender()) is { } n
            ? $"Automatic ({n.Name})" : "Automatic";
    }
}
