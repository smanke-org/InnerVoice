using Avalonia.Controls;
using InnerVoice.App.Pages;
using Page = InnerVoice.App.Pages.Page;
using InnerVoice.App.UI;

namespace InnerVoice.App.Windows;

/// Pages and commands that arrive milestone by milestone.
public sealed partial class ProjectWindow
{
    IEnumerable<PageKind> VisiblePages()
    {
        yield return PageKind.Manuscript;
        yield return PageKind.Cast;
        yield return PageKind.Record;
        yield return PageKind.Retakes;
    }

    Page MakeOtherPage(PageKind kind) => new ManuscriptPage(Document, Workspace);

    void AddFileItems(MenuItem file) { }

    void AddStepsMenu(MenuItem view) { }

    void AddBookMenu(Menu menu) { }

    void RefreshSteps() { }

    void AddHelpItems(MenuItem help)
    {
        help.Items.Add(Item("_About InnerVoice", null, () => _ = Dialogs.Alert(this, "InnerVoice for Windows",
            $"Version {App.Version}\n© 2026 Steve Manke. All rights reserved.")));
    }
}
