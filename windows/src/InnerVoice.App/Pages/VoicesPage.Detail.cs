// The right side of the Voices page: the narrator's, a shared role's or a character's voice.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Pages;

public sealed partial class VoicesPage
{
    /// Opens Clone a Voice (set by the window, so the page needn't know the window type).
    public static Action<VoicesPage, Guid?>? OpenCloneVoice { get; set; }
    /// ElevenLabs' Voice Design for a character, when the engine offers it.
    public static Func<VoicesPage, BookCharacter, Control?>? CloudDesign { get; set; }

    void BuildDetail()
    {
        var p = Document.Project;
        if (Target is VoiceTarget.Character ch && p.Character(ch.Id) is null) Target = VoiceTarget.Narrator;
        if (Target is VoiceTarget.Role r0 && !CastingStyleRules.SharedRolesNeeded(p.Settings).Contains(r0.Value)) Target = VoiceTarget.Narrator;
        var column = new StackPanel { Spacing = Look.Lg, Margin = new Thickness(Look.Xl), MaxWidth = 760, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var n in Notices()) column.Children.Add(n);
        switch (Target)
        {
            case VoiceTarget.Character c:
                CharacterDetail(column, p.Character(c.Id)!);
                break;
            case VoiceTarget.Role r:
                column.Children.Add(Ui.Title(r.Value.VoiceTitle()));
                column.Children.Add(Ui.Caption(r.Value switch
                {
                    VoiceRole.Men => "The voice every male character shares (Narrator + Two Voices).",
                    VoiceRole.Women => "The voice every female character shares.",
                    _ => "The voice for characters who are neither, or whose gender isn’t known.",
                }, TextWrapping.Wrap));
                VoiceSection(column, p.Settings.RoleVoice(r.Value, Engine), null, choice => Document.SetRoleVoice(choice, r.Value), sameAsNarrator: false);
                break;
            default:
                NarratorDetail(column);
                break;
        }
        detail.Content = column;
    }

    IEnumerable<Control> Notices()
    {
        var p = Document.Project;
        var engine = Engine;
        if (p.Settings.Engine != engine)
            yield return Notice($"{p.Settings.Engine.Title()} is turned off in Settings › Voices, so this project uses {engine.Title()}.");
        if (engine != VoiceEngine.Mixed && VoiceEngineRegistry.Get(engine) is { IsReady: false } e)
            yield return Notice(e.NotReadyReason ?? $"{engine.Title()} isn’t downloaded yet, so these voices can’t be heard.");
        bool cloud = engine.IsCloud() || (engine == VoiceEngine.Mixed && p.Settings.CloudVoicesAllowed is not null && EngineSwitches.Shared.IsOn(VoiceEngine.Elevenlabs));
        if (cloud)
        {
            var stop = Ui.Button("Stop Sending This Book", () => Document.SetCloudVoicesAllowed(false),
                tip: "No more of this book’s text goes to ElevenLabs. Its ElevenLabs voices fall back to the narrator’s.");
            yield return Ui.Card(Ui.Column(Look.Sm,
                Ui.Text("This book uses ElevenLabs, in the cloud: every line its voices speak is sent to ElevenLabs as text.", wrap: TextWrapping.Wrap),
                stop), Look.Md);
        }
        else if (engine == VoiceEngine.Mixed && p.Settings.CloudVoicesAllowed is null && EngineSwitches.Shared.IsOn(VoiceEngine.Elevenlabs))
        {
            var include = Ui.Button("Include ElevenLabs…", async () => { if (await AskCloud()) Document.SetCloudVoicesAllowed(true); });
            yield return Ui.Card(Ui.Column(Look.Sm, Ui.Caption("ElevenLabs voices aren’t in the mix: this book hasn’t allowed its text to be sent to ElevenLabs.", TextWrapping.Wrap), include), Look.Md);
        }
    }

    static Control Notice(string text) => Ui.Card(Ui.Text(text, wrap: TextWrapping.Wrap), Look.Md);

    void NarratorDetail(StackPanel column)
    {
        var p = Document.Project;
        column.Children.Add(Ui.Title("Narrator’s Voice"));
        var engine = Engine;
        if (VoiceResolver.PovNarrator(p, engine, EngineSwitches.Shared) is { } pov)
            column.Children.Add(Ui.Caption($"This book is told in the first person by {pov.Name}, so the narration uses {pov.FirstName}’s voice. Choose a voice below to give the narrator a voice of its own.", TextWrapping.Wrap));
        var narration = CastingStyleRules.Reader(VoiceRole.Narrator, p.Settings);
        if (narration != VoiceRole.Narrator)
            column.Children.Add(Ui.Caption($"In this book the {narration.VoiceCaption().ToLowerInvariant()} reads the narration (Casting Style). The voice below is used only if you change that.", TextWrapping.Wrap));
        if (p.Settings.CastingStyle == CastingStyle.Shared) column.Children.Add(NarrationRoleBox());
        VoiceSection(column, p.Settings.NarratorVoice(engine), null, choice => Document.SetVoice(choice, null), sameAsNarrator: false);
    }

    /// Narrator + Two Voices: who reads the narration.
    Control NarrationRoleBox()
    {
        var roles = new[] { VoiceRole.Narrator, VoiceRole.Men, VoiceRole.Women };
        var box = new ComboBox
        {
            ItemsSource = new[] { "Narrator’s Own Voice", "Men’s Voice", "Women’s Voice" },
            SelectedIndex = Math.Max(0, Array.IndexOf(roles, Document.Project.Settings.NarrationRole ?? VoiceRole.Narrator)),
        };
        box.SelectionChanged += (_, _) => { if (box.SelectedIndex >= 0) Document.SetNarrationRole(roles[box.SelectedIndex]); };
        ToolTip.SetTip(box, "Who reads the narration. Choose the men’s or women’s voice for a book read by two people.");
        return Ui.Row(Look.Sm, Ui.Text("Narration"), box);
    }

    void CharacterDetail(StackPanel column, BookCharacter c)
    {
        var p = Document.Project;
        var engine = Engine;
        column.Children.Add(Ui.Row(Look.Sm, Ui.Dot(c.ColorHex, 14), Ui.Title(c.Name)));
        var reader = Readers().GetValueOrDefault(c.Id, VoiceRole.Own);
        var id = c.Id;
        if (reader == VoiceRole.Narrator)
        {
            column.Children.Add(Ui.Headline("Played by the Narrator"));
            column.Children.Add(ShadeEditor(c));
            column.Children.Add(Ui.Button($"Give {c.FirstName} a Voice of Their Own", () => Document.SetRole(id, VoiceRole.Own)));
        }
        else if (reader is VoiceRole.Men or VoiceRole.Women or VoiceRole.Other)
        {
            column.Children.Add(Ui.Caption($"Read in the {reader.VoiceCaption().ToLowerInvariant()}{(c.Role is null ? " (Casting Style)" : "")}.", TextWrapping.Wrap));
            column.Children.Add(Ui.Row(Look.Sm,
                Ui.Button($"Show {reader.VoiceTitle()}", () => Show(new VoiceTarget.Role(reader))),
                Ui.Button($"Give {c.FirstName} a Voice of Their Own", () => Document.SetRole(id, VoiceRole.Own))));
        }
        else
        {
            if (c.Role == VoiceRole.Own && p.Settings.CastingStyle is not null and not CastingStyle.FullCast)
                column.Children.Add(Ui.Row(Look.Sm, Ui.Caption($"{c.FirstName} has a voice of their own."),
                    Ui.Button("Use the Casting Style", () => Document.SetRole(id, null))));
            VoiceSection(column, c.Voice(engine), c, choice => Document.SetVoice(choice, id), sameAsNarrator: true);
        }
        column.Children.Add(ProfileCard(c));
    }

    // MARK: The voice now, best fits and every voice

    void VoiceSection(StackPanel column, CastVoice? current, BookCharacter? c, Action<VoiceChoice> choose, bool sameAsNarrator)
    {
        var engine = Engine;
        var p = Document.Project;
        // The voice now.
        var now = new StackPanel { Spacing = Look.Sm };
        if (current is null)
            now.Children.Add(Ui.Text("No voice yet. Choose one below.", wrap: TextWrapping.Wrap));
        else
        {
            var info = VoiceEngineRegistry.Info(current.Choice);
            now.Children.Add(Ui.Row(Look.Sm, Ui.Headline(VoiceNames.Name(current.Choice)),
                Ui.Pill(current.Choice.IsNarrator ? "Narrator" : current.Choice.Engine.Title() + (current.Choice.Engine.IsCloud() ? " (Cloud)" : ""), Look.Accent)));
            if (info is not null && info.Detail.Length > 0) now.Children.Add(Ui.Caption(Describe(info)));
            if (current.Source == VoiceSource.Auto)
            {
                var sure = $"Chosen by casting, {(int)(current.Confidence * 100)}% sure.";
                if (c is not null && current.Confidence < p.Settings.UnsureBelow)
                    now.Children.Add(Ui.Row(Look.Sm, Ui.Caption(sure + " Listen, then confirm it or choose another."),
                        Ui.Button("Confirm", () => Document.ConfirmVoice(c.Id), primary: true)));
                else now.Children.Add(Ui.Caption(sure));
            }
            var hear = Ui.Button(c is null ? "Hear the Narrator" : $"Hear {c.FirstName}", () => Audition.Play(current.Choice, c, Document));
            now.Children.Add(hear);
            if (current.Choice.Engine == VoiceEngine.Apple && !current.Choice.IsNarrator) now.Children.Add(Adjustments(current.Choice, choose));
        }
        column.Children.Add(Ui.Card(now, Look.Md));

        var voices = EngineSwitches.Shared.VoicesFor(engine, p);
        // Best fits, from what the book says.
        if (c is not null && voices.Count > 0)
        {
            var fits = VoiceCasting.Suggestions(c, p, Document.Index, VoiceInfo.Castable(voices));
            if (fits.Count > 0)
            {
                column.Children.Add(Ui.Headline("Best Fits"));
                var fitList = new StackPanel { Spacing = 2 };
                foreach (var (v, _) in fits) fitList.Children.Add(VoiceRow(v, c, current?.Choice, choose));
                column.Children.Add(fitList);
            }
        }
        // Qwen3-TTS: any description.
        if (engine == VoiceEngine.Qwen) column.Children.Add(DescribeBox(current, c, choose));
        if (c is not null && engine is VoiceEngine.Elevenlabs or VoiceEngine.Mixed && CloudDesign?.Invoke(this, c) is { } design) column.Children.Add(design);

        column.Children.Add(Ui.Headline(engine == VoiceEngine.Mixed ? "All Voices" : $"All {engine.Title()}"));
        if (voices.Count == 0)
            column.Children.Add(Ui.Caption(engine == VoiceEngine.Elevenlabs
                ? "Your ElevenLabs account has no voices. Add some in the Voice Library at elevenlabs.io."
                : "No voices to show.", TextWrapping.Wrap));
        var all = new StackPanel { Spacing = 2 };
        if (sameAsNarrator)
        {
            var same = new VoiceChoice(engine == VoiceEngine.Mixed ? VoiceEngine.Apple : engine, VoiceChoice.NarratorID);
            all.Children.Add(ChoiceRow("Same as Narrator", "Uses the narrator’s voice", current?.Choice.IsNarrator == true, () => choose(same), null));
        }
        // Clones first; then the rest, best first.
        foreach (var v in voices.OrderByDescending(v => v.Cloned).Take(200)) all.Children.Add(VoiceRow(v, c, current?.Choice, choose));
        column.Children.Add(all);
        if (OpenCloneVoice is not null)
            column.Children.Add(Ui.Row(Look.Sm, Ui.Button("Clone a Voice…", () => OpenCloneVoice(this, c?.Id)),
                Ui.Caption("Make a voice from recordings of a real person, with their permission.")));
    }

    static string Describe(VoiceInfo v)
    {
        var parts = new List<string> { v.Gender.Title(), v.Accent };
        if (v.Age != VoiceAge.Adult) parts.Insert(0, v.Age.Title());
        if (v.Detail.Length > 0) parts.Add(v.Detail);
        return string.Join(" · ", parts.Where(x => x.Length > 0));
    }

    Control VoiceRow(VoiceInfo v, BookCharacter? c, VoiceChoice? current, Action<VoiceChoice> choose)
    {
        bool chosen = current is not null && current.Engine == v.Engine && current.VoiceID == v.VoiceID;
        var used = UsedBy(v.VoiceID, c?.Id);
        var caption = Describe(v) + (used.Count > 0 ? " · also " + string.Join(", ", used.Take(3)) : "");
        return ChoiceRow(v.Name + (v.Cloned ? " (Cloned)" : ""), caption, chosen, () => choose(v.Choice), () => Audition.Play(v.Choice, c, Document));
    }

    List<string> UsedBy(string voiceID, Guid? except) => Document.Project.Characters
        .Where(x => x.Id != except && x.Voice(Engine)?.Choice.VoiceID == voiceID).Select(x => x.FirstName).ToList();

    Control ChoiceRow(string name, string caption, bool chosen, Action use, Action? hear)
    {
        var text = Ui.Column(1, Ui.Text((chosen ? "✓ " : "") + name, 13, chosen ? FontWeight.SemiBold : FontWeight.Normal), Ui.Caption(caption));
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Look.Xs, VerticalAlignment = VerticalAlignment.Center };
        if (hear is not null) buttons.Children.Add(Ui.Button("Play", hear, tip: $"Hear {name}"));
        var useButton = Ui.Button("Use", use);
        useButton.IsEnabled = !chosen;
        buttons.Children.Add(useButton);
        var row = new DockPanel { Margin = new Thickness(0, 2) };
        DockPanel.SetDock(buttons, Dock.Right);
        row.Children.Add(buttons);
        row.Children.Add(text);
        return new Border
        {
            Child = row, Padding = new Thickness(Look.Sm, Look.Xs), CornerRadius = new CornerRadius(6),
            Background = chosen ? Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.25 : 0.08)) : Brushes.Transparent,
        };
    }

    /// The system's voices take a speed and pitch of their own.
    Control Adjustments(VoiceChoice choice, Action<VoiceChoice> choose)
    {
        Control Slider(string label, double value, Action<double> set)
        {
            var s = new Slider { Minimum = 0.5, Maximum = 1.5, Value = value, Width = 180, TickFrequency = 0.05, IsSnapToTickEnabled = true };
            var shown = Ui.Caption($"{value:0.00}×");
            DispatcherTimer? wait = null;
            s.PropertyChanged += (_, e) =>
            {
                if (e.Property != RangeBase.ValueProperty) return;
                shown.Text = $"{s.Value:0.00}×";
                wait?.Stop();
                wait = new DispatcherTimer(TimeSpan.FromMilliseconds(450), DispatcherPriority.Normal, (_, _) => { wait?.Stop(); set(Math.Round(s.Value, 2)); });
                wait.Start();
            };
            return Ui.Row(Look.Sm, Ui.Text(label), s, shown);
        }
        return Ui.Column(Look.Xs,
            Slider("Speed", choice.Rate ?? 1, v => { var c = choice.Copy(); c.Rate = Math.Abs(v - 1) < 0.001 ? null : v; choose(c); }),
            Slider("Pitch", choice.Pitch ?? 1, v => { var c = choice.Copy(); c.Pitch = Math.Abs(v - 1) < 0.001 ? null : v; choose(c); }),
            Ui.Caption("Windows Voices change pitch and speed."));
    }

    /// Qwen3-TTS makes a voice from any description.
    Control DescribeBox(CastVoice? current, BookCharacter? c, Action<VoiceChoice> choose)
    {
        var box = new TextBox
        {
            Text = current?.Choice.VoiceID == "design" ? current.Choice.Design ?? "" : "", AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
            MinHeight = 60, PlaceholderText = "A woman in her sixties with a low, warm voice, speaking English with a Scottish accent.",
        };
        var save = Ui.Button("Save Description", () =>
        {
            var text = box.Text?.Trim() ?? "";
            if (text.Length > 0) choose(new VoiceChoice(VoiceEngine.Qwen, "design", text));
        });
        var row = Ui.Row(Look.Sm, save);
        if (c is not null)
            row.Children.Insert(0, Ui.Button("Describe from Summary", () =>
                box.Text = VoiceCasting.Describe(VoiceCasting.Profile(c, Document.Project, Document.Index), c.Name)));
        var note = c is null
            ? "Or describe any voice and Qwen3-TTS makes it. Say who they are and how they sound."
            : $"Or describe any voice and Qwen3-TTS makes it. Say who they are and how they sound. {c.FirstName}’s Voice Notes are added to the description, so what you write there shapes the voice too.";
        return Ui.Column(Look.Sm, Ui.Headline("Describe a Voice"), Ui.Caption(note, TextWrapping.Wrap), box, row);
    }

    // MARK: Shade and what the book says

    Control ShadeEditor(BookCharacter c)
    {
        var resolver = VoiceSetup.Resolver(Document);
        var shade = c.Shade ?? resolver.Shades.GetValueOrDefault(c.Id) ?? Shade.Make();
        var engine = resolver.Narrator?.Engine ?? Engine;
        var id = c.Id;
        void Set(int pitch, int pace) => Document.SetShade(id, Shade.Make(pitch, pace, shade.Words));
        string Steps(int n, string low, string high) => n == 0 ? "the narrator’s" : $"{Math.Abs(n)} {(n < 0 ? low : high)}";
        var automatic = new CheckBox { Content = "Automatic", IsChecked = c.Shade is null };
        ToolTip.SetTip(automatic, $"Work out {c.FirstName}’s shade from their gender, age and voice brief");
        automatic.IsCheckedChanged += (_, _) => Document.SetShade(id, automatic.IsChecked == true ? null : shade);
        var pitch = Ui.Row(Look.Xs, Ui.Button("−", () => Set(Math.Max(-4, shade.Pitch - 1), shade.Pace)), Ui.Button("+", () => Set(Math.Min(4, shade.Pitch + 1), shade.Pace)),
            Ui.Text($"Pitch: {Steps(shade.Pitch, "lower", "higher")}"));
        ToolTip.SetTip(pitch, "Semitones lower or higher than the narrator’s voice. Within 2 or 3 it still sounds like the narrator.");
        var pace = Ui.Row(Look.Xs, Ui.Button("−", () => Set(shade.Pitch, Math.Max(-2, shade.Pace - 1))), Ui.Button("+", () => Set(shade.Pitch, Math.Min(2, shade.Pace + 1))),
            Ui.Text($"Pace: {(shade.Pace == 0 ? "the narrator’s" : shade.Pace < 0 ? "slower" : "quicker")}"));
        ToolTip.SetTip(pace, "A little slower or quicker than the narrator");
        var buttons = Ui.Row(Look.Sm);
        if (resolver.Narrator is { } n)
        {
            buttons.Children.Add(Ui.Button($"Hear {c.FirstName}", () => Audition.Play(n, c, Document, shade: shade)));
            var narrator = Ui.Button("Hear the Narrator", () => Audition.Play(n, c, Document));
            ToolTip.SetTip(narrator, "The same line in the narrator’s own voice, to compare");
            buttons.Children.Add(narrator);
        }
        string note = engine switch
        {
            VoiceEngine.Qwen => "Qwen3-TTS shifts the narrator’s voice sample, so it stays the same voice.",
            VoiceEngine.Apple => "Windows Voices change pitch and speed.",
            _ => $"{engine.Title()} shifts the finished audio. Keep it within 2 to sound natural.",
        };
        var header = new DockPanel();
        DockPanel.SetDock(automatic, Dock.Right);
        header.Children.Add(automatic);
        header.Children.Add(Ui.Headline("Shade"));
        return Ui.Card(Ui.Column(Look.Sm, header,
            Ui.Caption(shade.Cue is { } cue ? $"The narrator’s voice, {cue}." : "The narrator’s own voice, unchanged."),
            pitch, pace, buttons, Ui.Caption(note, TextWrapping.Wrap)), Look.Md);
    }

    /// What casting read about a character, and where from.
    Control ProfileCard(BookCharacter c)
    {
        var profile = VoiceCasting.Profile(c, Document.Project, Document.Index);
        var column = Ui.Column(Look.Xs, Ui.Headline("What the Book Says"));
        void Trait(string label, string? value) => column.Children.Add(Ui.Row(Look.Sm, Ui.Text(label, 13, FontWeight.Medium), Ui.Caption(value ?? "—")));
        Trait("Gender", profile.Gender?.Title());
        Trait("Age", profile.Age?.Title());
        Trait("Accent", profile.Accent);
        Trait("Voice", profile.Timbre.Count > 0 ? string.Join(", ", profile.Timbre) : null);
        if (profile.Evidence.Count > 0) column.Children.Add(Ui.Caption(string.Join(" · ", profile.Evidence), TextWrapping.Wrap));
        if (profile.Gender is null)
            column.Children.Add(Ui.Caption($"The book hasn’t said whether {c.FirstName} is a man, a woman or neither. Build Summaries reads it for details, or choose it on the Cast page.", TextWrapping.Wrap));
        else if (profile.GenderGuessed)
            column.Children.Add(Ui.Caption("This is a guess from a single pronoun. Choose the gender on the Cast page to settle it.", TextWrapping.Wrap));
        return Ui.Card(column, Look.Md);
    }
}
