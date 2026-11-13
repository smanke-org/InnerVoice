using Avalonia.Controls;
using InnerVoice.App.Document;

namespace InnerVoice.App.Pages;

public enum PageKind { Manuscript, Cast, Index, Teleprompter, Review, Revisions }

public static class PageKinds
{
    public static string Title(this PageKind p) => p switch
    {
        PageKind.Manuscript => "Manuscript",
        PageKind.Cast => "Cast",
        PageKind.Index => "Index",
        PageKind.Teleprompter => "Teleprompter",
        PageKind.Review => "Review",
        _ => "Revisions",
    };

    /// Ctrl+1 … Ctrl+4.
    public static readonly PageKind[] Numbered = [PageKind.Manuscript, PageKind.Cast, PageKind.Index, PageKind.Teleprompter];
}

/// Per-window state that is not part of the book (the Mac's Workspace).
public sealed class Workspace
{
    public PageKind Page { get; set; } = PageKind.Manuscript;
    /// The paragraph the Teleprompter shows (null: where the narrator left off in this book).
    public Guid? TeleprompterParagraph { get; set; }
    public Guid? SelectedCharacter { get; set; }
    public event Action? PageChanged;
    public event Action<Guid>? ShowParagraphRequested;
    public event Action<int>? ShowChapterRequested;
    public event Action<Guid>? SelectLineRequested;
    public event Action? TagRequested;

    public void RequestTag() => TagRequested?.Invoke();

    public void Go(PageKind page)
    {
        Page = page;
        PageChanged?.Invoke();
    }

    /// Shows a paragraph in the manuscript.
    public void Show(Guid paragraphID)
    {
        Go(PageKind.Manuscript);
        ShowParagraphRequested?.Invoke(paragraphID);
    }

    /// Shows a line in the manuscript, selected (the agent's `lines reveal`).
    public void ShowLine(Guid paragraphID, Guid lineID)
    {
        Show(paragraphID);
        SelectLineRequested?.Invoke(lineID);
    }

    public void ShowChapter(int chapter)
    {
        Go(PageKind.Manuscript);
        ShowChapterRequested?.Invoke(chapter);
    }
}

/// One page of a project window.
public abstract class Page : UserControl
{
    protected Page(ProjectDocument document, Workspace workspace)
    {
        Document = document;
        Workspace = workspace;
    }

    public ProjectDocument Document { get; }
    public Workspace Workspace { get; }
    public abstract PageKind Kind { get; }
    public virtual string Subtitle => "";
    /// Controls for the top bar, right of the title.
    public virtual Control? Toolbar => null;

    /// The book changed; redraw what depends on it.
    public abstract void Refresh(bool textChanged);
    public virtual void Shown() { }
    public virtual void Hidden() { }
}
