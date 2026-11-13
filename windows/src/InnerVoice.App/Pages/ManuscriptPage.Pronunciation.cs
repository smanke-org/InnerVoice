// Fix Pronunciation… in the manuscript's right-click menu (the Mac's ManuscriptTextView
// `fixPronunciation`): opens Pronunciations on the word's entry, adding one when the guide
// doesn't have it. Books made with AI voices only.

using Avalonia;
using InnerVoice.Core;

namespace InnerVoice.App.Pages;

public sealed partial class ManuscriptPage
{
    /// The word to fix: a selection of up to three words in one paragraph, else the word
    /// clicked, with the paragraph it's in.
    (string Word, Guid Paragraph)? PronunciationWord(Point point, List<(Paragraph? Paragraph, int Chapter, TextSpan Range)> selection)
    {
        if (selection.Count > 0)
        {
            if (selection.Count != 1 || selection[0].Paragraph is not { } para) return null;
            return PronunciationFinder.WordToFix(para.Text, selection[0].Range.Location, selection[0].Range) is { } w ? (w, para.Id) : null;
        }
        if (Canvas.TextPositionAt(point) is not { } hit) return null;
        return PronunciationFinder.WordToFix(hit.Paragraph.Text, hit.Position) is { } word ? (word, hit.Paragraph.Id) : null;
    }

    /// Open Pronunciations on the word's entry: one with the same term (any capitals, or the
    /// term without a plural s), brought back if it was dismissed, or a new one of the narrator's.
    public void FixPronunciation(string word, Guid paragraph)
    {
        var all = Document.Project.Pronunciations;
        var existing = all.FirstOrDefault(p => string.Equals(p.Term, word, StringComparison.OrdinalIgnoreCase))
            ?? (word.EndsWith('s') || word.EndsWith('S')
                ? all.FirstOrDefault(p => string.Equals(p.Term, word[..^1], StringComparison.OrdinalIgnoreCase))
                : null);
        Guid id;
        if (existing is not null)
        {
            id = existing.Id;
            if (existing.Dismissed)
                Document.Change("Fix Pronunciation", p => { if (p.Pronunciations.FirstOrDefault(x => x.Id == id) is { } x) x.Dismissed = false; });
        }
        else
        {
            var entry = new Pronunciation { Term = word, Occurrences = 1, FirstParagraphID = paragraph, AddedByUser = true };
            id = entry.Id;
            Document.Change("Fix Pronunciation", p => p.Pronunciations.Add(entry.Copy()));
        }
        Workspace.FocusPronunciation = id;
        Workspace.Go(PageKind.Pronunciations);
    }
}
