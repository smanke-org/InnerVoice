// Ported from Sources/InnerVoiceKit/AI/Prompts.swift and VoiceBrief.swift (the prompts and
// grammars are the Mac's, word for word).

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InnerVoice.Core.AI;

/// A piece of a chapter sent to the model to decide who speaks the marked lines.
public sealed record AttributionChunk(int ChapterIndex, Dictionary<string, Guid> Labels, List<ChatMessage> Messages, int Words)
{
    /// The labels in reading order.
    public List<string> Order { get; init; } = [];
    /// The passage's own text, without the tags: a new character's name must be written here.
    public string Passage { get; init; } = "";
    /// Every name the model may answer with: the cast's names and aliases, and the capitalized
    /// words of the passage (joined into names of up to three words by the grammar).
    public List<string> CastNames { get; init; } = [];
    public List<string> PassageWords { get; init; } = [];

    /// A grammar that needs one answer for every label, in order, and only lets the speaker be
    /// a cast name, a name made of the passage's capitalized words, or "?". Without it, Gemma 4
    /// 26B A4B answered with labels ("L12"), glued or misspelled names ("LAlice", "Dorm4ouse").
    public string Grammar
    {
        get
        {
            var items = Order.Select(l => $"\"{{\" ws \"\\\"id\\\"\" ws \":\" ws \"\\\"{l}\\\"\" ws \",\" ws rest");
            var g = "root ::= \"{\" ws \"\\\"lines\\\"\" ws \":\" ws \"[\" ws " + string.Join(" ws \",\" ws ", items) + " ws \"]\" ws \"}\"\n";
            g += AttributionPrompt.GrammarTail;
            var cast = CastNames.Where(AttributionPrompt.GrammarSafe).Select(AttributionPrompt.Literal).ToList();
            var words = PassageWords.Where(AttributionPrompt.GrammarSafe).Select(AttributionPrompt.Literal).ToList();
            var name = new List<string>();
            if (cast.Count > 0) name.Add("cast");
            if (words.Count > 0) name.Add("word ( \" \" word ){0,2}");
            g += "name ::= " + (name.Count == 0 ? "\"?\"" : string.Join(" | ", name)) + "\n";
            if (cast.Count > 0) g += "cast ::= " + string.Join(" | ", cast) + "\n";
            if (words.Count > 0) g += "word ::= " + string.Join(" | ", words) + "\n";
            return g;
        }
    }
}

/// One answer from the model about one line.
public sealed class AttributionReply
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("speaker")] public string Speaker { get; set; } = "";
    [JsonPropertyName("kind")] public string? Kind { get; set; }
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }
}

/// One fact the model found about a character.
public sealed class FactReply
{
    [JsonPropertyName("kind")] public string Kind { get; set; } = "";
    [JsonPropertyName("text")] public string Text { get; set; } = "";
    [JsonPropertyName("paragraph")] public string? Paragraph { get; set; }
    [JsonPropertyName("quote")] public string? Quote { get; set; }
}

internal static class Replies
{
    static readonly JsonSerializerOptions Options = new() { NumberHandling = JsonNumberHandling.AllowReadingFromString };

    public static T? Decode<T>(string json) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); } catch (JsonException) { return null; }
    }
}

public static class AttributionPrompt
{
    public const string System = "You help an audiobook narrator prepare a novel. You read a passage and decide who speaks, thinks or writes each marked line, so the narrator knows which voice to use.\n" +
        "Marked lines start with a tag like {L4} just before the line. Lines that are already known start with the speaker's name, like {Tom}. Use the speech tags (\"she said\"), who is in the scene, who was addressed, and the turn-taking of the conversation. When the tag is only a pronoun (\"he said\"), the speaker is usually the person doing something in the sentence just before the line.\n" +
        "Always name the most likely speaker. When you are not sure, still name your best guess and give it a low confidence; the narrator checks every low-confidence line. A speaker is always a character's name, spelled as in the character list or the passage, never a tag like L4.\n" +
        "Answer with JSON only.";

    /// Lines the model still needs to decide: not the narrator's and not a clear "said Tom".
    public static bool NeedsModel(Line line) => line.Source switch
    {
        AssignmentSource.User or AssignmentSource.Ai => false,
        AssignmentSource.Rule => line.Confidence < 0.95,
        _ => true,
    };

    /// Splits one chapter into passages of about `maxWords`, each with the lines to decide.
    public static List<AttributionChunk> Chunks(Project project, BookIndex index, int chapterIndex,
                                                Dictionary<Guid, string>? hints = null, int maxWords = 900)
    {
        hints ??= [];
        var chapter = project.Chapters[chapterIndex];
        var output = new List<AttributionChunk>();
        int start = 0;
        var paragraphs = chapter.Paragraphs;
        while (start < paragraphs.Count)
        {
            int end = start, words = 0;
            while (end < paragraphs.Count && (words < maxWords || end == start))
            {
                words += paragraphs[end].WordCount;
                end++;
            }
            var asked = paragraphs.Skip(start).Take(end - start).SelectMany(p => index.LinesIn(p.Id))
                .Where(i => NeedsModel(project.Lines[i])).ToHashSet();
            if (asked.Count > 0)
            {
                // Two paragraphs of lead-in, so the turn-taking makes sense.
                int contextStart = Math.Max(0, start - 2);
                output.Add(Build(project, index, chapterIndex, contextStart, end, asked, hints, words));
            }
            start = end;
        }
        return output;
    }

    static readonly Regex Pending = new(@"\{PENDING(\d+)( \w+)?\}", RegexOptions.CultureInvariant);

    static AttributionChunk Build(Project project, BookIndex index, int chapterIndex, int from, int to, HashSet<int> asked,
                                  Dictionary<Guid, string> hints, int words)
    {
        var chapter = project.Chapters[chapterIndex];
        var labels = new Dictionary<string, Guid>();
        var order = new List<string>();
        var passage = new StringBuilder();
        var hintLines = new List<string>();
        var plain = new StringBuilder();
        for (int k = from; k < to; k++)
        {
            var p = chapter.Paragraphs[k];
            plain.Append(p.Text).Append("\n\n");
            var lineIdx = index.LinesIn(p.Id).OrderByDescending(i => project.Lines[i].Range.Location).ToList();
            var text = new StringBuilder(p.Text);
            // Insert tags from the end so earlier offsets stay valid.
            foreach (var i in lineIdx)
            {
                var line = project.Lines[i];
                int at = Math.Min(line.Range.Location, text.Length);
                if (at > 0 && "“\"‘'«—".Contains(p.Text[Math.Min(at - 1, p.Text.Length - 1)])) at--;
                var kind = line.Kind == LineKind.Spoken ? "" : " " + JsonNamingPolicy.CamelCase.ConvertName(line.Kind.ToString());
                if (asked.Contains(i)) text.Insert(at, $"{{PENDING{i}{kind}}}");
                else if (project.Character(line.SpeakerID) is { } s) text.Insert(at, $"{{{s.Name}{kind}}}");
            }
            passage.Append(text).Append("\n\n");
        }
        // Number the asked lines in reading order.
        int n = 0;
        var src = passage.ToString();
        var result = new StringBuilder();
        int cursor = 0;
        foreach (Match m in Pending.Matches(src))
        {
            n++;
            var label = $"L{n}";
            int i = int.Parse(m.Groups[1].Value);
            var line = project.Lines[i];
            labels[label] = line.Id;
            order.Add(label);
            if (hints.TryGetValue(line.Id, out var pronoun)) hintLines.Add($"{label}: the tag says “{pronoun}”");
            var suffix = m.Groups[2].Success ? m.Groups[2].Value : "";
            result.Append(src, cursor, m.Index - cursor).Append('{').Append(label).Append(suffix).Append('}');
            cursor = m.Index + m.Length;
        }
        result.Append(src[cursor..]);

        var user = new StringBuilder("Characters so far: " + CastList(project) + "\n");
        if (project.Character(project.Settings.PovCharacterID) is { } pov)
            user.Append($"The book is told in the first person by {pov.Name}: “I” is {pov.Name}.\n");
        if (hintLines.Count > 0) user.Append("Hints: " + string.Join("; ", hintLines) + "\n");
        user.Append($"\nPassage from “{chapter.Title}”:\n\n" + result.ToString().Trim() + "\n\n");
        user.Append($"For each of {string.Join(", ", order)}: give the speaker's name exactly as in the character list, " +
                    "or a new name only if the passage writes that name and the person is not in the list. If the passage " +
                    "doesn't make it certain, give the most likely speaker with a confidence under 50. Use \"?\" only when no " +
                    "one in the passage could have said it. kind is spoken, thought or written. confidence is 0 to 100.\n" +
                    "Reply as {\"lines\":[{\"id\":\"L1\",\"speaker\":\"Name\",\"kind\":\"spoken\",\"confidence\":90}]}");
        var cast = project.Characters.SelectMany(c => new[] { c.Name }.Concat(c.Aliases)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToList();
        return new AttributionChunk(chapterIndex, labels, [new(ChatRole.System, System), new(ChatRole.User, user.ToString())], words)
        {
            Order = order, Passage = plain.ToString(), CastNames = cast, PassageWords = CapitalizedWords(plain.ToString()),
        };
    }

    static readonly Regex Capitalized = new(@"\p{Lu}[\p{L}\p{M}]*(?:[’'\-][\p{L}\p{M}]+)*", RegexOptions.CultureInvariant);

    /// The capitalized words of a passage (and the usual titles), the pieces a new name is made of.
    internal static List<string> CapitalizedWords(string text)
    {
        var words = Capitalized.Matches(text).Select(m => m.Value).ToHashSet();
        words.UnionWith(Names.Titles);
        return words.OrderBy(w => w, StringComparer.Ordinal).ToList();
    }

    static readonly Regex LabelName = new(@"^L\d+$", RegexOptions.CultureInvariant);

    /// Whether `rawName`, the model's answer for a speaker no one in the cast goes by, is a
    /// name written in the passage: every word of it appears there, and it isn't a line label.
    /// Gemma 4 26B A4B made up "L12", "LAlice", "Hattter" and "Dorm4ouse"; none of them pass.
    public static bool IsNamed(string rawName, string passage, IReadOnlyDictionary<string, Guid>? labels = null)
    {
        var name = Regex.Replace(rawName.Trim(' ', ',', '.', '’', '\''), "[’']s$", "");
        if ((labels?.ContainsKey(name) ?? false) || LabelName.IsMatch(name)) return false;
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0 || !words.All(w => w.Any(char.IsLetter))) return false;
        var lower = passage.ToLowerInvariant();
        return words.All(w => Names.Titles.Contains(w) || RuleAttribution.ContainsWord(passage, w) || RuleAttribution.ContainsWord(lower, w.ToLowerInvariant()));
    }

    /// What follows each answer's id in the grammar.
    internal const string GrammarTail = """
        rest ::= "\"speaker\"" ws ":" ws speaker ws "," ws "\"kind\"" ws ":" ws kind ws "," ws "\"confidence\"" ws ":" ws number ws "}"
        speaker ::= "\"?\"" | "\"" name "\""
        kind ::= "\"spoken\"" | "\"thought\"" | "\"written\""
        number ::= [0-9] [0-9]? [0-9]?
        ws ::= [ ]?

        """;

    internal static bool GrammarSafe(string s) => s.Length > 0 && !s.Contains('"') && !s.Contains('\\') && !s.Contains('\n');

    internal static string Literal(string s) => "\"" + s + "\"";

    static string CastList(Project project)
    {
        var cast = project.Characters.Take(40).Select(c => c.Aliases.Count == 0 ? c.Name : $"{c.Name} (also {string.Join(", ", c.Aliases)})").ToList();
        return cast.Count == 0 ? "none yet" : string.Join("; ", cast);
    }

    /// llama.cpp grammar that only allows the reply shape.
    public const string Grammar = """
        root ::= "{" ws "\"lines\"" ws ":" ws "[" ws ( item ( ws "," ws item )* )? ws "]" ws "}"
        item ::= "{" ws "\"id\"" ws ":" ws string ws "," ws "\"speaker\"" ws ":" ws string ws "," ws "\"kind\"" ws ":" ws kind ws "," ws "\"confidence\"" ws ":" ws number ws "}"
        kind ::= "\"spoken\"" | "\"thought\"" | "\"written\""
        string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] )* "\""
        number ::= [0-9] [0-9]? [0-9]?
        ws ::= [ ]?
        """;

    sealed class Wrapper { [JsonPropertyName("lines")] public List<AttributionReply>? Lines { get; set; } }

    /// Pulls the replies out of the model's text, tolerating chatter around the JSON.
    public static List<AttributionReply>? Parse(string text)
    {
        if (JsonText.Extract(text) is { } json && Replies.Decode<Wrapper>(json)?.Lines is { } whole) return whole;
        // A reply cut off by the token limit still has its finished answers.
        var partial = JsonText.Objects(text).Select(Replies.Decode<AttributionReply>).Where(r => r is not null && r.Id.Length > 0).Cast<AttributionReply>().ToList();
        return partial.Count == 0 ? null : partial;
    }

    static readonly HashSet<string> Unknown = ["?", "", "unknown", "none", "n/a", "narrator", "unclear", "nobody", "spoken", "thought", "written"];

    static readonly Regex ConfidenceNote = new(@"^\W*\d+\s*%[^:]*:\s*", RegexOptions.CultureInvariant);

    /// A speaker name as the model wrote it, without markdown or a confidence note
    /// ("*50% confidence: Mara*" is Mara).
    public static string CleanName(string raw)
    {
        var name = raw.Trim().Trim('*', '_', '"', '“', '”', ' ');
        name = ConfidenceNote.Replace(name, "");
        int paren = name.IndexOf(" (", StringComparison.Ordinal);
        if (paren > 0 && name.EndsWith(')')) name = name[..paren];
        return name.Trim().Trim('*', '_', ' ');
    }

    /// Writes the replies into the project. Returns copies of the lines before the change.
    public static List<Line> Apply(List<AttributionReply> replies, AttributionChunk chunk, Project project)
    {
        var before = new List<Line>();
        var lookup = new Dictionary<Guid, int>();
        for (int i = 0; i < project.Lines.Count; i++) lookup[project.Lines[i].Id] = i;
        foreach (var reply in replies)
        {
            if (!chunk.Labels.TryGetValue(reply.Id.Trim(), out var id) || !lookup.TryGetValue(id, out var i) || !NeedsModel(project.Lines[i])) continue;
            var old = project.Lines[i];
            before.Add(old.Copy());
            var name = CleanName(reply.Speaker);
            Guid? speaker = null;
            if (!Unknown.Contains(name.ToLowerInvariant()))
            {
                // Someone in the cast, or a new character only when the passage writes the name.
                speaker = RuleAttribution.Resolve(name, project, create: false);
                if (speaker is null && IsNamed(name, chunk.Passage, chunk.Labels)) speaker = RuleAttribution.Resolve(name, project, create: true);
            }
            double confidence = Math.Min(1, Math.Max(0, (reply.Confidence ?? 60) / 100));
            // The rules and the model agreeing is worth more than either alone.
            if (speaker is { } s && old.SpeakerID == s && old.Source != AssignmentSource.None) confidence = Math.Max(confidence, Math.Min(0.97, old.Confidence + 0.2));
            if (speaker is { } s2 && old.SpeakerID is { } o && o != s2 && old.Source == AssignmentSource.Rule) confidence = Math.Min(confidence, 0.55);
            old.SpeakerID = speaker;
            old.Confidence = speaker is null ? 0 : confidence;
            old.Source = AssignmentSource.Ai;
            if (reply.Kind is { } k && Enum.TryParse<LineKind>(k, true, out var kind)) old.Kind = kind;
        }
        return before;
    }
}

public static class SummaryPrompt
{
    public const string System = "You help an audiobook narrator prepare to voice a character. From the passages, list what the book says about the character: age, gender, accent and where they are from, how their voice sounds, appearance, personality, history and relationships. Only use what the text says or clearly shows. Each fact cites the paragraph label and copies the exact words that support it. Answer with JSON only.";

    static readonly string[] Descriptive =
    [
        "accent", "voice", "years old", "year-old", "age", "born", "grew up", "eyes", "hair", "tall", "short", "wife", "husband",
        "brother", "sister", "mother", "father", "son", "daughter", "friend", "lisp", "drawl", "brogue", "whisper", "gravel",
        "husky", "deep", "scottish", "irish", "english", "american", "southern", "french", "german", "italian", "spanish",
        "russian", "london", "boston", "texas", "old", "young", "girl", "boy", "woman", "man", "laugh", "smile", "scar", "wore",
        "dressed", "married", "widow",
    ];

    public sealed record Chunk(Dictionary<string, Guid> Labels, List<ChatMessage> Messages);

    /// Picks the paragraphs that say most about a character: where they speak, where they are
    /// named, especially near descriptive words, early ones first.
    public static List<Chunk> Chunks(BookCharacter character, Project project, BookIndex index, int maxParagraphs = 48, int maxWords = 1100)
    {
        var names = character.AllNames;
        var scored = new List<(Guid Id, int Global, double Score)>();
        var speaking = project.Lines.Where(l => l.SpeakerID == character.Id).Select(l => l.ParagraphID).ToHashSet();
        foreach (var chapter in project.Chapters)
        {
            foreach (var p in chapter.Paragraphs)
            {
                bool named = names.Any(n => RuleAttribution.ContainsWord(p.Text, n));
                if (!named && !speaking.Contains(p.Id)) continue;
                var lower = p.Text.ToLowerInvariant();
                int hits = Descriptive.Count(d => lower.Contains(d, StringComparison.Ordinal));
                int g = index.Positions.TryGetValue(p.Id, out var pos) ? pos.Global : 0;
                scored.Add((p.Id, g, hits * 3 + (named ? 2 : 0) - g / 2000.0));
            }
        }
        var picked = scored.OrderByDescending(s => s.Score).Take(maxParagraphs).OrderBy(s => s.Global).ToList();
        var output = new List<Chunk>();
        var labels = new Dictionary<string, Guid>();
        var passage = new StringBuilder();
        int words = 0, n = 0;
        void Flush()
        {
            if (labels.Count == 0) return;
            var aliases = character.Aliases.Count == 0 ? "" : $" (also called {string.Join(", ", character.Aliases)})";
            var user = $"Character: {character.Name}{aliases}\n\nPassages:\n\n{passage.ToString().Trim()}\n\n" +
                       "kind is one of age, gender, accent, voice, appearance, personality, history, relationships, other. " +
                       "text is a short note for the narrator, like \"About 60\" or \"Glaswegian accent\". " +
                       "Leave out anything the passages don't support. If there is nothing, reply {\"facts\":[]}.\n" +
                       "Reply as {\"facts\":[{\"kind\":\"age\",\"text\":\"…\",\"paragraph\":\"P1\",\"quote\":\"exact words\"}]}";
            output.Add(new Chunk(labels, [new(ChatRole.System, System), new(ChatRole.User, user)]));
            labels = [];
            passage.Clear();
            words = 0;
        }
        foreach (var (id, _, _) in picked)
        {
            if (project.Paragraph(id, index) is not { } p) continue;
            n++;
            labels[$"P{n}"] = id;
            passage.Append($"[P{n}] {p.Text}\n\n");
            words += p.WordCount;
            if (words >= maxWords) Flush();
        }
        Flush();
        return output;
    }

    public const string Grammar = """
        root ::= "{" ws "\"facts\"" ws ":" ws "[" ws ( item ( ws "," ws item )* )? ws "]" ws "}"
        item ::= "{" ws "\"kind\"" ws ":" ws kind ws "," ws "\"text\"" ws ":" ws note ws "," ws "\"paragraph\"" ws ":" ws label ws "," ws "\"quote\"" ws ":" ws quote ws "}"
        kind ::= "\"age\"" | "\"gender\"" | "\"accent\"" | "\"voice\"" | "\"appearance\"" | "\"personality\"" | "\"history\"" | "\"relationships\"" | "\"other\""
        note ::= "\"" [^"\\\n/{}] [^"\\\n/{}]* "\""
        label ::= "\"P" [0-9] [0-9]? [0-9]? "\""
        quote ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ) ( [^"\\\n] | "\\" ["\\/bfnrt] )* "\""
        ws ::= [ ]?
        """;

    /// Whether a fact's text reads like a note for the narrator. Gemma 4 26B A4B wrote
    /// "text-based/nonsensicalical-of-the-moment-of-text" and "arguing/controversial-looking".
    /// After the 1.1.2 fix it still wrote "P33" (a label), "text" and "paramount-text-voice".
    public static bool LooksLikeNote(string text)
    {
        if (!text.Any(char.IsLetter) || text.Length >= 240 || text.Contains('/') || text.Contains('{') || text.Contains('}')) return false;
        var lower = text.ToLowerInvariant();
        // A paragraph label, or a word from the reply's own JSON, instead of a note.
        if (Regex.IsMatch(lower, @"^p\d+$")) return false;
        if (SchemaWords.Contains(lower) || Enum.GetNames<FactKind>().Any(n => n.Equals(lower, StringComparison.OrdinalIgnoreCase))) return false;
        // Words strung together with hyphens past "matter-of-fact", or a hyphen chain built
        // from the JSON's keys.
        return !lower.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(token =>
        {
            var parts = token.Split('-', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length > 4 || (parts.Length > 1 && parts.Any(SchemaWords.Contains));
        });
    }

    static readonly HashSet<string> SchemaWords = ["text", "quote", "kind", "paragraph", "label", "note", "source", "facts"];

    /// Whether a fact's text fits its kind. A gender fact must name one ("female",
    /// "a young man"); Gemma filed "replying to the baby" and "paramount-age" under gender.
    public static bool Fits(string text, FactKind kind)
    {
        if (kind != FactKind.Gender) return true;
        if (GenderReader.GenderIn(text) is not null) return true;
        var lower = text.ToLowerInvariant();
        return new[] { "nonbinary", "non-binary", "genderless", "agender", "genderfluid" }.Any(lower.Contains);
    }

    sealed class Wrapper { [JsonPropertyName("facts")] public List<FactReply>? Facts { get; set; } }

    public static List<FactReply>? Parse(string text)
    {
        if (JsonText.Extract(text) is { } json && Replies.Decode<Wrapper>(json)?.Facts is { } whole) return whole;
        var partial = JsonText.Objects(text).Select(Replies.Decode<FactReply>).Where(r => r is not null && r.Kind.Length > 0).Cast<FactReply>().ToList();
        return partial.Count == 0 ? null : partial;
    }

    /// Adds supported facts to the character. Facts whose quote isn't in the cited paragraph
    /// are dropped (the model made them up); near-duplicates are skipped; facts the narrator
    /// edited are never touched. Returns how many were added.
    public static int Merge(List<FactReply> replies, Chunk chunk, Guid characterID, Project project, BookIndex index)
    {
        if (project.Character(characterID) is not { } character) return 0;
        int added = 0;
        foreach (var reply in replies)
        {
            var text = reply.Text.Trim().Trim(';', '/', ' ');
            if (!LooksLikeNote(text)) continue;
            var kind = Enum.TryParse<FactKind>(reply.Kind, true, out var k) ? k : FactKind.Other;
            if (!Fits(text, kind)) continue;
            Guid? source = reply.Paragraph is { } label && chunk.Labels.TryGetValue(label.Trim(), out var src) ? src : null;
            var quote = (reply.Quote ?? "").Trim();
            // A fact without the book's words for it is the model's own idea.
            if (!quote.Any(char.IsLetter)) continue;
            if (source is { } sid && project.Paragraph(sid, index) is { } p)
            {
                if (!TextMatch.Supports(p.Text, quote))
                {
                    // Maybe the model cited the wrong label; look in the other passages.
                    source = chunk.Labels.Values.Cast<Guid?>().FirstOrDefault(id => project.Paragraph(id!.Value, index) is { } q && TextMatch.Supports(q.Text, quote));
                    if (source is null) continue;
                }
            }
            else continue;
            var existing = character.Facts.Where(f => f.Kind == kind).ToList();
            if (existing.Any(f => TextMatch.Similarity(f.Text, text) >= 0.6)) continue;
            if (existing.Count >= 6) continue;
            character.Facts.Add(new Fact { Kind = kind, Text = text, SourceParagraphID = source, Quote = quote, FromAI = true });
            added++;
        }
        return added;
    }
}

public static class VoiceBriefPrompt
{
    public const string System = "You are the casting director for an audiobook. Write a short voice brief for one character, so a narrator or an AI voice can perform them and so they sound clearly different from the characters they share scenes with. Base it on what the book says, their lines, and the narrator's own notes, which come first when they disagree. Keep each field under 12 words, in plain words a performer can act on. Answer with JSON only.";

    /// The characters who share the most chapters with this one, heard most first.
    public static List<BookCharacter> Partners(BookCharacter character, Project project, BookIndex index, int limit = 4)
    {
        var mine = index.StatsFor(character.Id)?.Chapters ?? [];
        if (mine.Count == 0) return [];
        return project.Characters
            .Where(c => c.Id != character.Id && (index.StatsFor(c.Id)?.Lines ?? 0) > 0)
            .Select(c => (c, Shared: (index.StatsFor(c.Id)?.Chapters ?? []).Intersect(mine).Count()))
            .Where(x => x.Shared > 0)
            .OrderByDescending(x => x.Shared).ThenByDescending(x => index.StatsFor(x.c.Id)?.Lines ?? 0)
            .Take(limit).Select(x => x.c).ToList();
    }

    static string Facts(BookCharacter c)
    {
        FactKind[] kinds = [FactKind.Age, FactKind.Gender, FactKind.Accent, FactKind.Voice, FactKind.Personality];
        var parts = kinds.SelectMany(k => c.Facts.Where(f => f.Kind == k).Take(2).Select(f => $"{k.Title()}: {f.Text}")).ToList();
        return parts.Count == 0 ? "(the book says little)" : string.Join("; ", parts);
    }

    public static List<ChatMessage> Messages(BookCharacter character, Project project, BookIndex index)
    {
        var lines = project.Lines.Where(l => l.SpeakerID == character.Id && l.Kind == LineKind.Spoken)
            .Select(l => project.TextOf(l, index))
            .Where(t => Words.Count(t) is >= 4 and <= 30)
            .Take(5).ToList();
        var own = GeneratedNotes.NarratorsOwn(character);
        var partners = Partners(character, project, index);
        var partnerText = string.Join("\n", partners.Select(p =>
        {
            var s = $"- {p.Name}: {Facts(p)}";
            var brief = p.VoiceBriefGenerated ?? (p.VoiceNotes.Contains(VoiceBrief.Heading) ? p.VoiceNotes : null);
            if (brief is not null)
            {
                var b = VoiceBrief.Read(brief);
                if (b.Pitch is { } pitch) s += $"; their pitch: {pitch}";
                if (b.Pace is { } pace) s += $"; their pace: {pace}";
            }
            return s;
        }));
        var user = $"Character: {character.Name}\nWhat the book says: {Facts(character)}\n" +
                   (own.Length == 0 ? "" : $"The narrator's notes: {own}\n") +
                   "Some of their lines:\n" + string.Join("\n", lines.Select(l => $"- “{l}”")) + "\n\n" +
                   "Characters they share scenes with:\n" + (partners.Count == 0 ? "(none)" : partnerText) + "\n\n" +
                   "pitch: how high or low, e.g. \"Low, chest voice\". pace: e.g. \"Slow and deliberate\". texture: e.g. \"Gravelly, breathy at the ends\". " +
                   "attitude: how they come across, e.g. \"Gruff but warm\". contrast: how this voice differs from each character above by name, " +
                   "e.g. \"Lower and slower than Mara; rougher than Edda\", or \"\" if there is no one above.\n" +
                   "Reply as {\"pitch\":\"…\",\"pace\":\"…\",\"texture\":\"…\",\"attitude\":\"…\",\"contrast\":\"…\"}";
        return [new(ChatRole.System, System), new(ChatRole.User, user)];
    }

    public const string Grammar = """
        root ::= "{" ws "\"pitch\"" ws ":" ws string ws "," ws "\"pace\"" ws ":" ws string ws "," ws "\"texture\"" ws ":" ws string ws "," ws "\"attitude\"" ws ":" ws string ws "," ws "\"contrast\"" ws ":" ws string ws "}"
        string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] ){0,140} "\""
        ws ::= [ ]?
        """;

    public static VoiceBrief? Parse(string text)
    {
        if (JsonText.Extract(text) is not { } json || Replies.Decode<VoiceBrief>(json) is not { } b) return null;
        return new[] { b.Pitch, b.Pace, b.Texture, b.Attitude }.Any(x => x.Trim().Length > 0) ? b : null;
    }
}
