// Ported from the Mac's Speech/ServiceCatalogs.swift.

using Avalonia.Threading;
using InnerVoice.Core;

namespace InnerVoice.App.Speech;

/// Keeps each cloud voice service's models and voices current from its own API: at launch
/// when a day has passed, once a day while the app runs, and on Refresh in Settings ›
/// Voices. Only set-up services are asked, and only for lists (no book text is sent). A
/// refresh never changes the model the narrator chose; if it disappears, Settings says so
/// and suggests the closest one.
public sealed class ServiceCatalogs
{
    public static ServiceCatalogs Shared { get; } = new();

    readonly Dictionary<CatalogService, CatalogState> states = [];
    readonly HashSet<CatalogService> refreshing = [];
    readonly Dictionary<CatalogService, string> failures = [];
    bool started;

    public event Action? Changed;

    ServiceCatalogs()
    {
        if (AppPaths.IsTestRun) return;
        foreach (var (k, v) in AppSettings.Current.CatalogStates ?? [])
            if (Enum.TryParse<CatalogService>(k, true, out var s)) states[s] = v;
    }

    public CatalogState State(CatalogService s) => states.TryGetValue(s, out var v) ? v : new CatalogState();
    public bool IsRefreshing(CatalogService s) => refreshing.Contains(s);
    public string? Failure(CatalogService s) => failures.GetValueOrDefault(s);

    public static bool IsSetUp(CatalogService s) => s switch
    {
        CatalogService.Elevenlabs => ElevenLabsEngine.Shared.IsSetUp,
        CatalogService.Speechify => SpeechifyVoices.Shared.IsSetUp,
        _ => GeminiVoices.Shared.IsSetUp,
    };

    /// The model this PC uses; Speechify has one model its API doesn't list.
    public static string? ChosenModel(CatalogService s) => s switch
    {
        CatalogService.Elevenlabs => ElevenLabsEngine.Shared.ModelID,
        CatalogService.Gemini => GeminiVoices.Shared.ModelID,
        _ => null,
    };

    public string? RetiredWarning(CatalogService s)
    {
        if (!IsSetUp(s) || ChosenModel(s) is not { } chosen || !State(s).IsRetired(chosen)) return null;
        string Name(string id) => s == CatalogService.Elevenlabs
            ? ElevenLabsEngine.Shared.Models.FirstOrDefault(m => m.Id == id)?.Name ?? id
            : GeminiTTS.TitleFor(id);
        var text = $"{Name(chosen)} is no longer offered by {s.Title()}, so it may stop working. Choose another model";
        if (ServiceCatalog.Suggestion(chosen, State(s).Models) is { } next) text += $"; {Name(next)} is closest";
        return text + ".";
    }

    /// Starts the daily refresh (not in test runs).
    public void Start()
    {
        if (AppPaths.IsTestRun || started) return;
        started = true;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Dispatcher.UIThread.InvokeAsync(() => RefreshAll(force: false));
                await Task.Delay(TimeSpan.FromHours(1));
            }
        });
    }

    public async Task RefreshAll(bool force)
    {
        foreach (var s in Enum.GetValues<CatalogService>())
            if (IsSetUp(s) && (force || ServiceCatalog.IsDue(states.GetValueOrDefault(s), DateTime.UtcNow)))
                await Refresh(s);
    }

    public async Task Refresh(CatalogService s)
    {
        if (!IsSetUp(s) || !refreshing.Add(s)) return;
        Notify();
        try
        {
            var (models, voices) = await Fetch(s);
            var st = State(s);
            st.Record(models, voices, DateTime.UtcNow);
            states[s] = st;
            failures.Remove(s);
            Save();
        }
        catch (Exception e) { failures[s] = e.Message; }
        finally { refreshing.Remove(s); Notify(); }
    }

    public void MarkSeen(CatalogService s)
    {
        if (!states.TryGetValue(s, out var st) || st.NewSummary() is null) return;
        st.MarkSeen();
        Save();
        Notify();
    }

    static async Task<(IReadOnlyList<string>? Models, IReadOnlyList<string>? Voices)> Fetch(CatalogService s)
    {
        switch (s)
        {
            case CatalogService.Elevenlabs:
                var eleven = ElevenLabsEngine.Shared;
                await eleven.RefreshModels();
                var voices = await eleven.RefreshVoices();
                return (eleven.Models.Select(m => m.Id).ToList(), voices.Select(v => v.VoiceID).ToList());
            case CatalogService.Speechify:
                var speechify = SpeechifyVoices.Shared;
                await speechify.RefreshVoices();
                return (null, speechify.Voices.Select(v => v.VoiceID).ToList());
            default:
                // Google has no voice list to read; its 30 voices are built in.
                return (await GeminiVoices.Shared.FetchModels(), null);
        }
    }

    void Save()
    {
        if (AppPaths.IsTestRun) return;
        var copy = states.ToDictionary(kv => kv.Key.ToString().ToLowerInvariant(), kv => kv.Value);
        AppSettings.UpdateQuietly(s => s.CatalogStates = copy);
    }

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());
}
