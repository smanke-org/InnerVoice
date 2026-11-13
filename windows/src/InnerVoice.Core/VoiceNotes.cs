// Ported from Sources/InnerVoiceKit/VoiceNotes.swift and the VoiceBrief type in
// AI/VoiceBrief.swift. The blocks are the same text as on the Mac, so a book's Voice Notes
// read the same in both apps and Build Summaries on either replaces the other's blocks.

using System.Text.Json.Serialization;

namespace InnerVoice.Core;

/// What Build Summary learns about how a character sounds, written into their Voice Notes as
/// a "From the book:" block. The narrator's own notes are never touched; a block the
/// narrator edited is left as it is.
public static class VoiceNotesFromBook
{
    public const string Heading = "From the book:";

    static readonly (FactKind Kind, string Title)[] Kinds =
        [(FactKind.Age, "Age"), (FactKind.Gender, "Gender"), (FactKind.Accent, "Accent"), (FactKind.Voice, "Voice"), (FactKind.Personality, "Manner")];

    /// The block for a character's current facts, or null when the summary says nothing
    /// about their voice.
    public static string? Block(BookCharacter c)
    {
        var lines = new List<string>();
        foreach (var (kind, title) in Kinds)
        {
            var texts = c.Facts.Where(f => f.Kind == kind)
                .Select(f => f.Text.Trim().Trim('.', ';', '/', ' '))
                .Where(t => t.Any(char.IsLetter)).ToList();
            var picked = kind == FactKind.Personality ? texts.Take(2).ToList() : texts;
            if (picked.Count == 0) continue;
            var unique = new List<string>();
            foreach (var t in picked) if (!unique.Any(u => string.Equals(u, t, StringComparison.OrdinalIgnoreCase))) unique.Add(t);
            lines.Add($"{title}: " + CapitalizedFirst(string.Join("; ", unique)));
        }
        if (!lines.Any(l => l.StartsWith("Accent") || l.StartsWith("Voice") || l.StartsWith("Manner"))) return null;
        return string.Join("\n", new[] { Heading }.Concat(lines));
    }

    /// Puts the block into the character's Voice Notes. Returns whether they changed.
    public static bool Merge(BookCharacter c)
    {
        if (Block(c) is not { } block) return false;
        var previous = c.VoiceNotesFromBook;
        var notes = c.VoiceNotes;
        if (!GeneratedNotes.Put(block, Heading, ref previous, ref notes)) return false;
        c.VoiceNotesFromBook = previous;
        c.VoiceNotes = notes;
        return true;
    }

    public static string CapitalizedFirst(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];
}

/// Blocks InnerVoice writes into Voice Notes ("From the book:", "Voice brief:"): each
/// replaced only while it's exactly as last written; once the narrator edits one, it's theirs.
public static class GeneratedNotes
{
    public static bool Put(string block, string heading, ref string? previous, ref string notes)
    {
        int at = previous is { Length: > 0 } old ? notes.IndexOf(old, StringComparison.Ordinal) : -1;
        if (at >= 0)
        {
            if (previous == block) return false;
            notes = notes[..at] + block + notes[(at + previous!.Length)..];
        }
        else if (notes.Contains(heading, StringComparison.Ordinal)) return false;
        else
        {
            var trimmed = notes.Trim();
            notes = trimmed.Length == 0 ? block : trimmed + "\n\n" + block;
        }
        previous = block;
        return true;
    }

    /// The narrator's own part of the notes: without the blocks InnerVoice wrote.
    public static string NarratorsOwn(BookCharacter c)
    {
        var s = c.VoiceNotes;
        foreach (var block in new[] { c.VoiceNotesFromBook, c.VoiceBriefGenerated })
            if (block is { Length: > 0 }) s = s.Replace(block, "");
        return s.Trim();
    }
}

/// A casting director's note for one character: pitch, pace, texture, attitude, and how the
/// voice should differ from the characters they share scenes with.
public sealed class VoiceBrief
{
    [JsonPropertyName("pitch")] public string Pitch { get; set; } = "";
    [JsonPropertyName("pace")] public string Pace { get; set; } = "";
    [JsonPropertyName("texture")] public string Texture { get; set; } = "";
    [JsonPropertyName("attitude")] public string Attitude { get; set; } = "";
    [JsonPropertyName("contrast")] public string Contrast { get; set; } = "";

    public const string Heading = "Voice brief:";

    /// The block in Voice Notes.
    public string Block
    {
        get
        {
            var lines = new List<string> { Heading };
            foreach (var (title, value) in new[] { ("Pitch", Pitch), ("Pace", Pace), ("Texture", Texture), ("Attitude", Attitude), ("Contrast", Contrast) })
            {
                var v = value.Trim().Trim('.');
                if (v.Length > 0) lines.Add($"{title}: " + VoiceNotesFromBook.CapitalizedFirst(v));
            }
            return string.Join("\n", lines);
        }
    }

    /// Reads a brief back out of Voice Notes (the narrator may have edited it).
    public static (string? Pitch, string? Pace) Read(string notes)
    {
        int at = notes.IndexOf(Heading, StringComparison.Ordinal);
        if (at < 0) return (null, null);
        string? pitch = null, pace = null;
        foreach (var raw in notes[(at + Heading.Length)..].Split('\n'))
        {
            var l = raw.Trim();
            var lower = l.ToLowerInvariant();
            if (lower.StartsWith("pitch:")) pitch = l[6..].Trim();
            if (lower.StartsWith("pace:")) pace = l[5..].Trim();
            if (l.EndsWith(':') && !lower.StartsWith("pitch") && !lower.StartsWith("pace") && l != Heading) break;
        }
        return (pitch, pace);
    }

    /// Puts the brief into the character's Voice Notes. Returns whether they changed.
    public bool Merge(BookCharacter c)
    {
        var previous = c.VoiceBriefGenerated;
        var notes = c.VoiceNotes;
        if (!GeneratedNotes.Put(Block, Heading, ref previous, ref notes)) return false;
        c.VoiceBriefGenerated = previous;
        c.VoiceNotes = notes;
        return true;
    }
}

/// A character's Voice Notes turned into words a voice model can design a voice from
/// (ElevenLabs' Voice Design; the on-device engines can't take a description): the narrator's own notes, then the brief, then "From the
/// book", without headings or Contrast lines, capped at a whole line.
public static class VoiceDescription
{
    /// The notes as one paragraph, or null when they say nothing.
    public static string? Notes(BookCharacter c, int limit = 600)
    {
        var own = GeneratedNotes.NarratorsOwn(c);
        var brief = c.VoiceBriefGenerated is { } b && c.VoiceNotes.Contains(b, StringComparison.Ordinal) ? b : null;
        var book = c.VoiceNotesFromBook is { } f && c.VoiceNotes.Contains(f, StringComparison.Ordinal) ? f : null;
        // Generated blocks the narrator edited are part of `own` already.
        var pieces = new List<string>();
        foreach (var text in new[] { own, brief ?? "", book ?? "" })
            foreach (var raw in text.Split('\n', '\r'))
            {
                var line = raw.Trim().Trim('-', '•', '*', ' ');
                if (!line.Any(char.IsLetter)) continue;
                if (line == VoiceNotesFromBook.Heading || line == VoiceBrief.Heading) continue;
                // Who they contrast with means nothing to a voice model; how they differ does.
                if (line.ToLowerInvariant().StartsWith("contrast:", StringComparison.Ordinal)) continue;
                if (!".!?".Contains(line[^1])) line += ".";
                if (pieces.Any(p => string.Equals(p, line, StringComparison.OrdinalIgnoreCase))) continue;
                pieces.Add(line);
            }
        var output = "";
        foreach (var p in pieces)
        {
            var next = output.Length == 0 ? p : output + " " + p;
            if (next.Length > limit) break;
            output = next;
        }
        return output.Length == 0 ? null : output;
    }

    /// A voice description with the character's notes added, within `limit` characters.
    public static string Design(string @base, BookCharacter c, int limit = 700)
    {
        var b = @base.Trim();
        if (Notes(c, Math.Max(0, limit - b.Length - 12)) is not { } n) return b;
        return b.Length == 0 ? n : b + " Voice notes: " + n;
    }
}
