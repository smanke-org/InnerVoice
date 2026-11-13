// The voices in a project window: Book menu items (Project Type, Use Voices from Another
// Project, Clone a Voice, Play Line, Play from Here, Stop Playing, Cast Voices Again), the
// project-type question, and what each project type shows (the Mac's Views/ProjectType.swift).

using Avalonia.Controls;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using InnerVoice.App.Speech;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

public sealed partial class ProjectWindow
{
    /// Menu items each project type hides, updated when a menu opens.
    readonly List<(MenuItem Item, Func<Project, bool> Shown)> typedItems = [];

    MenuItem Typed(MenuItem item, Func<Project, bool> shown)
    {
        typedItems.Add((item, shown));
        return item;
    }

    void UpdateTypedItems()
    {
        foreach (var (item, shown) in typedItems) item.IsVisible = shown(Document.Project);
    }

    void AddVoiceItems(MenuItem book)
    {
        book.SubmenuOpened += (_, _) => UpdateTypedItems();
        book.Items.Add(new Separator());
        book.Items.Add(Item("Project _Type…", null, () => _ = ProjectTypeWindow.Ask(this, Document)));
        var voicesSeparator = new Separator();
        book.Items.Add(voicesSeparator);
        book.Items.Add(Typed(Item("Use Voices from Another _Project…", null, () => _ = SeriesVoicesWindow.Show(this, Document)), p => p.ShowsVoices));
        book.Items.Add(Typed(Item("C_lone a Voice…", null, () => CloneVoice(null)), p => p.ShowsVoices));
        book.Items.Add(Typed(Item("_Play Line", "Ctrl+Space", PlayLine), p => p.ShowsVoices));
        book.Items.Add(Typed(Item("Play from _Here", "Ctrl+Shift+Space", PlayFromHere), p => p.ShowsVoices));
        book.Items.Add(Typed(Item("_Stop Playing", "Ctrl+OemPeriod", SpeechPlayer.Shared.Stop), p => p.ShowsVoices));
        book.Items.Add(Typed(Item("Cast _Voices Again", null, () => { if (Document.Project.ShowsVoices) Document.CastVoicesAgain(); }), p => p.ShowsVoices));
        book.Items.Add(Typed(Item("Stop Sending This Book to ElevenLabs", null, () => Document.SetCloudVoicesAllowed(false)),
            p => p.ShowsVoices && p.Settings.CloudVoicesAllowed is not null));
        book.SubmenuOpened += (_, _) => voicesSeparator.IsVisible = Document.Project.ShowsVoices;
        VoicesPage.OpenCloneVoice ??= (page, id) => (TopLevel.GetTopLevel(page) as ProjectWindow)?.CloneVoice(id);
        SpeechPlayer.Shared.Changed += ShowPlayFailure;
        Closed += (_, _) => SpeechPlayer.Shared.Changed -= ShowPlayFailure;
    }

    /// Clone a Voice (ElevenLabs on Windows): the window comes with the ElevenLabs work.
    public void CloneVoice(Guid? characterID) => OpenCloneVoiceWindow?.Invoke(this, Document, characterID);

    /// Set by Clone a Voice's window class at startup.
    public static Action<ProjectWindow, ProjectDocument, Guid?>? OpenCloneVoiceWindow { get; set; }

    ManuscriptPage Manuscript => (ManuscriptPage)Get(PageKind.Manuscript);

    void PlayLine()
    {
        if (!Document.Project.ShowsVoices) return;
        Manuscript.PlayLines();
    }

    void PlayFromHere()
    {
        if (!Document.Project.ShowsVoices) return;
        if (Workspace.Page != PageKind.Manuscript) Workspace.Go(PageKind.Manuscript);
        Manuscript.PlayFromHere();
    }

    void ShowPlayFailure()
    {
        if (SpeechPlayer.Shared.Failure is { } f && IsActive) SetStatus(f);
    }

    /// A project that hasn't been asked what it's for asks once (not in test runs).
    async Task AskProjectTypeIfNeeded()
    {
        if (Document.Project.Settings.Purpose is not null || Document.IsReadOnly || AppPaths.IsTestRun) return;
        await ProjectTypeWindow.Ask(this, Document);
    }

    /// The pages and menus change with the project type.
    void ProjectTypeChanged()
    {
        if (!VisiblePages().Contains(Workspace.Page)) Workspace.Go(PageKind.Manuscript);
        foreach (var kind in pages.Keys.Where(k => !VisiblePages().Contains(k)).ToList()) pages.Remove(kind);
    }
}

/// What is this project for? (the Mac's project-type question).
public sealed class ProjectTypeWindow : Window
{
    public static async Task Ask(Window owner, ProjectDocument document)
    {
        var w = new ProjectTypeWindow(document);
        await w.ShowDialog(owner);
    }

    public static string Detail(ProjectPurpose p) => p == ProjectPurpose.Narration
        ? "You’ll narrate the book yourself. See who’s speaking on every line, with character summaries, an index of each part, a teleprompter for the booth and narrator scripts."
        : "You’ll make the audiobook with AI voices. Give every character a voice and a tone, hear a table read, and export the audio, with no human narrator.";

    public static string TitleOf(ProjectPurpose p) => p == ProjectPurpose.Narration ? "Narration Tools" : "Produce with AI Voices";

    ProjectTypeWindow(ProjectDocument document)
    {
        Title = "Project Type";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = Look.WindowIcon;
        Background = Look.Brush(Look.Paper);
        var choice = document.Project.Settings.Purpose ?? ProjectPurpose.Narration;
        var column = new StackPanel { Spacing = Look.Md, Margin = new Avalonia.Thickness(Look.Xl) };
        column.Children.Add(Ui.Title("What is this project for?"));
        column.Children.Add(Ui.Caption("InnerVoice shows the tools for the job. You can change it later in Book › Project Type; nothing is lost when you do.", Avalonia.Media.TextWrapping.Wrap));
        foreach (var p in new[] { ProjectPurpose.Narration, ProjectPurpose.AiVoices })
        {
            var radio = new RadioButton { GroupName = "purpose", IsChecked = p == choice };
            radio.Content = Ui.Column(2, Ui.Headline(TitleOf(p)), Ui.Caption(Detail(p), Avalonia.Media.TextWrapping.Wrap));
            var pp = p;
            radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) choice = pp; };
            column.Children.Add(Ui.Card(radio, Look.Md));
        }
        var go = Ui.Button("Continue", () =>
        {
            if (document.Project.Settings.Purpose != choice) document.SetPurpose(choice);
            Close();
        }, primary: true);
        go.IsDefault = true;
        var buttons = Ui.Row(Look.Sm, go);
        buttons.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        column.Children.Add(buttons);
        Content = column;
    }
}

public sealed partial class ProjectWindow
{
    /// The Listen step: Export Audio (File › Export Audio…).
    void ListenStep() => _ = InnerVoice.App.Export.AudioExportWindow.Show(this, Document);
}
