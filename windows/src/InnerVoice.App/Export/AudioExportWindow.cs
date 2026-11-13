// Ported from AudioExportSheet in the Mac's Sources/InnerVoice/Speech/AudioExport.swift
// (File › Export Audio…): the whole book, some chapters or one character's lines, as M4A or
// WAV, a file per chapter or one, Combined or Separate.

using System.Diagnostics;
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
        Closing += (_, _) => running?.Cancel();
        Build();
    }

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
                characterID = picker.SelectedIndex <= 0 ? AudioExporter.NarratorOnly : cast[picker.SelectedIndex - 1].Id;
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
                recaps.IsCheckedChanged += (_, _) => AppSettings.Update(s => s.ExportAudioRecaps = recaps.IsChecked == true);
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
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Choose a folder for the audio files", AllowMultiple = false });
        if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder) return;
        var files = await Export(folder);
        if (files is { Count: > 0 })
        {
            Close();
            Reveal(files[0].Path);
        }
    }

    /// Writes the files into `folder` (tests call this without the folder picker). Returns
    /// them, or null when stopped or failed (the window says which).
    public async Task<List<ExportedAudio>?> Export(string folder)
    {
        var project = document.Project;
        var index = document.Index;
        var resolver = VoiceSetup.Resolver(document);
        var picked = chapters.Picked;
        var parts = AudioExporter.Parts(project, index, picked, Narration, Recaps, perChapter, resolver, oneCharacter ? characterID ?? AudioExporter.NarratorOnly : null);
        var stem = Separate && !oneCharacter ? AudioExporter.Stems(project, index, resolver) : null;
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
