// First-Person Narrator (docs/CASTING-STYLE.md § First-Person Narrator), ported from the
// Mac's FirstPersonVoice, FirstPersonSettings and FirstPersonRules in
// Sources/InnerVoiceKit/CastingStyle.swift: Narrator + Two Voices plus the "I" character
// (`ProjectSettings.PovCharacterID`), who can sound the same as the narrator, a variant of
// them, or entirely different. The narrator chooses every part; JSON keys match the Mac's.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerVoice.Core;

/// How the "I" character sounds next to the narrator.
public enum FirstPersonVoice
{
    /// The narrator's voice, unchanged.
    Same,
    /// The narrator's voice, shaded (pitch, pace, texture).
    Variant,
    /// The character's own cast voice.
    Different,
}

public static class FirstPersonVoiceNames
{
    public static string Title(this FirstPersonVoice v) => v switch
    {
        FirstPersonVoice.Same => "Same as Narrator",
        FirstPersonVoice.Variant => "Variant of Narrator",
        _ => "Different Voice",
    };
}

/// First-Person Narrator settings. null fields take the defaults below, like the Mac's.
public sealed class FirstPersonSettings : ModelObject
{
    /// null: Variant of Narrator.
    public FirstPersonVoice? Voice { get; set; }
    /// Variant of Narrator's shade. null: a little higher, a bit quicker.
    public Shade? Shade { get; set; }
    /// The "I" character's voice reads the narration too. null: no, the narrator does.
    public bool? ReadsNarration { get; set; }
    /// Their thoughts in their voice. null: yes; false: the narrator reads them.
    public bool? VoicesThoughts { get; set; }
    /// Books with more than one "I": chapter → the character telling it. Chapters not listed
    /// use the book's point-of-view character. Written as the Mac writes a [UUID: UUID]:
    /// one flat array, key, value, key, value.
    [JsonConverter(typeof(GuidPairsConverter))]
    public Dictionary<Guid, Guid>? ChapterNarrators { get; set; }

    public static Shade DefaultShade => Shade.Make(pitch: 1, pace: 1);

    [JsonIgnore] public FirstPersonVoice VoiceMode => Voice ?? FirstPersonVoice.Variant;
    [JsonIgnore] public Shade VariantShade => Shade ?? DefaultShade;

    public FirstPersonSettings Copy() => new()
    {
        Voice = Voice, Shade = Shade?.Copy(), ReadsNarration = ReadsNarration, VoicesThoughts = VoicesThoughts,
        ChapterNarrators = ChapterNarrators is null ? null : new(ChapterNarrators), Extra = CopyExtra(),
    };
}

public static class FirstPersonRules
{
    /// Who says "I" in a chapter: its own narrator, else the book's.
    public static Guid? Teller(Guid? chapter, ProjectSettings settings)
    {
        if (chapter is { } ch && settings.FirstPerson?.ChapterNarrators is { } map && map.TryGetValue(ch, out var id)) return id;
        return settings.PovCharacterID;
    }

    /// Everyone who tells part of the book.
    public static HashSet<Guid> Tellers(ProjectSettings settings)
    {
        var all = settings.FirstPerson?.ChapterNarrators?.Values.ToHashSet() ?? [];
        if (settings.PovCharacterID is { } pov) all.Add(pov);
        return all;
    }

    /// A character merged into another: their chapters go to the one they became.
    public static void Merge(ProjectSettings settings, Guid from, Guid into)
    {
        if (settings.FirstPerson?.ChapterNarrators is not { } map) return;
        foreach (var k in map.Where(kv => kv.Value == from).Select(kv => kv.Key).ToList()) map[k] = into;
    }

    /// A character deleted: their chapters go back to the book's "I" character.
    public static void Remove(ProjectSettings settings, Guid id)
    {
        if (settings.FirstPerson is not { ChapterNarrators: { } map } fp) return;
        foreach (var k in map.Where(kv => kv.Value == id).Select(kv => kv.Key).ToList()) map.Remove(k);
        if (map.Count == 0) fp.ChapterNarrators = null;
    }
}

/// A Guid → Guid map as Swift's JSONEncoder writes `[UUID: UUID]`: a flat array of
/// uppercase strings, key, value, key, value. Reads an object too, in case the Mac ever
/// writes one.
public sealed class GuidPairsConverter : JsonConverter<Dictionary<Guid, Guid>>
{
    public override Dictionary<Guid, Guid>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var map = new Dictionary<Guid, Guid>();
        if (reader.TokenType == JsonTokenType.StartObject)
        {
            while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
            {
                var key = Guid.Parse(reader.GetString()!);
                reader.Read();
                map[key] = Guid.Parse(reader.GetString() ?? throw new JsonException("Expected a UUID."));
            }
            return map;
        }
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of UUIDs.");
        Guid? pending = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            var g = Guid.Parse(reader.GetString() ?? throw new JsonException("Expected a UUID."));
            if (pending is { } k) { map[k] = g; pending = null; } else pending = g;
        }
        if (pending is not null) throw new JsonException("A UUID map needs an even number of entries.");
        return map;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<Guid, Guid> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var (k, v) in value)
        {
            writer.WriteStringValue(k.ToString("D").ToUpperInvariant());
            writer.WriteStringValue(v.ToString("D").ToUpperInvariant());
        }
        writer.WriteEndArray();
    }
}

/// The Cast page's sections (the Mac's `castGroups` in Views/CastPage.swift): Prominent
/// Characters speak at least 3% of all dialogue lines, and at least 5 lines; everyone else is
/// a Minor Character.
public static class CastProminence
{
    public const string ProminentTitle = "Prominent Characters", MinorTitle = "Minor Characters";

    /// The fewest lines a prominent character has.
    public static int Bar(Project project, BookIndex index)
    {
        int total = project.Characters.Sum(c => index.StatsFor(c.Id)?.Lines ?? 0);
        return Math.Max(5, (int)Math.Ceiling(total * 0.03));
    }

    /// The characters in two groups, each keeping the order given. Empty groups are left out.
    public static List<(string Title, List<BookCharacter> Members)> Groups(Project project, BookIndex index, IEnumerable<BookCharacter> shown)
    {
        int bar = Bar(project, index);
        var all = shown.ToList();
        var prominent = all.Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) >= bar).ToList();
        var minor = all.Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) < bar).ToList();
        return new List<(string, List<BookCharacter>)> { (ProminentTitle, prominent), (MinorTitle, minor) }
            .Where(g => g.Item2.Count > 0).ToList();
    }
}
