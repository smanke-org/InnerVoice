// Ported from Sources/InnerVoiceKit/GuidedSteps.swift (the Narration Tools steps).

namespace InnerVoice.Core;

/// The steps from a manuscript to a finished book, in order. The first one not done is the
/// current step. Nothing is locked: the narrator can skip a step or go back to any of them.
public enum GuidedStep { Manuscript, Speakers, Review, Characters, Teleprompter }

public sealed record GuidedStepStatus(GuidedStep Step, int Number, bool IsDone, bool IsMarked, bool IsSkipped, string Detail);

public sealed class GuidedSteps
{
    public static readonly GuidedStep[] Narration =
        [GuidedStep.Manuscript, GuidedStep.Speakers, GuidedStep.Review, GuidedStep.Characters, GuidedStep.Teleprompter];

    public List<GuidedStepStatus> All { get; }
    public GuidedStepStatus? Current => All.FirstOrDefault(s => !s.IsDone);
    public bool AllDone => Current is null;

    /// The raw value the Mac stores in `stepsDone` ("manuscript", "speakers", …). The
    /// Teleprompter is stored as "record" (its old name), so a step marked done in an older
    /// version stays done.
    public static string RawValue(GuidedStep s) => s == GuidedStep.Teleprompter ? "record" : s.ToString().ToLowerInvariant();

    public static string Title(GuidedStep s) => s.ToString();

    /// The last step has no end of its own: it's done when the narrator says so.
    public static bool IsLast(GuidedStep s) => s == GuidedStep.Teleprompter;

    public GuidedSteps(Project p, int speaking)
    {
        var marked = (p.Settings.StepsDone ?? []).ToHashSet();
        bool tagged = p.LastTagged is not null;
        int needing = p.LinesNeedingYou;
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
                default:
                    // InnerVoice doesn't record: reading in the booth is done when the narrator says so.
                    done = false; detail = "Read in the booth"; break;
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
