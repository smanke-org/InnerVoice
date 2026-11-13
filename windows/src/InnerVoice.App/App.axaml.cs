using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using InnerVoice.App.Document;
using InnerVoice.App.UI;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.App;

public partial class App : Application
{
    public static new App Current => (App)Application.Current!;
    public static string Version => typeof(App).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    readonly List<ProjectWindow> projects = [];
    StartWindow? start;
    IClassicDesktopStyleApplicationLifetime? desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ApplyAppearance();
        Speech.VoiceSetup.Start();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime d)
        {
            desktop = d;
            d.ShutdownMode = ShutdownMode.OnLastWindowClose;
            d.Exit += (_, _) =>
            {
                InnerVoice.App.AI.ModelManager.Shared.UnloadNow();
                Speech.SpeechPlayer.Shared.Stop();
            };
            var files = (d.Args ?? []).Where(a => !a.StartsWith("--")).ToList();
            foreach (var f in files) OpenPath(f);
            if (projects.Count == 0) ShowStart();
        }
        base.OnFrameworkInitializationCompleted();
    }

    public void ApplyAppearance() => RequestedThemeVariant = AppSettings.Current.Appearance switch
    {
        Appearance.Light => ThemeVariant.Light,
        Appearance.Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    public void ShowStart()
    {
        if (start is null)
        {
            start = new StartWindow();
            start.Closed += (_, _) => start = null;
        }
        start.Show();
        start.Activate();
    }

    void CloseStart()
    {
        start?.Close();
        start = null;
    }

    public ProjectWindow Show(ProjectDocument document)
    {
        var w = new ProjectWindow(document);
        projects.Add(w);
        w.Show();
        CloseStart();
        return w;
    }

    public void WindowClosed(ProjectWindow w)
    {
        projects.Remove(w);
        if (projects.Count == 0) ShowStart();
    }

    public void OpenPath(string path)
    {
        var open = projects.FirstOrDefault(p => p.Document.FilePath is { } fp && string.Equals(Path.GetFullPath(fp), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        if (open is not null) { open.Activate(); return; }
        try
        {
            if (path.EndsWith(".innervoice", StringComparison.OrdinalIgnoreCase))
            {
                var doc = ProjectDocument.Open(path);
                AppSettings.AddRecent(path);
                Show(doc);
            }
            else
            {
                _ = Import(path, null);
            }
        }
        catch (Exception e)
        {
            var owner = (Visual?)projects.LastOrDefault() ?? start;
            if (owner is not null) _ = Dialogs.Alert(owner, "Couldn't Open the Project", $"“{Path.GetFileName(path)}” couldn't be opened. {e.Message}");
        }
    }

    public async Task OpenWithPicker(Visual owner)
    {
        var path = await Dialogs.OpenFile(owner, "Open Project", Dialogs.Projects, Dialogs.Manuscripts);
        if (path is not null) OpenPath(path);
    }

    public async Task NewFromManuscript(Visual owner)
    {
        var path = await Dialogs.OpenFile(owner, "Choose a Manuscript", Dialogs.Manuscripts);
        if (path is not null) await Import(path, owner);
    }

    /// Makes a new project from a manuscript, then asks where to save it.
    async Task Import(string path, Visual? owner)
    {
        owner ??= (Visual?)projects.LastOrDefault() ?? start;
        ImportedBook book;
        try
        {
            book = await Task.Run(() => ManuscriptImporter.Read(path));
        }
        catch (Exception e)
        {
            if (owner is not null) await Dialogs.Alert(owner, "Couldn't Open the Manuscript", e is ImportException ? e.Message : $"Unable to read “{Path.GetFileName(path)}”. {e.Message}");
            return;
        }
        var project = ProjectBuilder.Make(book, Path.GetFileName(path));
        var document = ProjectDocument.New(project, (Path.GetFileName(path), await File.ReadAllBytesAsync(path)));
        var window = Show(document);
        if (!AppPaths.IsTestRun)
        {
            var saved = await Dialogs.SaveFile(window, "Save the New Project", project.Title, "innervoice", Dialogs.Projects, Path.GetDirectoryName(path));
            if (saved is not null)
            {
                if (!saved.EndsWith(".innervoice", StringComparison.OrdinalIgnoreCase)) saved += ".innervoice";
                if (document.TrySave(out var error, saved)) AppSettings.AddRecent(saved);
                else await Dialogs.Alert(window, "Couldn't Save", error ?? "");
            }
        }
        _ = document.RecolorAutomaticAsync();
    }

    /// The made-up sample story, tagged by the rules, not saved anywhere.
    public void OpenSample()
    {
        var blocks = PlainTextReader.Blocks(SampleStory.Markdown, markdown: true);
        var chapters = ChapterDetector.Chapters(blocks, SampleStory.Title);
        var project = ProjectBuilder.Make(new ImportedBook(SampleStory.Title, SampleStory.Author, chapters), "Sample Story.md");
        Show(ProjectDocument.Unsaved(project));
    }
}
