using InnerVoice.App.AI;
using InnerVoice.App.Pages;

namespace InnerVoice.App.Windows;

/// What the agent commands (Agent/AgentHandlers*.cs) need from a project window.
public sealed partial class ProjectWindow
{
    /// The window's id for `innervoice projects list` (the Mac's OpenProjects entry id).
    public Guid AgentID { get; } = Guid.NewGuid();

    /// The AI runner, when this window has run the AI (without making one).
    public AIRunner? ExistingRunner => runner;

    /// Opens a character on the Cast page.
    public void ShowCharacter(Guid id)
    {
        Workspace.Go(PageKind.Cast);
        (Get(PageKind.Cast) as CastPage)?.ShowCharacter(id);
    }

    /// The agent saved the project: the title shows the file's name.
    public void AgentSaved() => UpdateTitle();
}
