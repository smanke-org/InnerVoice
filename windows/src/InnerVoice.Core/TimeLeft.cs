// Ported from the Mac's Sources/InnerVoiceKit/TimeLeft.swift.

namespace InnerVoice.Core;

/// Export Audio's "about 12 min left": the time so far per line, times the lines to go.
/// Quiet until it has something to go on (5 lines and 15 seconds), so the first guess isn't
/// wild. Lines already in the saved audio come back at once, so an export that starts with
/// them guesses low at first and corrects as it goes.
public static class TimeLeft
{
    public static string? Estimate(int done, int total, double elapsedSeconds)
    {
        if (done < 5 || elapsedSeconds < 15 || total <= done) return null;
        return Text(elapsedSeconds / done * (total - done));
    }

    public static string Text(double seconds)
    {
        if (seconds < 60) return "less than a minute left";
        int minutes = (int)Math.Round(seconds / 60, MidpointRounding.AwayFromZero);
        if (minutes < 60) return $"about {minutes} min left";
        int h = minutes / 60, m = minutes % 60;
        return m == 0 ? $"about {h} hr left" : $"about {h} hr {m} min left";
    }

    /// ", about 9 min left…" after the progress, or "…" while it's quiet.
    public static string Ending(int done, int total, double elapsedSeconds) =>
        Estimate(done, total, elapsedSeconds) is { } left ? $", {left}…" : "…";
}
