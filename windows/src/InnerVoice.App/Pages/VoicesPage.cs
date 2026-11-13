// The Voices page (the Mac's Views/VoicesPage.swift): the engine, the Casting Style, and a
// voice for the narrator, each shared role and every character with lines. Main characters
// first; a red dot marks a voice casting was unsure of. The right side shows the picked one:
// its voice, how to hear it, what the book says about them, the best fits and every voice.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Pages;

/// Who the Voices page is showing: the narrator, a shared role or a character.
public abstract record VoiceTarget
{
    public sealed record NarratorVoice : VoiceTarget;
    public sealed record Role(VoiceRole Value) : VoiceTarget;
    public sealed record Character(Guid Id) : VoiceTarget;
    public static readonly VoiceTarget Narrator = new NarratorVoice();
}

public sealed partial class VoicesPage : Page
{
    public override PageKind Kind => PageKind.Voices;

    readonly ComboBox engineBox = new() { MinWidth = 190 };
    readonly StackPanel toolbar;
    readonly Button playButton;
    readonly TextBox search = new() { PlaceholderText = "Search", Margin = new Thickness(Look.Md, Look.Sm) };
    readonly StackPanel list = new();
    readonly ContentControl detail = new();
    bool updating;

    public VoiceTarget Target { get; private set; } = VoiceTarget.Narrator;

    public VoicesPage(ProjectDocument document, Workspace workspace) : base(document, workspace)
    {
        engineBox.SelectionChanged += (_, _) =>
        {
            if (updating || engineBox.SelectedItem is not ComboBoxItem { Tag: VoiceEngine e } || e == Engine) return;
            _ = ChooseEngine(e);
        };
        ToolTip.SetTip(engineBox, "The speech engine the project’s voices use. Each engine keeps its own casting.");
        var castAgain = Ui.Button("Cast Voices Again", () => Document.CastVoicesAgain(),
            tip: "Choose every voice again from the character summaries. Voices you chose stay.");
        var series = Ui.Button("Use Voices from Another Project…", () => { if (TopLevel.GetTopLevel(this) is Window w) _ = Windows.SeriesVoicesWindow.Show(w, Document); },
            tip: "Give characters the voices they had in an earlier book in the series. You match the characters first.");
        playButton = Ui.Button("Play", TogglePlay, tip: "Hear the voice of the one selected");
        toolbar = Ui.Row(Look.Sm, engineBox, castAgain, series, playButton);
        search.TextChanged += (_, _) => BuildList();
        SpeechPlayer.Shared.Changed += UpdatePlayButton;
        DetachedFromVisualTree += (_, _) => SpeechPlayer.Shared.Changed -= UpdatePlayButton;

        var left = new DockPanel { Width = 290 };
        var styleBar = StyleBar();
        DockPanel.SetDock(styleBar, Dock.Top);
        left.Children.Add(styleBar);
        DockPanel.SetDock(search, Dock.Top);
        left.Children.Add(search);
        left.Children.Add(Ui.Scroll(list));
        var leftBorder = new Border
        {
            Child = left, BorderBrush = Look.Brush(Look.Separator), BorderThickness = new Thickness(0, 0, 1, 0),
        };
        var body = new DockPanel();
        DockPanel.SetDock(leftBorder, Dock.Left);
        body.Children.Add(leftBorder);
        body.Children.Add(Ui.Scroll(detail));
        Content = body;
        Refresh(true);
    }

    VoiceEngine Engine => Document.Project.EngineInUse();

    public override Control Toolbar => toolbar;

    public override string Subtitle
    {
        get
        {
            var readers = Readers();
            int unsure = Cast().Count(c => readers.GetValueOrDefault(c.Id, VoiceRole.Own) == VoiceRole.Own && Document.Project.VoiceNeedsYou(c, Engine));
            return unsure == 1 ? "1 voice needs you" : unsure > 1 ? $"{unsure} voices need you" : Engine.Title();
        }
    }

    public override void Shown()
    {
        VoiceCastingRun.CastIfNeeded(Document);
        if (VoiceEngineRegistry.Get(Engine) is { } e) _ = e.Prepare(CancellationToken.None);
        // ElevenLabs' voices, the first time they're needed.
        if (ElevenLabsEngine.Shared.IsSetUp && Engine is VoiceEngine.Elevenlabs or VoiceEngine.Mixed && VoiceEngineRegistry.VoicesOf(VoiceEngine.Elevenlabs).Count == 0)
            _ = RefreshCloudVoices();
    }

    async Task RefreshCloudVoices()
    {
        try { await ElevenLabsEngine.Shared.RefreshVoices(); } catch { }
        Refresh(false);
    }

    public void Show(VoiceTarget target)
    {
        Target = target;
        Refresh(false);
    }

    List<BookCharacter> Cast()
    {
        var index = Document.Index;
        return Document.Project.Characters.Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) > 0)
            .OrderByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0).ToList();
    }

    Dictionary<Guid, VoiceRole> Readers() =>
        Document.Project.Settings.CastingStyle is null ? [] : CastingStyleRules.Readers(Document.Project, Document.Index);

    public override void Refresh(bool textChanged)
    {
        updating = true;
        var choices = EngineSwitches.Shared.Choices;
        engineBox.ItemsSource = choices.Select(e => new ComboBoxItem
        {
            Content = e.Title() + (e.IsCloud() ? " (Cloud)" : VoiceEngineRegistry.IsReady(e) || e == VoiceEngine.Mixed ? "" : " (Not Downloaded)"),
            Tag = e,
        }).ToList();
        engineBox.SelectedIndex = choices.ToList().IndexOf(Engine);
        updating = false;
        BuildList();
        BuildDetail();
        UpdatePlayButton();
    }

    void UpdatePlayButton()
    {
        bool playing = SpeechPlayer.Shared.IsPlaying;
        playButton.Content = playing ? "Stop" : "Play";
        ToolTip.SetTip(playButton, playing ? "Stop Playing" : "Hear the voice of the one selected");
    }

    void TogglePlay()
    {
        if (SpeechPlayer.Shared.IsPlaying) { SpeechPlayer.Shared.Stop(); return; }
        var (choice, character) = CurrentChoice();
        if (choice is not null) Audition.Play(choice, character, Document, shade: character is { } c ? Shades().GetValueOrDefault(c.Id) : null);
    }

    Dictionary<Guid, Shade> Shades() => VoiceSetup.Resolver(Document).Shades;

    /// The voice of the one shown, as it would speak.
    (VoiceChoice? Choice, BookCharacter? Character) CurrentChoice()
    {
        var p = Document.Project;
        var resolver = VoiceSetup.Resolver(Document);
        return Target switch
        {
            VoiceTarget.Character ch when p.Character(ch.Id) is { } c =>
                (resolver.Characters.GetValueOrDefault(c.Id) ?? resolver.Narrator, c),
            VoiceTarget.Role r => (p.Settings.RoleVoice(r.Value, Engine)?.Choice ?? resolver.Narrator, null),
            _ => (resolver.Narrator, null),
        };
    }

    /// The first time a book uses ElevenLabs, ask before any of its text is sent.
    async Task ChooseEngine(VoiceEngine e)
    {
        if (e.IsCloud() && Document.Project.Settings.CloudVoicesAllowed is null)
        {
            if (!await AskCloud())
            {
                Refresh(false);
                return;
            }
            Document.SetCloudVoicesAllowed(true);
        }
        Document.SetVoiceEngine(e);
    }

    async Task<bool> AskCloud() => await Dialogs.Confirm(this, "Send This Book to ElevenLabs?",
        $"ElevenLabs makes its voices on its own servers. Every line of “{Document.Project.Title}” that an ElevenLabs voice speaks is sent there as text; a table read or an exported book sends the whole manuscript. Allow it only if you have the right to share this book. You can stop it at any time on the Voices page.",
        "Send to ElevenLabs", destructive: false);

    // MARK: Casting Style

    Control StyleBar()
    {
        var box = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        var styles = new (CastingStyle? Style, string Title)[]
        {
            (null, "Not Chosen"), (CastingStyle.OneVoice, CastingStyle.OneVoice.Title(ProjectPurpose.AiVoices)),
            (CastingStyle.Shared, CastingStyle.Shared.Title(ProjectPurpose.AiVoices)), (CastingStyle.FullCast, CastingStyle.FullCast.Title(ProjectPurpose.AiVoices)),
        };
        box.ItemsSource = styles.Select(s => s.Title).ToList();
        box.SelectedIndex = Array.FindIndex(styles, s => s.Style == Document.Project.Settings.CastingStyle);
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex < 0 || styles[box.SelectedIndex].Style == Document.Project.Settings.CastingStyle) return;
            Document.SetCastingStyle(styles[box.SelectedIndex].Style);
            VoiceCastingRun.CastIfNeeded(Document);
        };
        Document.Changed += _ => box.SelectedIndex = Array.FindIndex(styles, s => s.Style == Document.Project.Settings.CastingStyle);
        ToolTip.SetTip(box, "How voices are shared: one voice for everyone, a narrator plus voices for men, women and others, or a voice per character. Changing it keeps every voice.");
        var column = Ui.Column(Look.Xs, Ui.Caption("Casting Style"), box);
        column.Margin = new Thickness(Look.Md, Look.Md, Look.Md, 0);
        return column;
    }

    // MARK: The list

    void BuildList()
    {
        list.Children.Clear();
        var p = Document.Project;
        var engine = Engine;
        var resolver = VoiceSetup.Resolver(Document);
        var readers = Readers();
        var q = search.Text?.Trim() ?? "";

        list.Children.Add(Header("Narration"));
        var pov = VoiceResolver.PovNarrator(p, engine, EngineSwitches.Shared);
        string narratorCaption = pov is not null && p.Settings.NarratorVoice(engine)?.Source != VoiceSource.User
            ? $"{pov.FirstName}’s voice (first person)"
            : resolver.Narrator is { } nv ? VoiceNames.Name(nv) : "No voice";
        list.Children.Add(Row("Narrator", null, narratorCaption, false, Target is VoiceTarget.NarratorVoice, () => Show(VoiceTarget.Narrator)));
        foreach (var r in CastingStyleRules.SharedRolesNeeded(p.Settings))
        {
            var v = p.Settings.RoleVoice(r, engine);
            list.Children.Add(Row(r.VoiceTitle(), null, v is null ? "No voice yet" : VoiceNames.Name(v.Choice), v is null,
                                  Target is VoiceTarget.Role rr && rr.Value == r, () => Show(new VoiceTarget.Role(r))));
        }

        var cast = Cast().Where(c => q.Length == 0 || c.Matches(q)).ToList();
        if (cast.Count == 0)
        {
            list.Children.Add(Header("Characters"));
            list.Children.Add(Pad(Ui.Caption(q.Length > 0 ? "No match." : "Characters with lines show here after Tag Speakers.", TextWrapping.Wrap)));
            return;
        }
        list.Children.Add(Header("Characters"));
        foreach (var c in cast)
        {
            var reader = readers.GetValueOrDefault(c.Id, VoiceRole.Own);
            string caption = reader switch
            {
                VoiceRole.Narrator => "Played by the Narrator" + (resolver.Shades.GetValueOrDefault(c.Id)?.Cue is { } cue ? $", {cue}" : ""),
                VoiceRole.Men or VoiceRole.Women or VoiceRole.Other => $"Read in the {reader.VoiceCaption().ToLowerInvariant()}",
                _ => c.Voice(engine) is { } v ? VoiceNames.Name(v.Choice) : "No voice yet",
            };
            bool needs = reader == VoiceRole.Own && p.VoiceNeedsYou(c, engine);
            var id = c.Id;
            list.Children.Add(Row(c.Name, c.ColorHex, caption, needs, Target is VoiceTarget.Character ch && ch.Id == c.Id,
                                  () => Show(new VoiceTarget.Character(id))));
        }
    }

    static Control Header(string text)
    {
        var t = Ui.Text(text.ToUpperInvariant(), 11, FontWeight.SemiBold, Look.Brush(Look.SecondaryInk));
        t.Margin = new Thickness(Look.Md, Look.Md, Look.Md, Look.Xs);
        return t;
    }

    static Control Pad(Control c)
    {
        c.Margin = new Thickness(Look.Md, Look.Xs);
        return c;
    }

    Control Row(string name, string? color, string caption, bool needs, bool selected, Action open)
    {
        var dot = color is null ? (Control)new Border { Width = 10 } : Ui.Dot(color);
        dot.VerticalAlignment = VerticalAlignment.Top;
        dot.Margin = new Thickness(0, 4, 0, 0);
        var text = Ui.Column(1, Ui.Text(name, 13, FontWeight.Medium, maxLines: 1), Ui.Caption(caption));
        var row = new DockPanel();
        if (needs)
        {
            var mark = Ui.Dot(CharacterPalette.AttentionHex, 8);
            ToolTip.SetTip(mark, "Casting wasn’t sure of this voice. Listen, then confirm it or choose another.");
            mark.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(mark, Dock.Right);
            row.Children.Add(mark);
        }
        DockPanel.SetDock(dot, Dock.Left);
        row.Children.Add(dot);
        text.Margin = new Thickness(Look.Sm, 0, 0, 0);
        row.Children.Add(text);
        var button = new Button
        {
            Content = row, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = selected ? Look.Brush(Look.Accent.WithAlpha(Look.IsDark ? 0.35 : 0.14)) : Brushes.Transparent,
            BorderThickness = new Thickness(0), Padding = new Thickness(Look.Md, Look.Sm), CornerRadius = new CornerRadius(6),
            Margin = new Thickness(Look.Xs, 1),
        };
        button.Click += (_, _) => open();
        return button;
    }
}
