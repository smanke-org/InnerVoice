// Ported from Sources/InnerVoiceKit/GeminiTTS.swift.

using System.Text.Json;
using System.Text.Json.Nodes;
using InnerVoice.Core.AI;

namespace InnerVoice.Core;

/// The two Gemini TTS models. Flash acts best; Flash-Lite costs about two thirds as much.
/// Raw values (`GeminiTTS.Raw`) match the Mac's `GeminiTTS.Model`.
public enum GeminiModel { Flash, FlashLite }

/// Google's Gemini TTS (the cloud engine "gemini"): its voices, how a line's tone becomes a
/// spoken direction, and the request/response JSON of the Interactions API
/// (`POST v1beta/interactions`, checked against Google's docs 2026-11-14). Pure logic, so the
/// app and the tests share it; the network call lives in the app (`GeminiVoices`).
public static class GeminiTTS
{
    public const string Host = "https://generativelanguage.googleapis.com/v1beta/";

    public static string Raw(this GeminiModel m) => m switch
    {
        GeminiModel.Flash => "gemini-3.8-flash-tts",
        _ => "gemini-3.8-flash-lite-tts",
    };

    public static string Title(this GeminiModel m) => m switch
    {
        GeminiModel.Flash => "Gemini 3.8 Flash TTS (most expressive)",
        _ => "Gemini 3.8 Flash-Lite TTS (lower cost)",
    };

    public static GeminiModel? ModelFrom(string? raw) =>
        Enum.GetValues<GeminiModel>().Cast<GeminiModel?>().FirstOrDefault(m => m!.Value.Raw() == raw);

    /// Google doubles its prices on 2027-01-01.
    static readonly DateTime PriceRise = new(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// Dollars per second of audio on Google's paid Standard tier (25 audio tokens a second;
    /// text input is a rounding error beside it). Through 2026; Google doubles these on
    /// 2027-01-01.
    public static double DollarsPerSecond(this GeminiModel m, DateTime? date = null)
    {
        double perMillion = m == GeminiModel.Flash ? 9 : 6;
        var when = (date ?? DateTime.UtcNow).ToUniversalTime();
        return perMillion * (when >= PriceRise ? 2 : 1) * 25 / 1_000_000;
    }

    /// Rough seconds of audio for a piece of text (about 15 characters a second for a
    /// narrated book), for cost estimates.
    public static double EstimatedSeconds(long characters) => characters / 15.0;

    /// Google's 30 prebuilt voices, with the one-word character Google gives each. All speak
    /// every language Gemini TTS does.
    public static readonly IReadOnlyList<VoiceInfo> Voices = new (string Name, Gender G, string Detail)[]
    {
        ("Zephyr", Gender.Female, "Bright"), ("Puck", Gender.Male, "Upbeat"), ("Charon", Gender.Male, "Informative"),
        ("Kore", Gender.Female, "Firm"), ("Fenrir", Gender.Male, "Excitable"), ("Leda", Gender.Female, "Youthful"),
        ("Orus", Gender.Male, "Firm"), ("Aoede", Gender.Female, "Breezy"), ("Callirrhoe", Gender.Female, "Easy-going"),
        ("Autonoe", Gender.Female, "Bright"), ("Enceladus", Gender.Male, "Breathy"), ("Iapetus", Gender.Male, "Clear"),
        ("Umbriel", Gender.Male, "Easy-going"), ("Algieba", Gender.Male, "Smooth"), ("Despina", Gender.Female, "Smooth"),
        ("Erinome", Gender.Female, "Clear"), ("Algenib", Gender.Male, "Gravelly"), ("Rasalgethi", Gender.Male, "Informative"),
        ("Laomedeia", Gender.Female, "Upbeat"), ("Achernar", Gender.Female, "Soft"), ("Alnilam", Gender.Male, "Firm"),
        ("Schedar", Gender.Male, "Even"), ("Gacrux", Gender.Female, "Mature"), ("Pulcherrima", Gender.Female, "Forward"),
        ("Achird", Gender.Male, "Friendly"), ("Zubenelgenubi", Gender.Male, "Casual"), ("Vindemiatrix", Gender.Female, "Gentle"),
        ("Sadachbia", Gender.Male, "Lively"), ("Sadaltager", Gender.Male, "Knowledgeable"), ("Sulafat", Gender.Female, "Warm"),
    }.Select(v => new VoiceInfo(VoiceEngine.Gemini, v.Name, v.Name, v.G,
        v.Name == "Gacrux" ? VoiceAge.Older : v.Name == "Leda" ? VoiceAge.Young : VoiceAge.Adult,
        "Multilingual", 2, v.Detail)).ToList();

    /// The direction Gemini gets for a line ("speaking-style" text, not read aloud): the tone,
    /// a thought's inner voice, and the narrator's or Claude's delivery hint. null for an even
    /// narration line, so the voice reads it plainly.
    public static string? Style(Tone? tone, LineKind? kind, string? delivery)
    {
        var parts = new List<string>();
        if (tone is { } t && t != Tone.Neutral) parts.Add(t.Direction());
        if (kind == LineKind.Thought) parts.Add("quietly, as a private thought");
        if (delivery is not null && DeliveryPrompt.Clean(delivery) is { } hint) parts.Add(hint);
        return parts.Count == 0 ? null : string.Join(", ", parts);
    }

    /// The inline tag Gemini reads as a sound ("<sigh>"), put before the line.
    public static string Tag(VocalSound sound) => sound switch
    {
        VocalSound.Laugh => "<laugh>",
        VocalSound.Chuckle => "<chuckle>",
        VocalSound.Sigh => "<sigh>",
        VocalSound.Gasp => "<gasp>",
        VocalSound.Groan => "<groan>",
        VocalSound.Sniffle => "<sob>",
        VocalSound.Yawn => "<yawn>",
        _ => "<cough>",
    };

    /// The request body for one piece of text in one voice: WAV back (24 kHz mono 16-bit).
    /// Keys sorted like the Mac's (JSONSerialization .sortedKeys), so both send the same bytes.
    public static byte[] RequestBody(string text, string voice, string? style, GeminiModel model)
    {
        var content = new JsonObject();
        if (style is not null)
            content["annotations"] = new JsonArray(new JsonObject { ["style"] = style, ["type"] = "speech_metadata" });
        content["text"] = text;
        content["type"] = "text";
        var body = new JsonObject
        {
            ["generation_config"] = new JsonObject { ["speech_config"] = new JsonArray(new JsonObject { ["voice"] = voice }) },
            ["input"] = new JsonArray(new JsonObject { ["content"] = new JsonArray(content), ["type"] = "user_input" }),
            ["model"] = model.Raw(),
            ["response_format"] = new JsonObject { ["type"] = "audio" },
            ["store"] = false,
        };
        return JsonSerializer.SerializeToUtf8Bytes(body, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    /// A reply without audio, with Google's text when it answered in words (a refusal, say).
    public sealed class NoAudioException(string? said) : Exception(said ?? "No audio in the reply.")
    {
        public string? Said { get; } = said;
    }

    /// The audio from a reply: the last audio part of the model's output, base64 WAV (or raw
    /// 16-bit PCM). Throws `NoAudioException` with Google's text when it answered without audio.
    public static byte[] AudioFromResponse(byte[] data)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(data); } catch (JsonException) { throw new NoAudioException(null); }
        if (root is not JsonObject obj) throw new NoAudioException(null);
        byte[]? audio = null;
        string? text = null;
        foreach (var step in (obj["steps"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Str(step, "type") != "model_output") continue;
            foreach (var part in (step["content"] as JsonArray ?? []).OfType<JsonObject>())
            {
                if (Str(part, "type") == "audio" && Str(part, "data") is { } b64 && TryBase64(b64) is { } d) audio = d;
                else if (Str(part, "text") is { } t) text = t;
            }
        }
        if (audio is null || audio.Length == 0) throw new NoAudioException(text);
        return audio;
    }

    /// 16-bit little-endian samples from a WAV file (its "data" chunk) or raw PCM.
    public static byte[] Pcm16FromWavOrRaw(byte[] data)
    {
        static bool Is(byte[] d, int at, string id) => d[at] == id[0] && d[at + 1] == id[1] && d[at + 2] == id[2] && d[at + 3] == id[3];
        if (data.Length <= 12 || !Is(data, 0, "RIFF") || !Is(data, 8, "WAVE")) return data;
        int i = 12;
        while (i + 8 <= data.Length)
        {
            long size = (long)data[i + 4] | (long)data[i + 5] << 8 | (long)data[i + 6] << 16 | (long)data[i + 7] << 24;
            int start = i + 8;
            if (Is(data, i, "data")) return data[start..(int)Math.Min(data.Length, start + Math.Max(0, size))];
            long next = start + size + (size & 1);
            if (next > int.MaxValue) break;
            i = (int)next;
        }
        return [];
    }

    /// Google's error message from an error reply ({"error": {"message", "status"}}).
    public static (string Message, string Status)? ErrorMessage(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is JsonObject root && root["error"] is JsonObject e)
                return (Str(e, "message") ?? "", Str(e, "status") ?? "");
        }
        catch (JsonException) { }
        return null;
    }

    /// How long Google asks to wait before trying again: the RetryInfo detail of a 429
    /// ({"@type": "…google.rpc.RetryInfo", "retryDelay": "27s"}). null when it doesn't say.
    public static double? RetryDelay(byte[] data)
    {
        foreach (var d in Details(data))
            if (Str(d, "@type")?.EndsWith("RetryInfo", StringComparison.Ordinal) == true && Str(d, "retryDelay") is { } s && s.EndsWith('s')
                && double.TryParse(s[..^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v >= 0)
                return v;
        return null;
    }

    /// A 429 for a daily limit (QuotaFailure with a "…PerDay…" quota), which waiting a
    /// minute won't fix. Per-minute limits are only "busy".
    public static bool IsDailyQuota(byte[] data) => Details(data).Any(d =>
        d["violations"] is JsonArray vs && vs.OfType<JsonObject>().Any(v => Str(v, "quotaId")?.Contains("PerDay", StringComparison.OrdinalIgnoreCase) == true));

    /// The per-minute request limit a 429 names (QuotaFailure's "quotaValue" for a
    /// "…PerMinute…" quota), so requests can be spaced to fit it. null when it doesn't say.
    public static int? PerMinuteLimit(byte[] data)
    {
        foreach (var d in Details(data))
            if (d["violations"] is JsonArray vs)
                foreach (var v in vs.OfType<JsonObject>())
                {
                    if (Str(v, "quotaId")?.Contains("PerMinute", StringComparison.OrdinalIgnoreCase) != true) continue;
                    if (v["quotaValue"] is JsonValue q && (q.TryGetValue<string>(out var s) && int.TryParse(s, out var n) || q.TryGetValue<int>(out n)) && n > 0)
                        return n;
                }
        return null;
    }

    /// A 429 that waiting won't fix: a daily quota, or "quota" with no time to wait.
    public static bool IsOutOfQuota(byte[] data)
    {
        if (IsDailyQuota(data)) return true;
        var message = ErrorMessage(data)?.Message ?? "";
        return message.Contains("quota", StringComparison.OrdinalIgnoreCase) && RetryDelay(data) is null
            && !message.Contains("per minute", StringComparison.OrdinalIgnoreCase);
    }

    static IEnumerable<JsonObject> Details(byte[] data)
    {
        try
        {
            if (JsonNode.Parse(data) is JsonObject root && root["error"] is JsonObject e && e["details"] is JsonArray a)
                return a.OfType<JsonObject>().ToList();
        }
        catch (JsonException) { }
        return [];
    }

    static string? Str(JsonObject o, string k) => o[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static byte[]? TryBase64(string s)
    {
        try { return Convert.FromBase64String(s); } catch (FormatException) { return null; }
    }
}
