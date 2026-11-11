using System.Text.Json;
using InnerVoice.Core;

namespace InnerVoice.App;

/// What InnerVoice has done for the narrator, per day, for Settings › Activity. Numbers only,
/// never anything from a book. Kept in activity.json; test runs count in memory only.
public static class Activity
{
    public static ActivityLog Log { get; private set; } = Load();
    static CancellationTokenSource? saveSoon;
    static string FilePath => Path.Combine(AppPaths.Support, "activity.json");

    static ActivityLog Load()
    {
        if (AppPaths.IsTestRun) return new ActivityLog();
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<ActivityLog>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }
        return new ActivityLog();
    }

    public static void Record(string key, int count = 1)
    {
        if (count <= 0) return;
        Log.Record(key, count);
        ScheduleSave();
    }

    /// The narrator reviewed lines the rules or the AI had assigned: kept or changed, and
    /// for AI lines, per model.
    public static void RecordReview(IEnumerable<Line> before, Guid? newSpeaker)
    {
        foreach (var line in before)
        {
            switch (line.Source)
            {
                case AssignmentSource.None: Record(ActivityKey.LinesByHand); break;
                case AssignmentSource.User: break;
                default:
                    bool kept = line.SpeakerID is not null && line.SpeakerID == newSpeaker;
                    Record(kept ? ActivityKey.GuessesKept : ActivityKey.GuessesChanged);
                    if (line.Source == AssignmentSource.Ai && line.AiModel is { } m) Record(kept ? ActivityKey.AiKept(m) : ActivityKey.AiChanged(m));
                    break;
            }
        }
    }

    static void ScheduleSave()
    {
        if (AppPaths.IsTestRun) return;
        saveSoon?.Cancel();
        var cts = saveSoon = new CancellationTokenSource();
        _ = Task.Delay(2000, cts.Token).ContinueWith(t =>
        {
            if (t.IsCanceled) return;
            try
            {
                Log.Trim();
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Log));
            }
            catch { }
        }, TaskScheduler.Default);
    }
}
