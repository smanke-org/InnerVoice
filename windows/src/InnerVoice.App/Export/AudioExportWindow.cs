// Ported from AudioExportSheet in the Mac's Sources/InnerVoice/Speech/AudioExport.swift
// (File › Export Audio…): the whole book, some chapters or one character's lines, as M4A or
// WAV, a file per chapter or one, Combined or Separate.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Export;

public sealed class AudioExportWindow : Window
{
    readonly ProjectDocument document;
    readonly StackPanel content = new() { Margin = new Thickness(Look.Xl), Spacing = Look.Lg };
    readonly ChapterPicker chapters;
    readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    readonly TextBlock status = Ui.Caption("", TextWrapping.Wrap);
    CancellationTokenSource? running;
    string? error;
    bool oneCharacter;
    Guid? characterID;
    /// A file per chapter (true) or one file; kept while the app runs, like the Mac's.
    static bool perChapter = true;
    /// The ElevenLabs credit estimate near the top (when ElevenLabs is set up).
    readonly StackPanel creditsPanel = new() { Spacing = 2, IsVisible = false };
    (long Needed, int Lines, long? Left)? credits;
    /// What the plan has left, asked once per window.
    long? creditsLeft;
    string? creditsKey;
    CancellationTokenSource? creditsRun;

    public static Task Show(Window owner, ProjectDocument document) => new AudioExportWindow(document).ShowDialog(owner);

    public AudioExportWindow(ProjectDocument document)
    {
        this.document = document;
        Title = "Export Audio";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        FontFamily = Look.UIFamily;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = content;
        chapters = new ChapterPicker(document);
        chapters.Changed += () => Later(Build);
        // Casting fills in anyone without a voice before the first line is read.
        VoiceCastingRun.CastIfNeeded(document);
        characterID = Cast.FirstOrDefault()?.Id;
        Closing += (_, _) => { running?.Cancel(); creditsRun?.Cancel(); };
        Build();
    }

    /// The estimate: about how many ElevenLabs credits the export needs, the lines not made
    /// yet and what the plan has left (null: ElevenLabs isn't set up, or nothing to count yet).
    public (long Needed, int Lines, long? Left)? Credits => credits;
    /// The estimate being worked out (tests wait for it).
    public Task CreditsEstimate { get; private set; } = Task.CompletedTask;

    List<BookCharacter> Cast
    {
        get
        {
            var index = document.Index;
            return document.Project.Characters.Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) > 0)
                .OrderByDescending(c => index.StatsFor(c.Id)?.Lines ?? 0).ToList();
        }
    }

    public AudioFormat Format
    {
        get => AudioEncoders.M4a is null ? AudioFormat.Wav : AudioFormats.Parse(AppSettings.Current.ExportAudioFormat) ?? AudioFormat.M4a;
        set => AppSettings.Update(s => s.ExportAudioFormat = value.Title());
    }

    bool Narration => AppSettings.Current.ReadNarration;
    bool Separate => AppSettings.Current.ExportAudioSeparateVoices;
    bool AnyRecap => chapters.Picked.Any(c => document.Project.Chapters[c].RecapText is not null);
    bool Recaps => AppSettings.Current.ExportAudioRecaps && Narration && AnyRecap && !oneCharacter;

    /// Rebuilding inside a control's own event would remove it mid-click.
    static void Later(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);

    void Build()
    {
        content.Children.Clear();
        var project = document.Project;
        var engine = project.EngineInUse();
        content.Children.Add(Ui.Text("Export Audio", 17, FontWeight.SemiBold));
        content.Children.Add(Ui.Caption($"Each line is read in its character’s voice and tone, with {engine.Title()}.", TextWrapping.Wrap));
        if (project.Settings.CloudVoicesAllowed is not null && engine is VoiceEngine.Elevenlabs or VoiceEngine.Mixed)
            content.Children.Add(Ui.Caption("☁ ElevenLabs voices are made in the cloud: exporting sends the text they speak to ElevenLabs, and uses your plan’s credits.", TextWrapping.Wrap));
        (creditsPanel.Parent as Panel)?.Children.Remove(creditsPanel);
        content.Children.Add(creditsPanel);

        var form = new StackPanel { Spacing = Look.Md };
        var scope = new StackPanel { Spacing = Look.Xs };
        scope.Children.Add(Radio("All voices", "AudioScope", !oneCharacter, () => { oneCharacter = false; Later(Build); }));
        scope.Children.Add(Radio("One character or the narrator", "AudioScope", oneCharacter, () => { oneCharacter = true; Later(Build); }));
        form.Children.Add(Row("Export", scope));
        if (oneCharacter)
        {
            var cast = Cast;
            var items = new List<string> { "Narrator (narration only)" };
            items.AddRange(cast.Select(c => c.Name));
            var picker = new ComboBox { ItemsSource = items, MinWidth = 240 };
            picker.SelectedIndex = characterID == AudioExporter.NarratorOnly ? 0 : Math.Max(0, cast.FindIndex(c => c.Id == characterID) + 1);
            picker.SelectionChanged += (_, _) =>
            {
                characterID = picker.SelectedIndex <= 0 ? AudioExporter.NarratorOnly : cast[picker.SelectedIndex - 1].Id;
                Later(EstimateCredits);
            };
            if (picker.SelectedIndex == 0) characterID = AudioExporter.NarratorOnly;
            form.Children.Add(Row("Character", picker));
        }
        else
        {
            var narration = new CheckBox { Content = "Read narration too", IsChecked = Narration };
            narration.IsCheckedChanged += (_, _) => { AppSettings.Update(s => s.ReadNarration = narration.IsChecked == true); Later(Build); };
            form.Children.Add(narration);
            if (Narration && AnyRecap)
            {
                var recaps = new CheckBox { Content = "Say chapter recaps (“Previously…”)", IsChecked = AppSettings.Current.ExportAudioRecaps };
                ToolTip.SetTip(recaps, "The narrator reads each chapter’s recap after its title. Recaps are on the chapter’s right-click menu in the Manuscript.");
                recaps.IsCheckedChanged += (_, _) => { AppSettings.Update(s => s.ExportAudioRecaps = recaps.IsChecked == true); Later(EstimateCredits); };
                form.Children.Add(recaps);
            }
            if (chapters.Picked.Count > 1)
            {
                var files = Ui.Row(Look.Md,
                    Radio("One file per chapter", "AudioFiles", perChapter, () => perChapter = true),
                    Radio("One file", "AudioFiles", !perChapter, () => perChapter = false));
                form.Children.Add(Row("Files", files));
            }
            var voices = Ui.Row(Look.Md,
                Radio("Combined", "AudioVoices", !Separate, () => AppSettings.Update(s => s.ExportAudioSeparateVoices = false)),
                Radio("Separate, a file per voice", "AudioVoices", Separate, () => AppSettings.Update(s => s.ExportAudioSeparateVoices = true)));
            ToolTip.SetTip(voices, "Separate saves the narrator, each shared voice and each character with a voice of their own in files of their own. They’re all the same length, so they line up in an audio editor.");
            form.Children.Add(Row("Voices", voices));
        }
        var formats = Ui.Row(Look.Md,
            Radio("M4A", "AudioFormat", Format == AudioFormat.M4a, () => Format = AudioFormat.M4a, enabled: AudioEncoders.M4a is not null),
            Radio("WAV", "AudioFormat", Format == AudioFormat.Wav, () => Format = AudioFormat.Wav));
        form.Children.Add(Row("Format", formats));
        if (AudioEncoders.M4a is null) form.Children.Add(Ui.Caption(AudioEncoders.NoM4a, TextWrapping.Wrap));
        content.Children.Add(Ui.Card(form));

        content.Children.Add(Ui.Headline("Chapters"));
        if (chapters.Parent is Border old) old.Child = null;
        content.Children.Add(Ui.Card(chapters));
        AddProgress(content, bar, status, running is not null);
        if (error is not null) content.Children.Add(Ui.Text(error, 13, brush: Look.Brush(Look.Retake), wrap: TextWrapping.Wrap));

        var buttons = Ui.Row(Look.Sm);
        buttons.HorizontalAlignment = HorizontalAlignment.Right;
        if (running is not null)
        {
            var stop = new Button { Content = "Stop", IsCancel = true };
            stop.Click += (_, _) => running?.Cancel();
            buttons.Children.Add(stop);
        }
        else
        {
            var cancel = new Button { Content = "Cancel", IsCancel = true };
            cancel.Click += (_, _) => Close();
            var export = new Button { Content = "Export…", IsDefault = true, IsEnabled = chapters.Picked.Count > 0 };
            export.Classes.Add("accent");
            export.Click += async (_, _) => await PickAndExport();
            buttons.Children.Add(cancel);
            buttons.Children.Add(export);
        }
        content.Children.Add(buttons);
        EstimateCredits();
    }

    /// What the estimate depends on: the choices in the window, and the book's text and voices.
    string CreditsKey => string.Join("|", oneCharacter, characterID, Narration, Recaps, string.Join(",", chapters.Picked),
                                     document.Project.EngineInUse(), RuntimeHelpers.GetHashCode(document.Index));

    /// Counts the characters of the ElevenLabs lines not in the audio cache (off the UI
    /// thread), then asks ElevenLabs what the plan has left. Only the plan's numbers come
    /// back; no book text is sent.
    void EstimateCredits()
    {
        if (!ElevenLabsEngine.Shared.IsSetUp)
        {
            creditsRun?.Cancel();
            creditsKey = null;
            credits = null;
            ShowCredits();
            return;
        }
        var key = CreditsKey;
        // While it exports, the line stays as it was; afterwards it's counted again.
        if (key == creditsKey || running is not null) { ShowCredits(); return; }
        creditsKey = key;
        creditsRun?.Cancel();
        var run = creditsRun = new CancellationTokenSource();
        var resolver = VoiceSetup.Resolver(document);
        var parts = AudioExporter.Parts(document.Project, document.Index, chapters.Picked, Narration, Recaps, perChapter, resolver,
                                        oneCharacter ? characterID ?? AudioExporter.NarratorOnly : null);
        CreditsEstimate = Estimate(parts, resolver, run.Token);
    }

    async Task Estimate(List<AudioPart> parts, VoiceResolver resolver, CancellationToken ct)
    {
        try
        {
            var renderer = SpeechRenderer.Shared;
            var (needed, lines) = await Task.Run(() => AudioExporter.CreditsNeeded(parts, resolver, renderer, ct), ct);
            if (ct.IsCancellationRequested) return;
            credits = (needed, lines, creditsLeft);
            ShowCredits();
            if (needed == 0 || creditsLeft is not null) return;
            var account = await ElevenLabsEngine.Shared.Account(ct);
            creditsLeft = account.Left;
            if (ct.IsCancellationRequested) return;
            credits = (needed, lines, creditsLeft);
            ShowCredits();
        }
        catch (Exception e) when (e is OperationCanceledException or SpeechException or HttpRequestException or InvalidOperationException)
        {
            // Only a hint: without the plan's numbers the line says what the export needs.
        }
    }

    /// The estimate's words: the line, and the warning when the plan has too few left.
    public static (string Line, string? Warning) CreditsText(long needed, int lines, long? left) =>
        ($"About {needed:N0} ElevenLabs credits for {lines:N0} {(lines == 1 ? "line" : "lines")} not made yet" + (left is { } l ? $". Your plan has {l:N0} left." : "."),
         left is { } have && needed > have
             ? "That’s more than your plan has left: the export would stop when the credits run out. Lines already made aren’t charged again, so you could export fewer chapters now and the rest later."
             : null);

    void ShowCredits()
    {
        creditsPanel.Children.Clear();
        if (credits is not { Needed: > 0 } c) { creditsPanel.IsVisible = false; return; }
        var (line, warning) = CreditsText(c.Needed, c.Lines, c.Left);
        var brush = Look.Brush(warning is null ? Look.SecondaryInk : Look.Attention);
        creditsPanel.Children.Add(Ui.Text(line, 12, brush: brush, wrap: TextWrapping.Wrap));
        if (warning is not null) creditsPanel.Children.Add(Ui.Text(warning, 12, brush: brush, wrap: TextWrapping.Wrap));
        creditsPanel.IsVisible = true;
    }

    /// The progress bar while it runs, and the last status ("Stopped.") after.
    public static void AddProgress(Panel content, ProgressBar bar, TextBlock status, bool running)
    {
        (bar.Parent as Panel)?.Children.Remove(bar);
        (status.Parent as Panel)?.Children.Remove(status);
        bar.IsVisible = running;
        status.IsVisible = status.Text is { Length: > 0 };
        content.Children.Add(bar);
        content.Children.Add(status);
    }

    static RadioButton Radio(string title, string group, bool on, Action pick, bool enabled = true)
    {
        var r = new RadioButton { Content = title, GroupName = group, IsChecked = on, IsEnabled = enabled };
        r.IsCheckedChanged += (_, _) => { if (r.IsChecked == true) pick(); };
        return r;
    }

    static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("90,*"), ColumnSpacing = Look.Md };
        var l = Ui.Text(label, 13);
        l.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(l);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    async Task PickAndExport()
    {
        if (credits is { Left: { } left } c && c.Needed > left
            && !await Dialogs.Confirm(this, "Not Enough ElevenLabs Credits",
                $"This export needs about {c.Needed:N0} credits and your plan has {left:N0} left. It will stop when they run out; lines already made are kept and aren’t charged again.",
                "Export Anyway", destructive: false, cancelIsDefault: true))
            return;
        // One file: name it. Several (a file per chapter or per voice): name the folder
        // they go in.
        var project = document.Project;
        var resolver = VoiceSetup.Resolver(document);
        var parts = AudioExporter.Parts(project, document.Index, chapters.Picked, Narration, Recaps, perChapter, resolver,
                                        oneCharacter ? characterID ?? AudioExporter.NarratorOnly : null)
            .Where(p => p.Segments.Count > 0).ToList();
        var single = parts.Count == 1 && !(Separate && !oneCharacter);
        var ext = Format.Extension();
        string folder;
        string? name = null;
        if (single)
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Name the audio file",
                SuggestedFileName = AudioExporter.SafeName(parts[0].Name) + "." + ext,
                DefaultExtension = ext,
                ShowOverwritePrompt = true,
                FileTypeChoices = [new FilePickerFileType(ext.ToUpperInvariant() + " audio") { Patterns = ["*." + ext] }],
            });
            if (file?.TryGetLocalPath() is not { } path) return;
            folder = Path.GetDirectoryName(path)!;
            name = Path.GetFileNameWithoutExtension(path);
        }
        else
        {
            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Name the folder for the audio files",
                SuggestedFileName = AudioExporter.SafeName(project.Title) + " Audio",
            });
            if (target?.TryGetLocalPath() is not { } path) return;
            folder = path;
            try { Directory.CreateDirectory(folder); }
            catch (Exception e) { error = e.Message; Build(); return; }
        }
        var files = await Export(folder, name);
        if (files is { Count: > 0 })
        {
            Close();
            Reveal(files[0].Path);
        }
    }

    /// Writes the files into `folder` (tests call this without the folder picker). Returns
    /// them, or null when stopped or failed (the window says which).
    public async Task<List<ExportedAudio>?> Export(string folder, string? singleFileName = null)
    {
        var project = document.Project;
        var index = document.Index;
        var resolver = VoiceSetup.Resolver(document);
        var picked = chapters.Picked;
        var parts = AudioExporter.Parts(project, index, picked, Narration, Recaps, perChapter, resolver, oneCharacter ? characterID ?? AudioExporter.NarratorOnly : null);
        var stem = Separate && !oneCharacter ? AudioExporter.Stems(project, index, resolver) : null;
        // The name typed in the save dialog, for a single file.
        if (singleFileName is { Length: > 0 } && parts.Count(p => p.Segments.Count > 0) == 1)
            parts = parts.Select(p => p.Segments.Count > 0 ? p with { Name = singleFileName } : p).ToList();
        var format = Format;
        var source = new CancellationTokenSource();
        running = source;
        error = null;
        status.Text = "";
        bar.Value = 0;
        Build();
        double spoken = 0;
        try
        {
            var files = await Task.Run(() => AudioExporter.Write(parts, folder, format, resolver, encoder: AudioEncoders.M4a, stem: stem,
                timing: (_, _, b, e) => spoken += e - b,
                progress: (done, total, what) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    bar.Value = total == 0 ? 1 : (double)done / total;
                    status.Text = what.Length == 0 ? "Finishing…" : $"Reading {what} ({done:N0} of {total:N0})…";
                }),
                ct: source.Token));
            return files;
        }
        catch (OperationCanceledException)
        {
            status.Text = "Stopped.";
            return null;
        }
        catch (Exception e)
        {
            if (source.IsCancellationRequested) status.Text = "Stopped.";
            else
            {
                error = $"Unable to export the audio: {e.Message}";
                SpeechLog.Write($"export audio failed: {e.GetType().Name}: {e.Message}");
            }
            return null;
        }
        finally
        {
            Activity.Record(ActivityKey.SecondsExported, (int)Math.Round(spoken));
            running = null;
            creditsKey = null;   // the lines it made are no longer owed
            Build();
        }
    }

    /// Shows the file in File Explorer, like the Mac selects it in the Finder.
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false }); }
        catch { }
    }
}
