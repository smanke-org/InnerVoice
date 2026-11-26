// Ported from Sources/InnerVoiceKit/ElevenLabsModels.swift.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InnerVoice.Core;

/// An ElevenLabs text-to-speech model the narrator can choose (Settings › Voices ›
/// ElevenLabs). The list comes from the account (`GET /v1/models`), so newer models show
/// up on their own; `Known` stands in until the first fetch.
/// `CreditsPerCharacter` is ElevenLabs' `character_cost_multiplier` (1 for most models, 0.5
/// for Flash and Turbo); `MaxCharacters` is the longest text one request may carry.
public sealed record ElevenLabsModel(string Id, string Name, string Detail = "", double CreditsPerCharacter = 1, int MaxCharacters = 2000)
{
    public const string DefaultId = "eleven_v4";

    /// The model the narrator chose, for the audio cache key (which lives in the Core). The
    /// app keeps it current (`ElevenLabsEngine.ModelID`); Eleven v4 until then.
    public static string ActiveId { get; set; } = DefaultId;

    static readonly Regex VersionPrefix = new(@"^eleven_v(\d+)", RegexOptions.CultureInvariant);
    static readonly Regex VersionExact = new(@"^eleven_v(\d+)$", RegexOptions.CultureInvariant);

    /// Audio tags ([angry], [whispers], [sighs]) work only in the v3 family and later; other
    /// models would read them aloud, so they're left out.
    public bool SupportsAudioTags => SupportsAudioTagsFor(Id);

    public static bool SupportsAudioTagsFor(string id) =>
        VersionPrefix.Match(id) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var n) && n >= 3;

    /// Credits for `characters` characters of text.
    public long Credits(long characters) => (long)Math.Ceiling(characters * CreditsPerCharacter);

    /// "1 credit per character" / "0.5 credits per character".
    public string RateText
    {
        get
        {
            var n = CreditsPerCharacter == Math.Round(CreditsPerCharacter)
                ? ((long)CreditsPerCharacter).ToString(CultureInfo.InvariantCulture)
                : CreditsPerCharacter.ToString("R", CultureInfo.InvariantCulture);
            return $"{n} credit{(CreditsPerCharacter == 1 ? "" : "s")} per character";
        }
    }

    /// Before the first fetch (and if it fails).
    public static IReadOnlyList<ElevenLabsModel> Known { get; } =
    [
        new("eleven_v4", "Eleven v4", "Most expressive; understands tones and sounds", 1, 5000),
        new("eleven_v3", "Eleven v3", "Expressive; understands tones and sounds", 1, 5000),
        new("eleven_multilingual_v2", "Eleven Multilingual v2", "Steady and natural; no tones or sounds", 1, 10000),
        new("eleven_turbo_v2_5", "Eleven Turbo v2.5", "Faster and half the credits; no tones or sounds", 0.5, 40000),
        new("eleven_flash_v2_5", "Eleven Flash v2.5", "Fastest and half the credits; no tones or sounds", 0.5, 40000),
    ];

    /// The account's text-to-speech models from `GET /v1/models`, newest generation first
    /// (v-numbered models by number, then the rest as ElevenLabs lists them). null when the
    /// answer isn't a model list or has no text-to-speech model.
    public static List<ElevenLabsModel>? Parse(byte[] data)
    {
        JsonArray? list;
        try { list = JsonNode.Parse(data) as JsonArray; } catch (JsonException) { return null; }
        if (list is null) return null;
        var models = new List<ElevenLabsModel>();
        foreach (var node in list)
        {
            if (node is not JsonObject o || Text(o, "model_id") is not { } id) return null;
            if (!(Bool(o, "can_do_text_to_speech") ?? false)) continue;
            var fallback = Known.FirstOrDefault(k => k.Id == id);
            var detail = fallback?.Detail ?? (SupportsAudioTagsFor(id) ? "Understands tones and sounds" : "No tones or sounds");
            var rate = (o["model_rates"] as JsonObject) is { } rates ? Number(rates, "character_cost_multiplier") : null;
            var max = Number(o, "maximum_text_length_per_request");
            models.Add(new ElevenLabsModel(id, Text(o, "name") ?? fallback?.Name ?? id, detail,
                                           rate ?? fallback?.CreditsPerCharacter ?? 1,
                                           max is { } m ? (int)m : fallback?.MaxCharacters ?? 2000));
        }
        // OrderByDescending is stable, so models of the same generation keep ElevenLabs' order.
        var sorted = models.OrderByDescending(m => Generation(m.Id)).ToList();
        return sorted.Count == 0 ? null : sorted;
    }

    static long Generation(string id) =>
        VersionExact.Match(id) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var n) ? n : 0;

    static string? Text(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static bool? Bool(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    static double? Number(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
}
