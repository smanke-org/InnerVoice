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
        yield return PageKind.Record;
        yield return PageKind.Retakes;
    }

    Page MakeOtherPage(PageKind kind) => kind switch
    {
        PageKind.Review => new ReviewPage(Document, Workspace),
        _ => new ManuscriptPage(Document, Workspace),
    };

    void AddFileItems(MenuItem file)
    {
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

    void AddBookItems(MenuItem book) { }

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
        help.Items.Add(Item("_About InnerVoice", null, () => _ = Dialogs.Alert(this, "InnerVoice for Windows",
            $"Version {App.Version}\n© 2026 Steve Manke. All rights reserved.")));
    }
}
