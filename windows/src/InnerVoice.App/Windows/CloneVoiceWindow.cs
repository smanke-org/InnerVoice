using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Windows;

/// Book › Clone a Voice… (and the Voices page): a voice made from recordings of one person.
/// The Mac's CloneVoiceSheet. On Windows only ElevenLabs clones for now; cloning on this PC
/// needs Qwen3-TTS, which comes later. Nothing is sent until the rights checkbox is on, and
/// then only the recordings and the name, never text from the book. Recordings made here are
/// temporary files, deleted when the window closes.
public sealed class CloneVoiceWindow : Window
{
    enum Place { ThisPC, ElevenLabs }

    readonly ProjectDocument document;
    readonly List<string> recordings = [];
    readonly HashSet<string> temporary = [];
    Place place = Place.ElevenLabs;
    string name = "";
    /// Who gets the new voice: null no one yet, Guid.Empty the narrator, else a character.
    Guid? target;
    bool removeNoise;
    bool rightsConfirmed;
    bool cloning;
    string? error;
    VoiceInfo? made;
    ElevenLabsEngine.CloneAllowance? allowance;
    bool allowanceAsked;
    MicRecorder? recorder;
    CancellationTokenSource? cts;

    public static Task Open(Window owner, ProjectDocument document) => new CloneVoiceWindow(document).ShowDialog(owner);

    public CloneVoiceWindow(ProjectDocument doc)
    {
        document = doc;
        Title = "Clone a Voice";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 820;
        CanResize = false;
        FontFamily = Look.UIFamily;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        ElevenLabsEngine.Shared.Changed += Build;
        Closed += (_, _) =>
        {
            ElevenLabsEngine.Shared.Changed -= Build;
            cts?.Cancel();
            recorder?.Dispose();
            foreach (var f in temporary) try { File.Delete(f); } catch { }
        };
        Build();
    }

    /// The files picked or recorded (for tests and the Voices page).
    public IReadOnlyList<string> Recordings => recordings;

    bool ElevenReady => ElevenLabsEngine.Shared.IsSetUp && document.Project.Settings.CloudVoicesAllowed is not null;

    /// Why Clone Voice is off, or null when it's on.
    string? Blocked()
    {
        if (place == Place.ThisPC) return "Cloning on this PC needs Qwen3-TTS, which comes in a later version";
        if (!ElevenLabsEngine.Shared.IsSetUp) return "Set up ElevenLabs first";
        if (document.Project.Settings.CloudVoicesAllowed is null) return "Allow ElevenLabs for this book first";
        if (allowance is { CanClone: false }) return "Your ElevenLabs plan doesn’t include voice cloning";
        if (allowance is { SlotsFull: true }) return "Your ElevenLabs plan’s voice slots are full";
        if (name.Trim().Length == 0) return "Give the voice a name";
        if (recordings.Count == 0) return "Add a recording of the voice";
        if (recorder is not null) return "Stop recording first";
        if (!rightsConfirmed) return "Confirm you have the right to clone this voice";
        return null;
    }

    void Build()
    {
        if (place == Place.ElevenLabs && ElevenReady && !allowanceAsked && (!AppPaths.IsTestRun || ElevenLabsEngine.TestHttp is not null))
        {
            allowanceAsked = true;
            _ = LoadAllowance();
        }
        var panel = new StackPanel { Margin = new Thickness(Look.Xl), Spacing = Look.Md };
        panel.Children.Add(Ui.Title("Clone a Voice"));
        panel.Children.Add(Ui.Text("Make a voice from recordings of one person: you, or someone who agreed to it. It speaks any line in the book.", 13, wrap: TextWrapping.Wrap));

        if (made is { } v)
        {
            panel.Children.Add(Done(v));
            Content = Ui.Scroll(panel);
            return;
        }

        // Where the voice is made and kept.
        var places = Ui.Row(0);
        foreach (var (label, p) in new[] { ("On This PC", Place.ThisPC), ("ElevenLabs (Cloud)", Place.ElevenLabs) })
        {
            var b = new ToggleButton { Content = label, IsChecked = place == p, Padding = new Thickness(12, 4) };
            b.Click += (_, _) => { place = p; Build(); };
            places.Children.Add(b);
        }
        ToolTip.SetTip(places, "Where the voice is made and kept");
        panel.Children.Add(places);
        panel.Children.Add(PlaceNotice());

        // Name, who gets it.
        var nameBox = new TextBox { Text = name, PlaceholderText = "Ellen, warm and low", Width = 300 };
        nameBox.TextChanged += (_, _) => { name = nameBox.Text ?? ""; UpdateButton(); };
        panel.Children.Add(Row("Name:", nameBox));
        var speaking = document.Project.Characters.Where(c => (document.Index.StatsFor(c.Id)?.Lines ?? 0) > 0)
            .OrderByDescending(c => document.Index.StatsFor(c.Id)?.Lines ?? 0).ToList();
        var options = new List<(string Title, Guid? Id)> { ("No One Yet", null), ("Narrator", Guid.Empty) };
        options.AddRange(speaking.Select(c => (c.Name, (Guid?)c.Id)));
        var who = new ComboBox { ItemsSource = options.Select(o => o.Title).ToList(), SelectedIndex = Math.Max(0, options.FindIndex(o => o.Id == target)), MinWidth = 200 };
        who.SelectionChanged += (_, _) => { if (who.SelectedIndex >= 0) target = options[who.SelectedIndex].Id; };
        ToolTip.SetTip(who, "Who gets the new voice in this book once it’s made. You can give it to anyone later.");
        panel.Children.Add(Row("Use for:", who));

        panel.Children.Add(RecordingsSection());

        var noise = new CheckBox { Content = "Remove background noise", IsChecked = removeNoise };
        noise.IsCheckedChanged += (_, _) => removeNoise = noise.IsChecked == true;
        ToolTip.SetTip(noise, "ElevenLabs cleans up the recordings first. Leave it off for clean recordings: it can make them sound worse.");
        panel.Children.Add(noise);

        var rights = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "I have the right and the consent of the person whose voice this is to clone it, and I accept ElevenLabs’ terms for voice cloning.",
                TextWrapping = TextWrapping.Wrap,
            },
            IsChecked = rightsConfirmed,
        };
        rights.IsCheckedChanged += (_, _) => { rightsConfirmed = rights.IsChecked == true; UpdateButton(); };
        panel.Children.Add(rights);
        panel.Children.Add(Ui.Caption("The recordings and the name are sent to ElevenLabs and kept in your ElevenLabs account. Cloning uses one of your plan’s voice slots. No text from your book is sent.", TextWrapping.Wrap));

        if (cloning)
            panel.Children.Add(Ui.Column(Look.Xs, Ui.Caption("Cloning the voice with ElevenLabs…"), new ProgressBar { IsIndeterminate = true }));
        if (error is not null) panel.Children.Add(Ui.Text(error, 12, brush: Look.Brush(Look.Attention), wrap: TextWrapping.Wrap));

        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        cloneButton = new Button { Content = "Clone Voice", IsDefault = true };
        cloneButton.Classes.Add("accent");
        cloneButton.Click += (_, _) => _ = Clone();
        UpdateButton();
        var buttons = Ui.Row(Look.Sm, cancel, cloneButton);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        panel.Children.Add(buttons);
        Content = Ui.Scroll(panel);
    }

    Button? cloneButton;

    void UpdateButton()
    {
        if (cloneButton is null) return;
        var why = Blocked();
        cloneButton.IsEnabled = why is null && !cloning;
        ToolTip.SetTip(cloneButton, why ?? "Send the recordings and the name to ElevenLabs and make the voice");
    }

    static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = Look.Md };
        var l = Ui.Text(label, 13);
        l.VerticalAlignment = VerticalAlignment.Center;
        l.TextAlignment = TextAlignment.Right;
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    Control Notice(string text, string? button = null, Action? action = null)
    {
        var row = new DockPanel();
        if (button is not null && action is not null)
        {
            var b = Ui.Button(button, action);
            DockPanel.SetDock(b, Dock.Right);
            b.Margin = new Thickness(Look.Sm, 0, 0, 0);
            b.VerticalAlignment = VerticalAlignment.Top;
            row.Children.Add(b);
        }
        row.Children.Add(Ui.Text(text, 13, wrap: TextWrapping.Wrap));
        return new Border
        {
            Background = Look.Brush(Look.Retake.WithAlpha(Look.IsDark ? 0.18 : 0.08)),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(Look.Md),
            Child = row,
        };
    }

    Control PlaceNotice()
    {
        if (place == Place.ThisPC)
            return Notice("Cloning a voice on this PC needs Qwen3-TTS, which comes to InnerVoice for Windows in a later version. Until then, clone with ElevenLabs (Cloud).",
                "Use ElevenLabs", () => { place = Place.ElevenLabs; Build(); });
        if (!ElevenLabsEngine.Shared.IsSetUp)
            return Notice("Cloning with ElevenLabs needs your ElevenLabs account. Set it up in Settings › Voices.", "Open Settings",
                () => SettingsWindow.Show(this, "Voices"));
        if (document.Project.Settings.CloudVoicesAllowed is null)
            return Notice("This book hasn’t allowed ElevenLabs yet. Allowing it sends the lines its ElevenLabs voices speak, as text.", "Allow for This Book",
                () => { document.SetCloudVoicesAllowed(true); allowanceAsked = false; Build(); });
        if (!ElevenLabsEngine.CloningOffered(document.Project))
            return Ui.Text($"This book speaks with {document.Project.EngineInUse().Title()}. You can still clone a voice with ElevenLabs; to hear it in this book, choose ElevenLabs or Mix Engines on the Voices page.",
                13, brush: Look.Brush(Look.SecondaryInk), wrap: TextWrapping.Wrap);
        if (allowance is { CanClone: false })
            return Notice("Voice cloning needs a paid ElevenLabs plan (Starter or above). Your plan doesn’t include it.", "See Plans",
                () => _ = Launcher.LaunchUriAsync(new Uri(ElevenLabsEngine.PlansPage)));
        if (allowance is { SlotsFull: true } full)
            return Notice($"All {full.SlotLimit} of your plan’s voice slots are in use. Delete a voice you no longer need in Settings › Voices, or at elevenlabs.io.",
                "Open Settings", () => SettingsWindow.Show(this, "Voices"));
        if (allowance is { SlotLimit: > 0 } a)
            return Ui.Text($"Uses 1 of your plan’s voice slots ({a.SlotsUsed} of {a.SlotLimit} in use).", 13, brush: Look.Brush(Look.SecondaryInk));
        return new Panel();
    }

    async Task LoadAllowance()
    {
        try { allowance = await ElevenLabsEngine.Shared.Allowance(); } catch { allowance = null; }
        Build();
    }

    // MARK: Recordings

    static readonly FilePickerFileType AudioFiles = new("Audio")
    {
        Patterns = ["*.wav", "*.mp3", "*.m4a", "*.aac", "*.flac", "*.ogg", "*.aif", "*.aiff"],
    };

    Control RecordingsSection()
    {
        var col = Ui.Column(Look.Sm, Ui.Headline("Recordings"));
        col.Children.Add(Ui.Caption(OperatingSystem.IsWindows()
            ? "Record the voice here, or add audio files (MP3, WAV, M4A…). ElevenLabs needs at least 10 seconds; 1 to 2 minutes of clear speech clones best."
            : "Add audio files (MP3, WAV, M4A…). ElevenLabs needs at least 10 seconds; 1 to 2 minutes of clear speech clones best.", TextWrapping.Wrap));
        foreach (var path in recordings)
        {
            var row = new DockPanel();
            var remove = Ui.Button("Remove", () =>
            {
                recordings.Remove(path);
                if (temporary.Remove(path)) try { File.Delete(path); } catch { }
                Build();
            }, tip: "Remove this recording");
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            var label = temporary.Contains(path) ? $"Recording {recordings.IndexOf(path) + 1}" : Path.GetFileName(path);
            if (Duration(path) is { } d) label += $"  ({Clock(d)})";
            var t = Ui.Text(label, 13);
            t.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(t);
            col.Children.Add(row);
        }
        var actions = Ui.Row(Look.Sm, Ui.Button("Add Files…", () => _ = AddFiles(), tip: "Add recordings of the voice"));
        if (OperatingSystem.IsWindows())
        {
            if (recorder is { } r)
                actions.Children.Add(Ui.Button($"Stop Recording ({Clock(r.Elapsed)} of 3:00)", StopRecording, primary: true));
            else
                actions.Children.Add(Ui.Button("Record…", StartRecording, tip: "Record the voice with this PC’s microphone"));
        }
        col.Children.Add(actions);
        return col;
    }

    static double? Duration(string path)
    {
        if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return null;
        try { return SpeechAudio.Seconds(WavFile.Read(path)); } catch { return null; }
    }

    static string Clock(double seconds) => $"{(int)seconds / 60}:{(int)seconds % 60:00}";

    async Task AddFiles()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Add Recordings", AllowMultiple = true, FileTypeFilter = [AudioFiles] });
        foreach (var f in files)
            if (f.TryGetLocalPath() is { } p && !recordings.Contains(p)) recordings.Add(p);
        Build();
    }

    /// Adds files without the picker (tests).
    public void AddRecordings(IEnumerable<string> paths)
    {
        foreach (var p in paths) if (!recordings.Contains(p)) recordings.Add(p);
        Build();
    }

    void StartRecording()
    {
        try
        {
            var file = Path.Combine(Path.GetTempPath(), $"InnerVoice-recording-{Guid.NewGuid():N}.wav");
            temporary.Add(file);
            recorder = new MicRecorder(file, maxSeconds: 180, onTick: () => Avalonia.Threading.Dispatcher.UIThread.Post(Build),
                                       onStop: () => Avalonia.Threading.Dispatcher.UIThread.Post(StopRecording));
            error = null;
        }
        catch (Exception e)
        {
            recorder = null;
            error = $"Unable to record: {e.Message}";
        }
        Build();
    }

    void StopRecording()
    {
        if (recorder is not { } r) return;
        recorder = null;
        r.Dispose();
        if (File.Exists(r.Path) && new FileInfo(r.Path).Length > 44) recordings.Add(r.Path);
        Build();
    }

    // MARK: Clone

    /// Sets what the window would send (tests).
    public void Prepare(string voiceName, bool rights, Guid? useFor = null)
    {
        name = voiceName;
        rightsConfirmed = rights;
        target = useFor;
        Build();
    }

    public async Task Clone()
    {
        if (Blocked() is not null || cloning) return;
        cloning = true;
        error = null;
        Build();
        cts = new CancellationTokenSource();
        try
        {
            var v = await ElevenLabsEngine.Shared.CloneVoice(name, recordings, rightsConfirmed, cts.Token, removeNoise);
            made = v;
            if (target is { } t) document.SetVoice(v.Choice, t == Guid.Empty ? null : t);
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { error = e.Message; }
        finally { cloning = false; }
        Build();
    }

    /// The made voice: hear it, and give it to someone.
    Control Done(VoiceInfo v)
    {
        var col = Ui.Column(Look.Md);
        col.Children.Add(Ui.Row(Look.Sm, Ui.Headline($"“{v.Name}” is ready"), SettingsWindow.ElevenLabsCloudBadge()));
        string? who = target is { } t ? t == Guid.Empty ? "the narrator" : document.Project.Character(t)?.Name : null;
        col.Children.Add(Ui.Text(who is null
            ? "It’s in your ElevenLabs voices, so every project can use it. Give it to a character on the Voices page."
            : $"{char.ToUpperInvariant(who[0])}{who[1..]} speaks with it in this book now.", 13, wrap: TextWrapping.Wrap));
        if (!ElevenLabsEngine.CloningOffered(document.Project))
            col.Children.Add(Ui.Caption($"This book speaks with {document.Project.EngineInUse().Title()}. To hear “{v.Name}” in it, choose ElevenLabs or Mix Engines on the Voices page, then pick it there.", TextWrapping.Wrap));
        var play = Ui.Button("Play", () => Play(v), tip: "Hear the new voice say a line");
        var done = new Button { Content = "Done", IsDefault = true };
        done.Classes.Add("accent");
        done.Click += (_, _) => Close();
        var buttons = Ui.Row(Look.Sm, play, done);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        col.Children.Add(buttons);
        return col;
    }

    void Play(VoiceInfo v)
    {
        var p = document.Project;
        if (p.Settings.CloudVoicesAllowed is null) return;
        // One sample line from a made-up speaker who has the new voice.
        var speaker = Guid.NewGuid();
        var seg = new SpeechSegment
        {
            Id = "clone-sample", ParagraphID = Guid.Empty, LineID = Guid.NewGuid(), SpeakerID = speaker, Kind = LineKind.Spoken,
            Text = "The light was never mine to leave you. It belongs to you now.", Pause = PauseKind.None, PauseAfter = 0,
        };
        var resolver = new VoiceResolver(p, VoiceEngine.Elevenlabs, document.Index);
        resolver.Characters[speaker] = v.Choice;
        SpeechPlayer.Shared.Play([seg], resolver);
    }
}

/// The PC's microphone into a WAV file (Windows only, through WinMM), up to `maxSeconds`.
sealed class MicRecorder : IDisposable
{
    public string Path { get; }
    readonly NAudio.Wave.WaveInEvent input;
    readonly NAudio.Wave.WaveFileWriter writer;
    readonly DateTime started = DateTime.UtcNow;
    readonly double maxSeconds;
    readonly Action onTick, onStop;
    DateTime lastTick = DateTime.UtcNow;
    bool stopped;

    public double Elapsed => (DateTime.UtcNow - started).TotalSeconds;

    public MicRecorder(string path, double maxSeconds, Action onTick, Action onStop)
    {
        Path = path;
        this.maxSeconds = maxSeconds;
        this.onTick = onTick;
        this.onStop = onStop;
        input = new NAudio.Wave.WaveInEvent { WaveFormat = new NAudio.Wave.WaveFormat(44100, 16, 1) };
        writer = new NAudio.Wave.WaveFileWriter(path, input.WaveFormat);
        input.DataAvailable += (_, e) =>
        {
            if (stopped) return;
            writer.Write(e.Buffer, 0, e.BytesRecorded);
            if ((DateTime.UtcNow - lastTick).TotalSeconds >= 1) { lastTick = DateTime.UtcNow; onTick(); }
            if (Elapsed >= this.maxSeconds) { stopped = true; onStop(); }
        };
        input.StartRecording();
    }

    public void Dispose()
    {
        stopped = true;
        try { input.StopRecording(); } catch { }
        input.Dispose();
        writer.Dispose();
    }
}
