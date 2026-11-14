// Play Line and Play from Here (the Mac's PlayCommand): the selected lines in their voices,
// or a table read from the selected line (or the top of the chapter) to the end of the book,
// following along in the manuscript.

using InnerVoice.App.Speech;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

public sealed partial class ManuscriptPage
{
    /// The selected lines, in book order, each in its character's voice.
    public void PlayLines(IReadOnlyCollection<Guid>? ids = null)
    {
        ids ??= Canvas.SelectedLines.ToList();
        if (ids.Count == 0 || !Document.Project.ShowsVoices) return;
        VoiceCastingRun.CastIfNeeded(Document);
        var project = Document.Project;
        var index = Document.Index;
        var wanted = ids.ToHashSet();
        var paragraphs = ids.Select(id => index.LineIndexByID.TryGetValue(id, out var i) ? project.Lines[i].ParagraphID : (Guid?)null)
            .OfType<Guid>().Distinct()
            .OrderBy(pid => index.Positions.TryGetValue(pid, out var pos) ? pos.Global : int.MaxValue);
        var segments = new List<SpeechSegment>();
        foreach (var pid in paragraphs)
            if (project.Paragraph(pid, index) is { } para)
                segments.AddRange(SpeechScript.Segments(para, project, index, includeNarration: false).Where(s => s.LineID is { } l && wanted.Contains(l)));
        SpeechPlayer.Shared.Play(segments, VoiceSetup.Resolver(Document));
    }

    /// From the line (or the selected line, or the top of the chapter in view) to the end of
    /// the book, as a table read: each line in its character's voice, narration as set in
    /// Settings › Voices.
    public void PlayFromHere(Guid? lineID = null, Guid? paragraphID = null)
    {
        if (!Document.Project.ShowsVoices) return;
        VoiceCastingRun.CastIfNeeded(Document);
        var project = Document.Project;
        var index = Document.Index;
        // A right-click names where to start (the line, or the top of the paragraph when the
        // click wasn't on a line); the menu item and shortcut use the selected line.
        if (lineID is null && paragraphID is null) lineID = Canvas.SelectedLines.Count > 0 ? Canvas.SelectedLines.First() : null;
        int chapter = Math.Clamp(Canvas.VisibleChapter, 0, Math.Max(0, project.Chapters.Count - 1));
        Guid? start = null;
        if (lineID is { } id && index.LineIndexByID.TryGetValue(id, out var li) && index.Positions.TryGetValue(project.Lines[li].ParagraphID, out var pos))
        {
            start = project.Lines[li].ParagraphID;
            chapter = pos.Chapter;
        }
        else if (paragraphID is { } pid && index.Positions.TryGetValue(pid, out var ppos))
        {
            start = pid;
            chapter = ppos.Chapter;
        }
        if (chapter >= project.Chapters.Count) return;
        var segments = SpeechScript.Segments(chapter, project, index, AppSettings.Current.ReadNarration, from: start,
                                             chapters: project.Chapters.Count - chapter);
        // Start at the line, not the narration before it in its paragraph.
        if (lineID is { } l2 && segments.FindIndex(s => s.LineID == l2) is var k and > 0) segments.RemoveRange(0, k);
        SpeechPlayer.Shared.Play(segments, VoiceSetup.Resolver(Document), seg =>
        {
            // Follow along in the manuscript.
            if (Workspace.Page != PageKind.Manuscript || !AppSettings.Current.FollowAlong) return;
            Canvas.SelectedLines.Clear();
            if (seg.LineID is { } sid) Canvas.SelectedLines.Add(sid);
            Canvas.ScrollToParagraph(seg.ParagraphID);
            Canvas.InvalidateVisual();
        });
    }
}
