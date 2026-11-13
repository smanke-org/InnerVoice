using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Import;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

namespace InnerVoice.App.Windows;

/// Settings › Voices, the Mac's VoicesSettings: Table Read, Pauses (with Try It), the engines
/// (Windows Voices, ElevenLabs marked Cloud), Cloned Voices, and the saved audio.
public sealed partial class SettingsWindow
{
    bool refreshedElevenLabsVoices;
    /// Try It plays a passage over and over until Stop.
    static bool sampling;

    void AddVoicesTab(TabControl tabs) =>
        tabs.Items.Add(new TabItem { FontSize = 16, Header = "Voices", Content = Ui.Scroll(VoicesPane()) });

    Control VoicesPane()
    {
        var s = AppSettings.Current;
        var pane = Pane();

        // Table Read
        pane.Children.Add(Ui.Headline("Table Read"));
        pane.Children.Add(Choice("Play from Here reads", s.ReadNarration, [("Narration and dialogue", true), ("Dialogue only", false)],
            v => AppSettings.Update(x => x.ReadNarration = v)));
        pane.Children.Add(Check("Follow along in the manuscript", s.FollowAlong, v => AppSettings.Update(x => x.FollowAlong = v)));
        pane.Children.Add(Check("Read written text in the narrator’s voice", s.WrittenByNarrator, v => AppSettings.Update(x => x.WrittenByNarrator = v),
            "Letters, notes and signs a character wrote are read by the narrator. Off: by the character who wrote them."));
        pane.Children.Add(Note("Narration is read in the narrator’s voice, chosen on the Voices page; in a first-person book, the narrator character’s voice. Export Audio asks each time."));

        pane.Children.Add(PausesSection());
        pane.Children.Add(EnginesSection());
        if (LocalClones.Shared.All.Count > 0) pane.Children.Add(LocalClonesSection());
        if (ElevenLabsEngine.Shared.IsSetUp && EngineSwitches.Shared.IsOn(VoiceEngine.Elevenlabs)) pane.Children.Add(ClonedVoicesSection());
        pane.Children.Add(SavedAudioSection());
        return pane;
    }

    static Control Check(string label, bool value, Action<bool> set, string? tip = null)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        box.IsCheckedChanged += (_, _) => { if ((box.IsChecked == true) != value) set(box.IsChecked == true); };
        if (tip is not null) ToolTip.SetTip(box, tip);
        return box;
    }

    /// A row with room for the longer pause labels.
    static Control WideRow(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,*"), ColumnSpacing = Look.Md };
        var l = Ui.Text(label, 13);
        l.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    static TextBlock Note(string text) => Ui.Caption(text, TextWrapping.Wrap);

    /// The orange Cloud mark wherever ElevenLabs appears.
    public static Border ElevenLabsCloudBadge()
    {
        var pill = Ui.Pill("Cloud", Look.Retake);
        ToolTip.SetTip(pill, "Made on ElevenLabs’ computers, not this PC");
        return pill;
    }

    // MARK: Pauses

    Control PausesSection()
    {
        var col = Ui.Column(Look.Sm, Ui.Headline("Pauses"));
        var current = PauseSettings.Current;
        var presets = Ui.Row(Look.Xs);
        var preset = VoiceSetup.Preset(current);
        foreach (var (name, settings) in VoiceSetup.Presets)
        {
            var b = new ToggleButton { Content = name, IsChecked = preset == name, Padding = new Thickness(10, 3) };
            b.Click += (_, _) => { VoiceSetup.SetPauses(settings); };
            presets.Children.Add(b);
        }
        col.Children.Add(WideRow("Presets", presets));
        col.Children.Add(Check("Set the pause between sentences", current.SplitSentences, v => VoiceSetup.SetPauses(PauseSettings.Current with { SplitSentences = v }),
            "Speak one sentence at a time with the pause below. Off: each voice decides the pause between sentences itself."));
        col.Children.Add(PauseSlider("Between sentences", p => p.Sentence, (p, v) => p with { Sentence = v }, 1.5, current.SplitSentences));
        col.Children.Add(PauseSlider("Between a line and the narration", p => p.Line, (p, v) => p with { Line = v }, 1.5));
        col.Children.Add(PauseSlider("Between paragraphs", p => p.Paragraph, (p, v) => p with { Paragraph = v }, 3));
        col.Children.Add(PauseSlider("After a chapter title", p => p.Heading, (p, v) => p with { Heading = v }, 3));
        col.Children.Add(PauseSlider("At a scene break", p => p.SceneBreak, (p, v) => p with { SceneBreak = v }, 5));
        col.Children.Add(PauseSlider("Between chapters", p => p.Chapter, (p, v) => p with { Chapter = v }, 8));

        var speeds = new[] { 0.75, 1.0, 1.25, 1.5, 2.0 };
        var speedRow = Ui.Row(Look.Xs);
        double speed = SpeechPlayer.Shared.Speed;
        foreach (var v in speeds)
        {
            var b = new ToggleButton { Content = SpeedTitle(v), IsChecked = Math.Abs(speed - v) < 0.01, Padding = new Thickness(10, 3) };
            b.Click += (_, _) => SpeechPlayer.Shared.Speed = v;
            speedRow.Children.Add(b);
        }
        ToolTip.SetTip(speedRow, "How fast Play plays, pauses included. Export Audio always saves at normal speed.");
        col.Children.Add(WideRow("Playback speed", speedRow));

        var tryIt = Ui.Button(sampling && SpeechPlayer.Shared.IsPlaying ? "Stop" : "Try It", () =>
        {
            if (sampling && SpeechPlayer.Shared.IsPlaying) StopSample(); else StartSample();
            Build();
        }, tip: "Play a passage over and over while you change the pauses and speed");
        col.Children.Add(WideRow("Try it", tryIt));
        col.Children.Add(Note("Seconds at normal speed. Changes apply to the next pause, even while playing, and to Export Audio. Each voice’s own silence before and after the words is trimmed, so these are the pauses you hear."));
        return col;
    }

    Control PauseSlider(string label, Func<PauseSettings, double> get, Func<PauseSettings, double, PauseSettings> with, double max, bool enabled = true)
    {
        double value = get(PauseSettings.Current);
        var slider = new Slider { Minimum = 0, Maximum = max, Value = value, Width = 180, IsEnabled = enabled, SmallChange = 0.05, LargeChange = 0.25 };
        var shown = Ui.Caption($"{value:0.00} s");
        shown.Width = 56;
        shown.TextAlignment = TextAlignment.Right;
        shown.VerticalAlignment = VerticalAlignment.Center;
        // Kept while dragging without rebuilding the pane: the player looks the pause up per gap.
        slider.ValueChanged += (_, e) =>
        {
            double v = Math.Round(e.NewValue * 20) / 20;
            shown.Text = $"{v:0.00} s";
            PauseSettings.Current = with(PauseSettings.Current, v);
            if (!AppPaths.IsTestRun) AppSettings.UpdateQuietly(x => x.Pauses = PauseSettings.Current);
        };
        return WideRow(label, Ui.Row(Look.Sm, slider, shown));
    }

    public static string SpeedTitle(double s) => s == Math.Floor(s) ? $"{(int)s}×" : $"{s:0.##}×";

    static void StartSample()
    {
        var document = App.Current.FrontProject?.Document ?? SampleDocument();
        VoiceCastingRun.CastIfNeeded(document);
        var project = document.Project;
        var index = document.Index;
        // About three paragraphs from the first one with dialogue.
        var chapter = project.Chapters.FirstOrDefault(c => c.Paragraphs.Any(p => index.LinesIn(p.Id).Count > 0));
        if (chapter is null) return;
        var paras = chapter.Paragraphs;
        int start = Math.Max(0, paras.FindIndex(p => index.LinesIn(p.Id).Count > 0));
        var segs = paras.Skip(Math.Max(0, start - 1)).Take(start - Math.Max(0, start - 1) + 3)
            .SelectMany(p => SpeechScript.Segments(p, project, index, includeNarration: true)).ToList();
        sampling = true;
        PlaySample(segs, VoiceSetup.Resolver(document));
    }

    static void PlaySample(List<SpeechSegment> segs, VoiceResolver resolver) =>
        SpeechPlayer.Shared.Play(segs, resolver, onFinish: async () =>
        {
            // Over and over until Stop, so every change is heard soon.
            if (!sampling) return;
            await Task.Delay(TimeSpan.FromSeconds(PauseSettings.Current.Paragraph));
            if (sampling) PlaySample(segs, resolver);
        });

    static void StopSample()
    {
        if (!sampling) return;
        sampling = false;
        SpeechPlayer.Shared.Stop();
    }

    static ProjectDocument SampleDocument()
    {
        var blocks = PlainTextReader.Blocks(SampleStory.Markdown, markdown: true);
        var chapters = ChapterDetector.Chapters(blocks, SampleStory.Title);
        return ProjectDocument.Unsaved(ProjectBuilder.Make(new ImportedBook(SampleStory.Title, SampleStory.Author, chapters), "Sample Story.md"));
    }

    // MARK: Engines

    static ToggleSwitch EngineSwitch(VoiceEngine engine, Action rebuild)
    {
        var sw = EngineSwitches.Shared;
        bool on = sw.IsOn(engine);
        var t = new ToggleSwitch { IsChecked = on, OnContent = "", OffContent = "", IsEnabled = !on || sw.CanTurnOff(engine) };
        ToolTip.SetTip(t, on
            ? sw.CanTurnOff(engine) ? $"Turn {engine.Title()} off: it won’t be offered on the Voices page" : "At least one engine stays on"
            : $"Turn {engine.Title()} on");
        t.IsCheckedChanged += (_, _) => { sw.Set(engine, t.IsChecked == true); rebuild(); };
        return t;
    }

    Control EnginesSection()
    {
        var col = Ui.Column(Look.Sm, Ui.Headline("Engines"));

        // Windows Voices
        var windows = VoiceEngineRegistry.Get(VoiceEngine.Apple) as WindowsVoices;
        var voices = windows?.Voices ?? [];
        var wTitle = Ui.Row(Look.Sm, Ui.Text("Windows Voices", 14, FontWeight.SemiBold), Ui.Caption("Built in"));
        var wCol = Ui.Column(Look.Xs, wTitle,
            Ui.Text(voices.Count > 0
                ? $"{voices.Count} English {(voices.Count == 1 ? "voice" : "voices")} on this PC: {string.Join(", ", voices.Take(6).Select(v => v.Name))}{(voices.Count > 6 ? "…" : "")}. Instant, with no download; they can’t act out a tone, so a tone only changes their speed, pitch and loudness."
                : windows?.NotReadyReason ?? "Windows Voices need Windows.", 12, wrap: TextWrapping.Wrap));
        var wActions = Ui.Row(Look.Sm, EngineSwitch(VoiceEngine.Apple, Build));
        if (windows is not null)
            wActions.Children.Add(Ui.Button("Reload Voices", () => { windows.Reload(); Build(); },
                tip: "Look again for voices added in Windows Settings › Time & language › Speech"));
        wActions.Children.Add(Ui.Button("Get More Voices…", () => _ = Launcher.LaunchUriAsync(new Uri("ms-settings:speech")),
            tip: "Add voices in Windows Settings › Time & language › Speech"));
        wCol.Children.Add(wActions);
        col.Children.Add(Ui.Card(wCol, Look.Md));

        // Qwen3-TTS and Orpheus: downloaded voice models that run on this PC.
        foreach (var pack in VoicePacks.Packs) col.Children.Add(LocalModelCard(pack));
        col.Children.Add(HardwareCard());

        col.Children.Add(ElevenLabsRow());
        col.Children.Add(Note("Windows Voices, Qwen3-TTS and Orpheus run on this PC; downloading them is the only time they use the internet. ElevenLabs is different: it makes its voices in the cloud, so the text it speaks is sent to ElevenLabs, and each book asks first. Choose the engine for each project on its Voices page; turn an engine off to hide it there."));
        return col;
    }

    Control ElevenLabsRow()
    {
        var eleven = ElevenLabsEngine.Shared;
        var title = Ui.Row(Look.Sm, Ui.Text("ElevenLabs", 14, FontWeight.SemiBold), ElevenLabsCloudBadge());
        if (eleven.IsSetUp) title.Children.Add(Ui.Pill("Connected", Look.Gray));
        var col = Ui.Column(Look.Xs, title,
            Ui.Text("Natural voices made on ElevenLabs’ servers. The text they speak is sent to ElevenLabs, and uses your plan’s credits.", 12, wrap: TextWrapping.Wrap));
        var actions = Ui.Row(Look.Sm);
        if (eleven.IsSetUp)
        {
            actions.Children.Add(EngineSwitch(VoiceEngine.Elevenlabs, Build));
            actions.Children.Add(Ui.Button("Change…", () => _ = ElevenLabsSetupDialog().ShowDialog(this)));
            if (!refreshedElevenLabsVoices)
            {
                refreshedElevenLabsVoices = true;
                if (!AppPaths.IsTestRun || ElevenLabsEngine.TestHttp is not null) _ = RefreshElevenLabsVoices();
            }
        }
        else actions.Children.Add(Ui.Button("Set Up…", () => _ = ElevenLabsSetupDialog().ShowDialog(this),
            tip: "Connect your ElevenLabs account. Its voices are made in the cloud."));
        col.Children.Add(actions);
        return Ui.Card(col, Look.Md);
    }

    static async Task RefreshElevenLabsVoices()
    {
        try { await ElevenLabsEngine.Shared.RefreshVoices(); } catch { } // keep the list we have
    }

    /// Setting up ElevenLabs: what it means for the book's privacy, getting a key, checking it,
    /// and the narrator's yes. Checking the key reads the plan only; no book text is sent.
    public static Window ElevenLabsSetupDialog()
    {
        var eleven = ElevenLabsEngine.Shared;
        var dialog = new Window
        {
            Title = "Set Up ElevenLabs", Width = 520, SizeToContent = SizeToContent.Height, CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner, ShowInTaskbar = false,
            FontFamily = Look.UIFamily, Background = Look.Brush(Look.Paper),
        };
        var privacy = new Button { Content = "ElevenLabs Privacy Policy", Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent };
        privacy.Click += (_, _) => _ = dialog.Launcher.LaunchUriAsync(new Uri(ElevenLabsEngine.PrivacyPage));
        var warning = new Border
        {
            Background = Look.Brush(Look.Retake.WithAlpha(Look.IsDark ? 0.18 : 0.08)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Look.Md),
            Child = Ui.Column(Look.Xs,
                Ui.Text("ElevenLabs sends your book to the cloud", 13, FontWeight.SemiBold),
                Ui.Text("Everything else in InnerVoice happens on this PC. ElevenLabs doesn’t: its voices are made on ElevenLabs’ servers. Every line you play or export with an ElevenLabs voice is sent there as text, so a table read or an exported book sends the whole manuscript to ElevenLabs, a third party, under its own terms.", 13, wrap: TextWrapping.Wrap),
                Ui.Text("Each book asks before its text is sent the first time.", 13, wrap: TextWrapping.Wrap),
                privacy),
        };
        var key = new TextBox { PasswordChar = '•', PlaceholderText = "API key", Text = eleven.ApiKey ?? "" };
        var openKeys = new Button { Content = "Open API Keys Page", Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent };
        openKeys.Click += (_, _) => _ = dialog.Launcher.LaunchUriAsync(new Uri(ElevenLabsEngine.KeysPage));
        var signUp = new Button { Content = "Create a Free Account", Classes = { "link" }, Padding = new Thickness(0), Background = Brushes.Transparent };
        signUp.Click += (_, _) => _ = dialog.Launcher.LaunchUriAsync(new Uri(ElevenLabsEngine.SignUpPage));
        var agree = new CheckBox
        {
            Content = new TextBlock { Text = "I understand that when I use ElevenLabs voices, the text they speak from my books is sent to ElevenLabs.", TextWrapping = TextWrapping.Wrap },
            IsChecked = eleven.Consented is not null,
        };
        var status = Ui.Text("", 12, wrap: TextWrapping.Wrap);
        status.IsVisible = false;
        var turnOn = new Button { Content = "Turn On ElevenLabs", IsDefault = true, IsEnabled = false };
        turnOn.Classes.Add("accent");
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => dialog.Close();
        var disconnect = new Button { Content = "Disconnect", IsVisible = eleven.IsSetUp };
        ToolTip.SetTip(disconnect, "Forget the key. Nothing more is sent to ElevenLabs.");
        disconnect.Click += (_, _) => { eleven.Disconnect(); dialog.Close(); };
        void Update() => turnOn.IsEnabled = !string.IsNullOrWhiteSpace(key.Text) && agree.IsChecked == true;
        key.TextChanged += (_, _) => Update();
        agree.IsCheckedChanged += (_, _) => Update();
        Update();
        turnOn.Click += async (_, _) =>
        {
            turnOn.IsEnabled = false;
            turnOn.Content = "Checking…";
            status.IsVisible = false;
            try
            {
                var credits = await ElevenLabsEngine.Check(key.Text ?? "");
                eleven.Save(key.Text ?? "", consent: true);
                EngineSwitches.Shared.Set(VoiceEngine.Elevenlabs, true);
                _ = RefreshElevenLabsVoices();
                dialog.Close();
            }
            catch (Exception e)
            {
                status.Text = e.Message;
                status.Foreground = Look.Brush(Look.Attention);
                status.IsVisible = true;
                turnOn.Content = "Turn On ElevenLabs";
                Update();
            }
        };
        var buttons = Ui.Row(Look.Sm, disconnect, cancel, turnOn);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(Look.Xl), Spacing = Look.Md,
            Children =
            {
                Ui.Row(Look.Sm, Ui.Title("Set Up ElevenLabs"), ElevenLabsCloudBadge()),
                warning,
                Ui.Text("In ElevenLabs, create an API key (Text to Speech, read access to User, and access to Voices: read, or write to clone voices), then copy it. A free account works to try it; cloning voices needs a paid plan.", 13, wrap: TextWrapping.Wrap),
                Ui.Row(Look.Md, openKeys, signUp),
                Ui.Column(Look.Xs, Ui.Headline("API key"), key),
                agree, status, buttons,
            },
        };
        return dialog;
    }

    // MARK: Local voice models

    static readonly Color Caution = Color.Parse("#B8860B");

    /// Qwen3-TTS or Orpheus: what it does, Download (with progress, Cancel) or Installed
    /// (Remove), where it runs, and the hardware warning when this PC is weak for it.
    Control LocalModelCard(VoicePack pack)
    {
        var models = VoiceModels.Shared;
        var e = pack.Engine;
        var title = Ui.Row(Look.Sm, Ui.Text(pack.Title, 14, FontWeight.SemiBold), Ui.Caption("Runs on this PC"));
        if (models.IsInstalled(e)) title.Children.Add(Ui.Pill("Installed", Look.Gray));
        var col = Ui.Column(Look.Xs, title, Ui.Text(pack.Summary, 12, wrap: TextWrapping.Wrap));
        var actions = Ui.Row(Look.Sm);
        switch (models.States.GetValueOrDefault(e))
        {
            case VoiceModels.State.Downloading d:
                var bar = new ProgressBar { Minimum = 0, Maximum = 1, Value = d.Fraction, Width = 160, VerticalAlignment = VerticalAlignment.Center };
                actions.Children.Add(bar);
                actions.Children.Add(Ui.Caption(d.Verifying ? "Checking…" : $"{(int)(d.Fraction * 100)}% of {pack.SizeDescription}"));
                actions.Children.Add(Ui.Button("Cancel", () => models.Cancel(e), tip: "Stop the download. Download again later to pick up where it stopped."));
                break;
            case VoiceModels.State.Failed f:
                actions.Children.Add(Ui.Text(f.Message, 12, brush: Look.Brush(Caution), wrap: TextWrapping.Wrap));
                actions.Children.Add(Ui.Button("Download", () => _ = StartDownload(e)));
                break;
            default:
                if (models.IsInstalled(e))
                {
                    actions.Children.Add(EngineSwitch(e, Build));
                    actions.Children.Add(Ui.Button("Remove…", async () =>
                    {
                        if (await Dialogs.Confirm(this, $"Remove {pack.Title}?",
                            $"Its models ({pack.SizeDescription}) are deleted from this PC{(e == VoiceEngine.Qwen ? ", with the voice samples made from them" : "")}. Books cast with it keep their voices and can download it again.", "Remove"))
                            models.Remove(e);
                    }, tip: $"Delete the {pack.Title} models from this PC ({pack.SizeDescription})"));
                }
                else actions.Children.Add(Ui.Button($"Download ({pack.SizeDescription})", () => _ = StartDownload(e), tip: $"Download {pack.Title} from Hugging Face"));
                break;
        }
        col.Children.Add(actions);
        if (models.HardwareKnown && models.Warning(e) is { } why)
            col.Children.Add(Ui.Text(why, 12, brush: Look.Brush(Caution), wrap: TextWrapping.Wrap));
        if (NeuralModels.Shared.IsLoaded(e)) col.Children.Add(Ui.Caption($"Loaded, running on the {(Onnx.LastDevice == "CPU" ? "processor" : "graphics card")}."));
        return Ui.Card(col, Look.Md);
    }

    async Task StartDownload(VoiceEngine e)
    {
        if (await VoiceModels.Shared.ConfirmHardware(this, e, downloading: true)) VoiceModels.Shared.Download(e);
    }

    /// What this PC has, and what makes the local voices fast.
    Control HardwareCard()
    {
        var models = VoiceModels.Shared;
        var hw = models.Hardware;
        var col = Ui.Column(Look.Xs, Ui.Text("This PC", 14, FontWeight.SemiBold));
        if (!models.HardwareKnown) col.Children.Add(Ui.Caption("Checking the graphics card and memory…"));
        else
        {
            col.Children.Add(Ui.Text(hw.HasGpu
                ? $"Graphics card: {hw.GpuName}, {hw.GpuMemoryGB:0.#} GB of video memory (DirectX 12)."
                : "Graphics card: none that runs DirectX 12 with memory of its own, so the voice models run on the processor.", 12, wrap: TextWrapping.Wrap));
            col.Children.Add(Ui.Text($"Memory: {hw.MemoryGB} GB. Processor: {hw.CpuName} ({hw.CpuThreads} threads, {hw.Architecture}).", 12, wrap: TextWrapping.Wrap));
        }
        col.Children.Add(Ui.Caption(VoiceHardwareCheck.Recommended, TextWrapping.Wrap));
        return Ui.Card(col, Look.Md);
    }

    // MARK: Voices cloned on this PC

    Control LocalClonesSection()
    {
        var store = LocalClones.Shared;
        var col = Ui.Column(Look.Sm, Ui.Headline("Voices Cloned on This PC"));
        foreach (var c in store.All)
        {
            var rename = Ui.Button("Rename…", async () =>
            {
                var name = await Dialogs.Ask(this, "Rename Voice", "The new name shows in every project.", "Rename", c.Name, "Name");
                if (!string.IsNullOrWhiteSpace(name)) { store.Rename(c.Id, name); Build(); }
            }, tip: "Change the name it has in every project");
            var delete = Ui.Button("Delete…", async () =>
            {
                if (await Dialogs.Confirm(this, $"Delete “{c.Name}”?",
                    "The voice and its recording are removed from this PC. Characters that use it, in any book, will need another voice. This can’t be undone.", "Delete"))
                { store.Delete(c.Id); Build(); }
            }, tip: "Delete the voice and its recording from this PC");
            var row = new DockPanel();
            var actions = Ui.Row(Look.Sm, rename, delete);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var label = Ui.Column(0, Ui.Text(c.Name, 13),
                Ui.Caption($"{c.Gender} · {(int)c.Seconds / 60}:{(int)c.Seconds % 60:00} clip · {c.Created.ToLocalTime():MMM d, yyyy}"));
            label.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(label);
            col.Children.Add(row);
        }
        col.Children.Add(Note("Qwen3-TTS speaks these from a short recording kept on this PC, so every project here can use them. Casting never chooses them on its own."));
        return col;
    }

    // MARK: Cloned Voices

    Control ClonedVoicesSection()
    {
        var eleven = ElevenLabsEngine.Shared;
        var col = Ui.Column(Look.Sm, Ui.Row(Look.Sm, Ui.Headline("Cloned Voices"), ElevenLabsCloudBadge()));
        var clones = eleven.Clones;
        if (clones.Count == 0)
            col.Children.Add(Ui.Text("None yet. To clone a voice with ElevenLabs, choose Book › Clone a Voice… and ElevenLabs (Cloud).", 13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap));
        var problem = Ui.Text("", 12, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap);
        problem.IsVisible = false;
        async Task Run(Func<Task> work)
        {
            problem.IsVisible = false;
            try { await work(); }
            catch (Exception e) { problem.Text = e.Message; problem.IsVisible = true; }
        }
        foreach (var v in clones)
        {
            var rename = Ui.Button("Rename…", async () =>
            {
                var name = await Dialogs.Ask(this, "Rename Voice", "The new name shows in every project and at elevenlabs.io.", "Rename", v.Name, "Name");
                if (!string.IsNullOrWhiteSpace(name) && name.Trim() != v.Name) await Run(() => eleven.RenameVoice(v.VoiceID, name));
            }, tip: "Change the name it has in every project and at elevenlabs.io");
            var delete = Ui.Button("Delete…", async () =>
            {
                if (await Dialogs.Confirm(this, $"Delete “{v.Name}”?",
                    "It’s removed from your ElevenLabs account, which frees a voice slot. Characters that use it, in any book, will need another voice. This can’t be undone.", "Delete"))
                    await Run(() => eleven.DeleteVoice(v.VoiceID));
            }, tip: "Delete it from your ElevenLabs account and free its voice slot");
            var row = new DockPanel();
            var actions = Ui.Row(Look.Sm, rename, delete);
            DockPanel.SetDock(actions, Dock.Right);
            row.Children.Add(actions);
            var name = Ui.Text(v.Name, 13);
            name.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(name);
            col.Children.Add(row);
        }
        col.Children.Add(problem);
        col.Children.Add(Note("Voices you cloned from recordings are kept in your ElevenLabs account, so every project can use them. Casting never chooses them on its own."));
        return col;
    }

    // MARK: Saved audio

    Control SavedAudioSection()
    {
        var renderer = SpeechRenderer.Shared;
        long size = renderer.CacheSize;
        var shown = Ui.Caption(FileSize(size));
        shown.VerticalAlignment = VerticalAlignment.Center;
        var clear = Ui.Button("Clear", () => { renderer.ClearCache(); Build(); }, tip: "Remove the saved audio. Lines are made again the next time they play.");
        clear.IsEnabled = size > 0;
        return Ui.Column(Look.Sm, Ui.Headline("Saved Audio"),
            Row("Saved audio", Ui.Row(Look.Sm, shown, clear)),
            Note("Lines are saved after they’re first made, so they play at once next time. The oldest go when it passes 1 GB."));
    }

    public static string FileSize(long bytes) => bytes switch
    {
        < 1000 => $"{bytes} bytes",
        < 1_000_000 => $"{bytes / 1000.0:0} KB",
        < 1_000_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        _ => $"{bytes / 1_000_000_000.0:0.##} GB",
    };
}
