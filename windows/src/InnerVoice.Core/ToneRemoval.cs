// Ported from Sources/InnerVoiceKit/ToneRemoval.swift. Book > Remove Tones…: clears tones by
// who set them, so a book the AI tagged with more feeling than wanted can go back to even
// reading (or be tagged again).

namespace InnerVoice.Core;

public readonly record struct ToneRemoval(bool Automatic, bool Narrator)
{
    public bool IsEmpty => !Automatic && !Narrator;

    static bool HasTone(Line l) => l.Tone is not null || l.ToneSource is not null;

    public bool Matches(Line line)
    {
        if (!HasTone(line)) return false;
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

    /// Clears the chosen tones and returns how many lines changed. Clearing the automatic
    /// tones also forgets when they were tagged, so Steps offers Tag Speakers again.
    public int Apply(Project p)
    {
        int changed = 0;
        foreach (var l in p.Lines.Where(Matches))
        {
            l.Tone = null;
            l.ToneConfidence = null;
            l.ToneSource = null;
            changed++;
        }
        if (Automatic) p.TonesTagged = null;
        return changed;
    }
}
