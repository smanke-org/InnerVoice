using Avalonia.Controls;
using InnerVoice.App.Pages;
using Page = InnerVoice.App.Pages.Page;
using InnerVoice.App.Controls;
using InnerVoice.App.UI;
using InnerVoice.Core;

namespace InnerVoice.App.Windows;

/// Pages and commands that arrive milestone by milestone.
public sealed partial class ProjectWindow
{
    IEnumerable<PageKind> VisiblePages()
    {
        yield return PageKind.Manuscript;
        yield return PageKind.Review;
        yield return PageKind.Cast;
        yield return PageKind.Index;
        yield return PageKind.Pronunciations;
        yield return PageKind.Record;
        yield return PageKind.Retakes;
        if (Document.Project.Revisions.Count > 0) yield return PageKind.Revisions;
    }

    Page MakeOtherPage(PageKind kind) => kind switch
    {
        PageKind.Review => new ReviewPage(Document, Workspace),
        PageKind.Index => new IndexPage(Document, Workspace),
        PageKind.Pronunciations => new PronunciationsPage(Document, Workspace),
        PageKind.Revisions => new RevisionsPage(Document, Workspace),
        _ => new ManuscriptPage(Document, Workspace),
    };

    void AddFileItems(MenuItem file)
    {
        file.Items.Add(new Separator());
        file.Items.Add(Item("_Import Revised Manuscript…", "Ctrl+Shift+I", () => _ = ImportRevision()));
        file.Items.Add(Item("_Export…", "Ctrl+Shift+E", () => _ = InnerVoice.App.Export.ExportWindow.Show(this, Document)));
        file.Items.Add(new Separator());
        file.Items.Add(Item("Se_ttings…", "Ctrl+OemComma", () => SettingsWindow.Show(this)));
    }

    void AddStepsMenu(MenuItem view)
    {
        view.Items.Add(new Separator());
        view.Items.Add(Item("Show/Hide _Steps", null, () => AppSettings.Update(s => s.StepsHidden = !s.StepsHidden)));
    }

    void AddBookMenu(Menu menu)
    {
        var book = Top("_Book");
        AddAIMenu(book);
        AddBookItems(book);
        menu.Items.Add(book);
    }

    void AddBookItems(MenuItem book)
    {
        book.Items.Add(new Separator());
        book.Items.Add(Item("Find _Pronunciations", null, () => { Workspace.Go(PageKind.Pronunciations); (Get(PageKind.Pronunciations) as PronunciationsPage)?.FindTerms(); }));
        book.Items.Add(Item("_Narrators…", null, () => NarratorsWindow.Show(this, Document)));
        book.Items.Add(Item("_Next Line That Needs You", "Ctrl+Down", () => { Workspace.Go(PageKind.Manuscript); (Get(PageKind.Manuscript) as ManuscriptPage)?.NextNeedingYou(1); }));
        book.Items.Add(Item("Pre_vious Line That Needs You", "Ctrl+Up", () => { Workspace.Go(PageKind.Manuscript); (Get(PageKind.Manuscript) as ManuscriptPage)?.NextNeedingYou(-1); }));
        AddMoreBookItems(book);
    }

    void AddMoreBookItems(MenuItem book) { }

    void AddHelpWindowItem(MenuItem help) => help.Items.Add(Item("InnerVoice _Help", "F1", () => InnerVoice.App.Help.HelpWindow.Show()));

    /// Brings in the author's new draft, keeping every tag, note and mark on the paragraphs
    /// that didn't change.
    async Task ImportRevision()
    {
        var path = await Dialogs.OpenFile(this, "Choose the Revised Manuscript", Dialogs.Manuscripts);
        if (path is null) return;
        try
        {
            var book = await Task.Run(() => InnerVoice.Core.Import.ManuscriptImporter.Read(path));
            var result = RevisionMerger.Merge(Document.Project, book, Path.GetFileName(path));
            var oldIDs = Document.Project.Lines.Select(l => l.Id).ToHashSet();
            int carried = result.Project.Lines.Count(l => l.Source != AssignmentSource.None && oldIDs.Contains(l.Id));
            Document.Change("Import Revised Manuscript", p =>
            {
                p.Chapters = result.Project.Chapters;
                p.Lines = result.Project.Lines;
                p.Recorded = result.Project.Recorded;
                p.Retakes = result.Project.Retakes;
                p.Characters = result.Project.Characters;
                p.Pronunciations = result.Project.Pronunciations;
                p.Revisions = result.Project.Revisions;
                p.SourceFileName = result.Project.SourceFileName;
            }, textChanged: true);
            Activity.Record(ActivityKey.LinesCarriedOver, carried);
            Workspace.Go(PageKind.Revisions);
        }
        catch (Exception e)
        {
            await Dialogs.Alert(this, "Couldn't Open the Revised Manuscript", e.Message);
        }
    }

    StepsBar? steps;

    void RefreshSteps()
    {
        if (AppSettings.Current.StepsHidden) { stepsHost.Child = null; steps = null; return; }
        if (steps is null) stepsHost.Child = steps = new StepsBar(Document, GoToStep);
        else steps.Refresh();
    }

    void GoToStep(GuidedStep step) => Workspace.Go(step switch
    {
        GuidedStep.Review => PageKind.Review,
        GuidedStep.Characters => PageKind.Cast,
        GuidedStep.Pronunciations => VisiblePages().Contains(PageKind.Pronunciations) ? PageKind.Pronunciations : PageKind.Cast,
        GuidedStep.Record => PageKind.Record,
        _ => PageKind.Manuscript,
    });

    void AddHelpItems(MenuItem help)
    {
        AddHelpWindowItem(help);
        help.Items.Add(Item("_About InnerVoice", null, () => AboutWindow.Show(this)));
    }
}
