using System.Text.Json;
using Avalonia.Threading;
using InnerVoice.Core;

namespace InnerVoice.App;

/// Cloud usage on this PC (Claude tokens, ElevenLabs characters), saved in
/// %LOCALAPPDATA%\InnerVoice\Cloud Usage.json. Numbers only, never book text. Test runs keep
/// it in memory. The Mac's CloudUsageStore.
public sealed class CloudUsageStore
{
    public static CloudUsageStore Shared { get; } = new();
    public event Action? Changed;

    readonly List<CloudUsageRecord> records = [];
    /// Claude's per-minute token allowance left after the last request (from Anthropic's
    /// rate-limit headers), per model.
    readonly Dictionary<string, long> claudeTokensLeft = [];
    DispatcherTimer? saveTimer;

    public IReadOnlyList<CloudUsageRecord> Records => records;
    public long? ClaudeTokensLeft(string model) => claudeTokensLeft.TryGetValue(model, out var n) ? n : null;

    static string? FilePath => AppPaths.IsTestRun ? null : Path.Combine(AppPaths.Support, "Cloud Usage.json");

    CloudUsageStore()
    {
        if (FilePath is not { } file || !File.Exists(file)) return;
        try
        {
            if (JsonSerializer.Deserialize<List<CloudUsageRecord>>(File.ReadAllText(file), ProjectJson.Options) is { } saved) records.AddRange(saved);
        }
        catch { }
    }

    /// Adds one request (call on the UI thread).
    public void Add(CloudUsageRecord record, long? claudeTokensLeftNow = null)
    {
        CloudUsage.Merge(record, records);
        if (claudeTokensLeftNow is { } left && record.Service == CloudService.Claude) claudeTokensLeft[record.Model] = left;
        Changed?.Invoke();
        ScheduleSave();
    }

    /// Test hook: replaces everything (snapshots seed made-up numbers).
    public void ReplaceForTesting(IEnumerable<CloudUsageRecord> list)
    {
        records.Clear();
        records.AddRange(list);
        claudeTokensLeft.Clear();
        Changed?.Invoke();
    }

    void ScheduleSave()
    {
        if (FilePath is null) return;
        saveTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => { saveTimer!.Stop(); Save(); });
        saveTimer.Stop();
        saveTimer.Start();
    }

    void Save()
    {
        if (FilePath is not { } file) return;
        try
        {
            var tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(records, ProjectJson.Options));
            File.Move(tmp, file, overwrite: true);
        }
        catch { }
    }
}
