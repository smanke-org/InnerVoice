// Ported from EpubExportSheet in the Mac's Sources/InnerVoice/Export/EpubExport.swift
// (File › Export EPUB with Audio…): the book's text with every line read in its character's
// voice, highlighted as it's read in reading apps that play read-along books.

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

public sealed class EpubExportWindow : Window
{
    readonly ProjectDocument document;
    readonly StackPanel content = new() { Margin = new Thickness(Look.Xl), Spacing = Look.Lg };
    readonly ChapterPicker chapters;
    readonly ProgressBar bar = new() { Minimum = 0, Maximum = 1, IsVisible = false };
    readonly TextBlock status = Ui.Caption("", TextWrapping.Wrap);
    CancellationTokenSource? running;
    string? error;

    public static Task Show(Window owner, ProjectDocument document) => new EpubExportWindow(document).ShowDialog(owner);

    public EpubExportWindow(ProjectDocument document)
    {
        this.document = document;
        Title = "Export EPUB with Audio";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        FontFamily = Look.UIFamily;
        Background = Look.Brush(Look.Paper);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Content = content;
        chapters = new ChapterPicker(document);
        chapters.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(Build);
        VoiceCastingRun.CastIfNeeded(document);
        Closing += (_, _) => running?.Cancel();
        Build();
    }

    void Build()
    {
        content.Children.Clear();
        var project = document.Project;
        var engine = project.EngineInUse();
        content.Children.Add(Ui.Text("Export EPUB with Audio", 17, FontWeight.SemiBold));
        content.Children.Add(Ui.Caption($"The book’s text with every line read in its character’s voice and tone, with {engine.Title()}. In a reading app that plays read-along books, each sentence is highlighted as it’s read.", TextWrapping.Wrap));
        if (project.Settings.CloudVoicesAllowed is not null && engine is VoiceEngine.Elevenlabs or VoiceEngine.Mixed)
            content.Children.Add(Ui.Caption("☁ ElevenLabs voices are made in the cloud: exporting sends the text they speak to ElevenLabs, and uses your plan’s credits.", TextWrapping.Wrap));
        // EPUB audio is M4A: without Windows' AAC encoder there's no book to make.
        if (AudioEncoders.M4a is null)
            content.Children.Add(Ui.Text("An EPUB with audio needs M4A. " + AudioEncoders.NoM4a, 13, brush: Look.Brush(Look.Retake), wrap: TextWrapping.Wrap));
        content.Children.Add(Ui.Headline("Chapters"));
        if (chapters.Parent is Border old) old.Child = null;
        content.Children.Add(Ui.Card(chapters));
        AudioExportWindow.AddProgress(content, bar, status, running is not null);
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
            var export = new Button { Content = "Export…", IsDefault = true, IsEnabled = chapters.Picked.Count > 0 && AudioEncoders.M4a is not null };
            export.Classes.Add("accent");
            export.Click += async (_, _) => await PickAndExport();
            buttons.Children.Add(cancel);
            buttons.Children.Add(export);
        }
        content.Children.Add(buttons);
    }

    /// "Book - Chapters 1–3.epub" when only some chapters go out.
    public string SuggestedName
    {
        get
        {
            var title = document.Project.Title.Trim().Length == 0 ? "Book" : document.Project.Title;
            var which = chapters.EveryChapter ? "" : " - " + SpeechScript.ChapterLabel(chapters.Picked);
            return AudioExporter.SafeName(title + which) + ".epub";
        }
    }

    async Task PickAndExport()
    {
        var path = await Dialogs.SaveFile(this, "Export EPUB with Audio", SuggestedName, "epub", new FilePickerFileType("EPUB") { Patterns = ["*.epub"] });
        if (path is null) return;
        if (await Export(path))
        {
            Close();
            AudioExportWindow.Reveal(path);
        }
    }

    /// Writes the book to `path` (tests call this without the save dialog). False when stopped
    /// or failed (the window says which).
    public async Task<bool> Export(string path)
    {
        if (AudioEncoders.M4a is not { } encoder)
        {
            error = "An EPUB with audio needs M4A. " + AudioEncoders.NoM4a;
            Build();
            return false;
        }
        var project = document.Project;
        var index = document.Index;
        var resolver = VoiceSetup.Resolver(document);
        var picked = chapters.Picked;
        var source = new CancellationTokenSource();
        running = source;
        error = null;
        status.Text = "";
        bar.Value = 0;
        Build();
        try
        {
            double spoken = await Task.Run(() => EpubAudioExport.Write(project, index, picked, path, resolver, encoder,
                progress: (done, total, what) => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    bar.Value = total == 0 ? 1 : (double)done / total;
                    status.Text = what.Length == 0 ? "Putting the book together…" : $"Reading {what} ({done:N0} of {total:N0})…";
                }),
                ct: source.Token));
            Activity.Record(ActivityKey.SecondsExported, (int)Math.Round(spoken));
            return true;
        }
        catch (OperationCanceledException)
        {
            status.Text = "Stopped.";
            return false;
        }
        catch (Exception e)
        {
            if (source.IsCancellationRequested) status.Text = "Stopped.";
            else
            {
                error = $"Unable to export the book: {e.Message}";
                SpeechLog.Write($"export EPUB failed: {e.GetType().Name}: {e.Message}");
            }
            return false;
        }
        finally
        {
            running = null;
            Build();
        }
    }
}
