// Ported from Sources/InnerVoiceKit/ToneRemoval.swift. Book > Remove Tones…: clears tones by
// who set them, so a book the AI tagged with more feeling than wanted can go back to even
// reading (or be tagged again). Tones in Keep stay on their lines (a narrator who likes
// Sarcastic but finds the rest too dramatic).

namespace InnerVoice.Core;

public readonly record struct ToneRemoval
{
    public bool Automatic { get; init; }
    public bool Narrator { get; init; }
    /// Tones that stay, whoever set them. Neutral is never kept: clearing it changes nothing you hear.
    public IReadOnlySet<Tone> Keep { get; init; }

    public ToneRemoval(bool automatic, bool narrator, IEnumerable<Tone>? keep = null)
    {
        Automatic = automatic;
        Narrator = narrator;
        Keep = (keep ?? []).Where(t => t != Tone.Neutral).ToHashSet();
    }

    public bool IsEmpty => !Automatic && !Narrator;

    static bool HasTone(Line l) => l.Tone is not null || l.ToneSource is not null;

    public bool Matches(Line line)
    {
        if (!HasTone(line)) return false;
        if (Keep?.Contains(line.Tone ?? Tone.Neutral) == true) return false;
        return line.ToneSource == ToneSource.User ? Narrator : Automatic;
    }

    /// How many lines each choice would clear: tones Tag Speakers set (the AI's and the rules')
    /// and tones the narrator chose.
    public static (int Automatic, int Narrator) Counts(Project p)
    {
        int a = 0, n = 0;
        foreach (var l in p.Lines.Where(HasTone))
        {
            if (l.ToneSource == ToneSource.User) n++; else a++;
        }
        return (a, n);
    }

    /// How many lines have each tone (any source), for the list of tones to keep.
    public static Dictionary<Tone, int> ToneCounts(Project p)
    {
        var counts = new Dictionary<Tone, int>();
        foreach (var l in p.Lines.Where(HasTone))
        {
            var t = l.Tone ?? Tone.Neutral;
            counts[t] = counts.GetValueOrDefault(t) + 1;
        }
        return counts;
    }

    /// Clears the chosen tones and returns how many lines changed. Clearing all of Tag
    /// Speakers' tones also forgets when they were tagged, so Steps offers Tag Speakers again;
    /// keeping some doesn't.
    public int Apply(Project p)
    {
        int changed = 0;
        foreach (var l in p.Lines.Where(Matches).ToList())
        {
            l.Tone = null;
            l.ToneConfidence = null;
            l.ToneSource = null;
            changed++;
        }
        if (Automatic && (Keep?.Count ?? 0) == 0) p.TonesTagged = null;
        return changed;
    }
}
