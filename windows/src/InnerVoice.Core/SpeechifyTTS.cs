// Ported from Sources/InnerVoiceKit/SpeechifyTTS.swift.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace InnerVoice.Core;

/// Speechify's text-to-speech API (api.speechify.ai, the third cloud voice engine): request and
/// response shapes, the voice list, and how a tone becomes SSML. Pure logic, so the app and the
/// tests share it; the app's `SpeechifyVoices` sends the requests, only for a book that allows
/// Speechify.
///
/// The current models (Simba 3.x) ignore Speechify's emotion tag, so a tone is acted with SSML
/// prosody (pace, pitch, loudness) instead; delivery hints and vocal sounds aren't sent.
public static class SpeechifyTTS
{
    public const string Host = "https://api.speechify.ai/v1/";
    /// The recommended model (48 languages).
    public const string Model = "simba-3.5";
    /// /v1/audio/speech takes up to 2,000 spoken characters a request.
    public const int ChunkLimit = 1800;

    /// Dollars per million characters for Simba 3.5 at the Starter plan's rate (speechify.ai,
    /// November 2026: $20; 3.0 and 3.2 are $10). Only for the export's estimate.
    public const double DollarsPerMillionCharacters = 20.0;

    public static double EstimatedDollars(long characters) => characters * DollarsPerMillionCharacters / 1_000_000;

    // MARK: Voices

    /// The GET /v1/voices URL for one page of shared English voices (cursor: the page after).
    public static string VoicesUrl(string? cursor = null) =>
        Host + "voices?type=shared&locale=en&limit=200" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

    /// One page of GET /v1/voices: the shared (ready-made) English voices, and the cursor for
    /// the next page (null on the last). Personal voices (clones) are left out: they're never cast.
    public static (List<VoiceInfo> Voices, string? Next) VoicesFromResponse(byte[] data)
    {
        if (JsonNode.Parse(data) is not JsonObject root || root["voices"] is not JsonArray list)
            throw new JsonException("No voice list in the reply.");
        var voices = new List<VoiceInfo>();
        foreach (var v in list.OfType<JsonObject>())
        {
            if ((Str(v, "type") ?? "shared") != "shared") continue;
            if (Str(v, "id") is not { } id || Str(v, "display_name") is not { } name) throw new JsonException("A voice without an id or name.");
            var rawTags = (v["tags"] as JsonArray ?? []).Select(t => t is JsonValue tv && tv.TryGetValue<string>(out var s) ? s : null)
                .OfType<string>().ToList();
            var tags = rawTags.Select(t => t.ToLowerInvariant()).ToList();
            var age = tags.Any(t => t.Contains("child") || t.Contains("kid")) ? VoiceAge.Child
                : tags.Any(t => t.Contains("old") || t.Contains("senior") || t.Contains("elder")) ? VoiceAge.Older
                : tags.Any(t => t.Contains("young") || t.Contains("teen")) ? VoiceAge.Young : VoiceAge.Adult;
            var detail = rawTags.Where(t => !t.Contains(':')).Take(3).Select(t => t.Length == 0 ? t : t[..1].ToUpperInvariant() + t[1..]);
            var gender = Str(v, "gender") switch { "female" => Gender.Female, "male" => Gender.Male, _ => Gender.Neutral };
            bool staffPick = v["staff_pick_rank"] is JsonValue rank && rank.GetValueKind() == JsonValueKind.Number;
            voices.Add(new VoiceInfo(VoiceEngine.Speechify, id, name, gender, age, Accent(Str(v, "locale") ?? ""),
                staffPick ? 2 : 1, string.Join(", ", detail)));
        }
        bool hasMore = root["has_more"] is JsonValue hm && hm.TryGetValue<bool>(out var b) && b;
        return (voices, hasMore ? Str(root, "next_cursor") : null);
    }

    /// The voices from every saved page, staff picks first, then by name (as the Mac lists them).
    public static List<VoiceInfo> VoicesFromPages(IEnumerable<byte[]> pages) =>
        pages.SelectMany(p =>
        {
            try { return VoicesFromResponse(p).Voices; }
            catch (JsonException) { return new List<VoiceInfo>(); }
        }).OrderByDescending(v => v.Quality).ThenBy(v => v.Name, StringComparer.Ordinal).ToList();

    /// "en-GB" → "British".
    public static string Accent(string locale)
    {
        var region = locale.Split('-').Skip(1).FirstOrDefault()?.ToUpperInvariant() ?? "";
        return region switch
        {
            "US" => "American", "GB" => "British", "AU" => "Australian", "CA" => "Canadian", "IE" => "Irish",
            "IN" => "Indian", "NZ" => "New Zealand", "ZA" => "South African", "SG" => "Singaporean", "PH" => "Filipino",
            _ => locale.Length == 0 ? "English" : locale,
        };
    }

    // MARK: Speech

    /// How a tone is acted with SSML prosody (rate %, pitch %, volume); null for even reading.
    public static (int Rate, int Pitch, string? Volume)? Prosody(Tone? tone, LineKind? kind)
    {
        if (kind == LineKind.Thought && (tone is null || tone == Tone.Neutral)) return (-3, 0, "soft");
        return tone switch
        {
            Tone.Excited => (8, 6, "loud"),
            Tone.Confused => (-5, 2, (string?)null),
            Tone.Sad => (-10, -5, "soft"),
            Tone.Angry => (5, 3, "loud"),
            Tone.Whispered => (-8, -2, "x-soft"),
            Tone.Scared => (7, 5, (string?)null),
            Tone.Tender => (-7, -2, "soft"),
            Tone.Sarcastic => (-4, -1, (string?)null),
            _ => ((int, int, string?)?)null,
        };
    }

    /// The text as SSML: escaped, with the tone's prosody around it. Narration (kind null) is
    /// always read evenly.
    public static string Ssml(string text, Tone? tone, LineKind? kind)
    {
        var escaped = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        if (kind is null || Prosody(tone, kind) is not { } p) return $"<speak>{escaped}</speak>";
        static string Signed(int n) => n >= 0 ? $"+{n}%" : $"{n}%";
        var volume = p.Volume is { } v ? $" volume=\"{v}\"" : "";
        return $"<speak><prosody rate=\"{Signed(p.Rate)}\" pitch=\"{Signed(p.Pitch)}\"{volume}>{escaped}</prosody></speak>";
    }

    /// The JSON for POST /v1/audio/speech: WAV at 24 kHz. Keys sorted like the Mac's.
    public static byte[] RequestBody(string text, string voice, Tone? tone, LineKind? kind)
    {
        var body = new JsonObject
        {
            ["audio_format"] = "wav",
            ["input"] = Ssml(text, tone, kind),
            ["model"] = Model,
            ["output_format"] = "wav_24000",
            ["voice_id"] = voice,
        };
        return JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// A reply without audio.
    public sealed class NoAudioException() : Exception("No audio in the reply.") { }

    /// The audio file from a reply ({"audio_data": base64, "billable_characters_count": n}).
    public static (byte[] Audio, int? Billable) AudioFromResponse(byte[] data)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(data); } catch (JsonException) { throw new NoAudioException(); }
        if (root is not JsonObject obj || Str(obj, "audio_data") is not { } b64) throw new NoAudioException();
        byte[] audio;
        try { audio = Convert.FromBase64String(b64); } catch (FormatException) { throw new NoAudioException(); }
        if (audio.Length == 0) throw new NoAudioException();
        int? billable = obj["billable_characters_count"] is JsonValue n && n.TryGetValue<int>(out var c) ? c : null;
        return (audio, billable);
    }

    /// Speechify's error from an error reply ({"error": {"code", "message"}}).
    public static (string Code, string Message)? ErrorMessage(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is JsonObject root && root["error"] is JsonObject e)
                return (Str(e, "code") ?? "", Str(e, "message") ?? "");
        }
        catch (JsonException) { }
        return null;
    }

    /// What an error reply means for the narrator.
    public enum Failure { BadKey, OutOfCharacters, Busy, Other }

    /// 401 or "unauthorized": the key; 402 or "payment_required": no characters left; 429 and
    /// 502–504: busy; anything else: Speechify's own message (as the Mac's throwIfFailed).
    public static Failure Classify(int status, string code) => (status, code) switch
    {
        (401, _) or (_, "unauthorized") => Failure.BadKey,
        (402, _) or (_, "payment_required") => Failure.OutOfCharacters,
        (429, _) or (>= 502 and <= 504, _) => Failure.Busy,
        _ => Failure.Other,
    };

    static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
