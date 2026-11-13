// The first pieces of the Mac's Sources/InnerVoiceKit/Voices.swift that the Windows app
// needs before it has voices: the tone names and audio tags (for delivery hints) and who
// decided a tone or hint. Lines keep their tone fields (`tone`, `toneConfidence`,
// `toneSource`) in Extra, unchanged, so they round-trip; `Line.Tone` only reads them.
// TODO(voices): the voices work models tone on Line and ports the rest of Voices.swift.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerVoice.Core;

/// How a line is said. Raw values match the Mac's (`neutral`, `whispered`, …).
public enum Tone { Neutral, Excited, Confused, Sad, Angry, Whispered, Scared, Tender, Sarcastic }

/// Who decided a line's tone or delivery hint. The narrator's is never changed by the rules or the AI.
public enum ToneSource { Rule, Ai, User }

public static class Tones
{
    public static string Raw(this Tone t) => t.ToString().ToLowerInvariant();

    public static string Title(this Tone t) => t.ToString();

    /// The tone as an ElevenLabs eleven_v4 audio tag; none for neutral.
    public static string? AudioTag(this Tone t) => t switch
    {
        Tone.Neutral => null,
        Tone.Whispered => "[whispers]",
        Tone.Tender => "[tenderly]",
        _ => $"[{t.Raw()}]",
    };

    public static Tone? Parse(string? raw) =>
        Enum.TryParse<Tone>(raw, ignoreCase: true, out var t) && Enum.IsDefined(t) ? t : null;

    /// The line's tone, read from the Mac's field in Extra. null: none set.
    public static Tone? ToneOf(Line line) =>
        line.Extra is { } x && x.TryGetValue("tone", out var v) && v.ValueKind == JsonValueKind.String ? Parse(v.GetString()) : null;
}
