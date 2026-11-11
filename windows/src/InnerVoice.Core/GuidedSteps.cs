// Ported from Sources/InnerVoiceKit/GuidedSteps.swift (the Narration Tools steps).

namespace InnerVoice.Core;

/// The steps from a manuscript to a finished book, in order. The first one not done is the
/// current step. Nothing is locked: the narrator can skip a step or go back to any of them.
public enum GuidedStep { Manuscript, Speakers, Review, Characters, Pronunciations, Record }

public sealed record GuidedStepStatus(GuidedStep Step, int Number, bool IsDone, bool IsMarked, bool IsSkipped, string Detail);

public sealed class GuidedSteps
{
    public static readonly GuidedStep[] Narration =
        [GuidedStep.Manuscript, GuidedStep.Speakers, GuidedStep.Review, GuidedStep.Characters, GuidedStep.Pronunciations, GuidedStep.Record];

    public List<GuidedStepStatus> All { get; }
    public GuidedStepStatus? Current => All.FirstOrDefault(s => !s.IsDone);
    public bool AllDone => Current is null;

    /// The raw value the Mac stores in `stepsDone` ("manuscript", "speakers", …).
    public static string RawValue(GuidedStep s) => s.ToString().ToLowerInvariant();

    public static string Title(GuidedStep s) => s.ToString();

    public static bool IsLast(GuidedStep s) => s == GuidedStep.Record;

    public GuidedSteps(Project p, int speaking)
    {
        var marked = (p.Settings.StepsDone ?? []).ToHashSet();
        bool tagged = p.LastTagged is not null;
        int needing = p.LinesNeedingYou;
        int paragraphs = p.Chapters.Sum(c => c.Paragraphs.Count);
        var terms = p.Pronunciations.Where(t => !t.Dismissed).ToList();
        int unsaid = terms.Count(t => t.SayItAs.Trim().Length == 0);
        static string Plural(int n, string one, string many) => $"{n:N0} {(n == 1 ? one : many)}";

        All = Narration.Select((step, i) =>
        {
            bool isMarked = marked.Contains(RawValue(step));
            bool done;
            string detail;
            switch (step)
            {
                case GuidedStep.Manuscript:
                    done = tagged; detail = Plural(p.Chapters.Count, "chapter", "chapters"); break;
                case GuidedStep.Speakers:
                    done = tagged; detail = tagged ? Plural(p.Lines.Count, "line", "lines") : Plural(p.Lines.Count, "line found", "lines found"); break;
                case GuidedStep.Review:
                    done = tagged && needing == 0; detail = !tagged ? "After tagging" : needing == 0 ? "All confirmed" : $"{needing:N0} to review"; break;
                case GuidedStep.Characters:
                    done = p.SummariesBuilt is not null; detail = done ? Plural(speaking, "character", "characters") : "Not read yet"; break;
                case GuidedStep.Pronunciations:
                    done = terms.Count > 0 && unsaid == 0; detail = terms.Count == 0 ? "Names and places" : unsaid == 0 ? "All added" : $"{unsaid:N0} to add"; break;
                default:
                    done = paragraphs > 0 && p.Recorded.Count >= paragraphs; detail = $"{Math.Min(p.Recorded.Count, paragraphs):N0} of {paragraphs:N0} recorded"; break;
            }
            return new GuidedStepStatus(step, i + 1, done || isMarked, isMarked, isMarked && !done && !IsLast(step), detail);
        }).ToList();
    }

    /// Marks a step done (or skipped), or takes the mark off.
    public static void MarkStep(ProjectSettings s, GuidedStep step, bool done)
    {
        var marked = s.StepsDone ?? [];
        marked.RemoveAll(x => x == RawValue(step));
        if (done) marked.Add(RawValue(step));
        s.StepsDone = marked.Count == 0 ? null : marked;
    }
}
