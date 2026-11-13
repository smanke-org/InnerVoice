// Ported from Sources/InnerVoiceKit/AIReview.swift. Windows has no tones (they stay in each
// line's Extra), so only speakers are reviewed here; a Mac run's tone changes are untouched.

namespace InnerVoice.Core;

/// What a cloud AI run (Claude) changed on one line, for the narrator to keep or undo.
public sealed record AIReviewItem(Guid LineID, Guid? SpeakerBefore, Guid? SpeakerAfter)
{
    public bool SpeakerChanged => SpeakerBefore != SpeakerAfter;
}

public static class AIReview
{
    /// The lines the AI changed in a run, in book order. `before` holds each touched line as
    /// it was. Only changes the AI still owns are listed: the rules' and the narrator's aren't.
    public static List<AIReviewItem> Items(IReadOnlyDictionary<Guid, Line> before, Project project) =>
        project.Lines
            .Where(l => before.ContainsKey(l.Id))
            .Select(l => (Line: l, Item: new AIReviewItem(l.Id, before[l.Id].SpeakerID, l.SpeakerID)))
            .Where(x => x.Item.SpeakerChanged && x.Line.Source == AssignmentSource.Ai)
            .Select(x => x.Item)
            .ToList();

    /// Puts the listed lines back as they were before the run, where the AI still owns them,
    /// and removes characters the run added that no line uses any more.
    public static void Undo(IReadOnlySet<Guid> ids, IReadOnlyDictionary<Guid, Line> before, IReadOnlySet<Guid> addedCharacters, Project p)
    {
        foreach (var l in p.Lines)
        {
            if (!ids.Contains(l.Id) || !before.TryGetValue(l.Id, out var old) || l.Source != AssignmentSource.Ai) continue;
            l.SpeakerID = old.SpeakerID;
            l.Confidence = old.Confidence;
            l.Source = old.Source;
            l.Kind = old.Kind;
            l.AiModel = old.AiModel;
        }
        var used = p.Lines.Where(l => l.SpeakerID is not null).Select(l => l.SpeakerID!.Value).ToHashSet();
        p.Characters.RemoveAll(c => addedCharacters.Contains(c.Id) && !used.Contains(c.Id));
    }
}
