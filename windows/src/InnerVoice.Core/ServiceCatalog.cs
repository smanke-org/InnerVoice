// Ported from Sources/InnerVoiceKit/ServiceCatalog.swift.

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InnerVoice.Core;

/// The cloud voice services whose models and voices are read from their own APIs. To add a
/// service: a case here and a branch in the app's `ServiceCatalogs.Fetch`.
public enum CatalogService { Elevenlabs, Speechify, Gemini }

/// One service's last refresh: what it listed, and what's new since the refresh before.
/// A refresh only updates the lists; the model the narrator chose never changes on its own.
public sealed class CatalogState
{
    public DateTime? LastRefreshed { get; set; }
    public List<string> Models { get; set; } = [];
    public List<string> Voices { get; set; } = [];
    public List<string> NewModels { get; set; } = [];
    public List<string> NewVoices { get; set; } = [];

    /// Records a refresh; null means that list wasn't fetched. The first refresh of a list
    /// marks nothing new.
    public void Record(IReadOnlyList<string>? models, IReadOnlyList<string>? voices, DateTime at)
    {
        if (models is not null)
        {
            if (LastRefreshed is not null && Models.Count > 0)
                NewModels = NewModels.Where(models.Contains).Concat(models.Where(m => !Models.Contains(m))).Distinct().ToList();
            Models = models.ToList();
        }
        if (voices is not null)
        {
            if (LastRefreshed is not null && Voices.Count > 0)
            {
                var old = Voices.ToHashSet();
                NewVoices = NewVoices.Where(voices.Contains).Concat(voices.Where(v => !old.Contains(v))).Distinct().ToList();
            }
            Voices = voices.ToList();
        }
        LastRefreshed = at;
    }

    public void MarkSeen() { NewModels = []; NewVoices = []; }

    /// "2 new models, 5 new voices", or null.
    public string? NewSummary()
    {
        var parts = new List<string>();
        if (NewModels.Count > 0) parts.Add($"{NewModels.Count} new model{(NewModels.Count == 1 ? "" : "s")}");
        if (NewVoices.Count > 0) parts.Add($"{NewVoices.Count} new voice{(NewVoices.Count == 1 ? "" : "s")}");
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// The chosen model is retired when a refresh listed models and it isn't among them.
    public bool IsRetired(string chosen) => LastRefreshed is not null && Models.Count > 0 && !Models.Contains(chosen);
}

public static class ServiceCatalog
{
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    public static string Title(this CatalogService s) => s switch
    {
        CatalogService.Elevenlabs => "ElevenLabs",
        CatalogService.Speechify => "Speechify",
        _ => "Gemini TTS",
    };

    public static bool IsDue(CatalogState? state, DateTime now) =>
        state?.LastRefreshed is not { } last || now - last >= Interval || now < last;

    /// The closest model to suggest for a retired one: the first sharing its family name,
    /// else the first listed.
    public static string? Suggestion(string retired, IReadOnlyList<string> models)
    {
        var family = Regex.Replace(retired, @"[_-]?v?\d[\w.]*$", "");
        return models.FirstOrDefault(m => family.Length > 0 && m.StartsWith(family, StringComparison.Ordinal)) ?? models.FirstOrDefault();
    }

    /// Google's text-to-speech model IDs from `GET v1beta/models`, in Google's order.
    public static List<string>? GeminiTTSModels(byte[] data)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(data); } catch (JsonException) { return null; }
        if (root is not JsonObject obj) return null;
        return (obj["models"] as JsonArray ?? [])
            .Select(m => m?["name"]?.GetValue<string>() ?? "")
            .Select(n => n.StartsWith("models/", StringComparison.Ordinal) ? n[7..] : n)
            .Where(id => id.EndsWith("-tts", StringComparison.Ordinal) || id.Contains("-tts-", StringComparison.Ordinal))
            .ToList();
    }
}
