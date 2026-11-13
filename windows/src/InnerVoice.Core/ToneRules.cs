// Ported from the Mac's Sources/InnerVoiceKit/ToneRules.swift: a line's tone from the words
// around it (before the AI), the sound a speech tag asks for, and the AI's tone pass that
// runs inside Tag Speakers (a per-chunk grammar that needs an answer for every line).

using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using InnerVoice.Core.AI;

namespace InnerVoice.Core;

/// Reads a line's tone from the words around it, before the AI: "she snapped" is angry,
/// "he whispered" is a whisper, "she sobbed" is sad. A clear speech tag is fairly sure;
/// an exclamation mark alone is only a guess for the AI to check; a line with no clue is
/// probably said plainly.
public static class ToneRules
{
    /// A clue word and the tone it means, matched as a whole word in the speech tag after
    /// the line or the narration just before it.
    static readonly (Tone Tone, string[] Words)[] Cues =
    [
        (Tone.Whispered, ["whispered", "whispering", "whisper", "murmured", "murmuring", "breathed", "muttered", "mumbled",
                          "under her breath", "under his breath", "under their breath", "hushed", "barely audible"]),
        (Tone.Angry, ["snapped", "snarled", "growled", "barked", "yelled", "shouted", "roared", "bellowed", "spat", "fumed",
                      "raged", "seethed", "thundered", "hissed", "angrily", "furiously", "fiercely", "sharply", "glared",
                      "scowled", "slammed", "stormed", "bristled", "through gritted teeth", "through clenched teeth"]),
        (Tone.Sad, ["sobbed", "sobbing", "wept", "weeping", "sniffled", "sniffed", "whimpered", "sighed", "sadly", "mournfully",
                    "tearfully", "miserably", "forlornly", "choked", "voice broke", "voice cracked", "her eyes filled", "his eyes filled", "wiped her eyes", "wiped his eyes"]),
        (Tone.Scared, ["stammered", "stuttered", "trembled", "trembling", "quavered", "shrieked", "screamed", "gasped",
                       "nervously", "fearfully", "frightened", "terrified", "panicked", "shakily", "voice shaking", "voice shook"]),
        (Tone.Excited, ["exclaimed", "squealed", "gushed", "whooped", "cheered", "beamed", "excitedly", "eagerly", "breathlessly",
                        "delighted", "thrilled", "giggled", "laughed", "crowed", "bounced"]),
        (Tone.Confused, ["puzzled", "confused", "bewildered", "baffled", "frowned", "blinked", "uncertainly", "hesitantly",
                         "hesitated", "wondered", "perplexed", "tilted her head", "tilted his head", "scratched his head", "scratched her head"]),
        (Tone.Tender, ["gently", "tenderly", "softly", "kindly", "warmly", "soothed", "soothingly", "cooed", "lovingly", "smiled"]),
        (Tone.Sarcastic, ["drawled", "sneered", "scoffed", "smirked", "mocked", "snorted", "dryly", "drily", "sarcastically",
                          "wryly", "rolled her eyes", "rolled his eyes", "rolled their eyes", "deadpan"]),
    ];

    /// Sounds a speech tag asks for, which engines that can make them (Orpheus) add.
    static readonly (VocalSound Sound, string[] Words)[] Sounds =
    [
        (VocalSound.Laugh, ["laughed", "laughing", "with a laugh", "giggled", "giggling"]),
        (VocalSound.Chuckle, ["chuckled", "chuckling", "with a chuckle"]),
        (VocalSound.Sigh, ["sighed", "sighing", "with a sigh"]),
        (VocalSound.Gasp, ["gasped", "gasping", "with a gasp"]),
        (VocalSound.Groan, ["groaned", "groaning", "with a groan"]),
        (VocalSound.Sniffle, ["sniffled", "sniffed", "sniffling"]),
        (VocalSound.Yawn, ["yawned", "yawning", "with a yawn"]),
        (VocalSound.Cough, ["coughed", "coughing"]),
    ];

    static readonly (Tone Tone, Regex Rx)[] CueRx = Cues.SelectMany(c => c.Words.Select(w => (c.Tone, WordRx(w)))).ToArray();
    static readonly (VocalSound Sound, Regex Rx)[] SoundRx = Sounds.SelectMany(c => c.Words.Select(w => (c.Sound, WordRx(w)))).ToArray();

    static Regex WordRx(string w) => new(@"\b" + Regex.Escape(w) + @"\b", RegexOptions.CultureInvariant);

    public const double SureCue = 0.85;
    /// "Said plainly" when nothing says otherwise: above the Unsure level, below what the
    /// AI is asked to look at again.
    public const double Plain = 0.65;
    public const double PunctuationGuess = 0.45;

    /// The narration that tells how a line is said: after it, up to the next line or 90
    /// characters, and the last sentence before it ("Her voice shook. “I can’t.”").
    static (string After, string Before) Context(Line line, Paragraph paragraph, IReadOnlyList<Line> linesInParagraph)
    {
        var text = paragraph.Text;
        int len = text.Length;
        int later = linesInParagraph.Where(l => l.Range.Location > line.Range.Location).Select(l => l.Range.Location).DefaultIfEmpty(len).Min();
        int afterStart = Math.Min(line.Range.End, len);
        int afterLength = Math.Max(0, Math.Min(Math.Min(later, line.Range.End + 90), len) - line.Range.End);
        int earlier = linesInParagraph.Where(l => l.Range.End <= line.Range.Location).Select(l => l.Range.End).DefaultIfEmpty(0).Max();
        int beforeStart = Math.Max(earlier, line.Range.Location - 90);
        int beforeLength = Math.Max(0, Math.Min(line.Range.Location, len) - beforeStart);
        string after = afterLength > 0 && afterStart + afterLength <= len ? text.Substring(afterStart, afterLength) : "";
        string before = beforeLength > 0 && beforeStart >= 0 ? text.Substring(beforeStart, beforeLength) : "";
        return (after.ToLowerInvariant(), LastSentence(before.ToLowerInvariant()));
    }

    /// The tone the words around a line suggest, with how sure that is.
    public static (Tone Tone, double Confidence) ToneFor(Line line, Paragraph paragraph, IReadOnlyList<Line> linesInParagraph)
    {
        var lineText = paragraph.Substring(line.Range);
        var (after, before) = Context(line, paragraph, linesInParagraph);
        if ((Cue(after) ?? Cue(before)) is { } t) return (t, SureCue);

        var trimmed = lineText.Trim();
        if (line.Kind == LineKind.Written) return (Tone.Neutral, 0.8);
        if (trimmed.Contains("?!") || trimmed.Contains("!?")) return (Tone.Confused, PunctuationGuess);
        if (trimmed.Contains('!')) return (Tone.Excited, PunctuationGuess);
        if (line.Kind == LineKind.Thought) return (Tone.Neutral, 0.7);
        return (Tone.Neutral, Plain);
    }

    static Tone? Cue(string text)
    {
        if (text.Length == 0) return null;
        (Tone Tone, int At)? best = null;
        foreach (var (tone, rx) in CueRx)
        {
            var m = rx.Match(text);
            if (!m.Success) continue;
            // The first clue in the tag wins ("she whispered angrily": a whisper).
            if (best is null || m.Index < best.Value.At) best = (tone, m.Index);
        }
        return best?.Tone;
    }

    /// The sound the narration around a line asks for ("she sighed" → a sigh).
    public static VocalSound? Sound(Line line, Paragraph paragraph, IReadOnlyList<Line> linesInParagraph)
    {
        if (line.Kind == LineKind.Written) return null;
        var (after, before) = Context(line, paragraph, linesInParagraph);
        foreach (var text in new[] { after, before })
        {
            if (text.Length == 0) continue;
            (VocalSound Sound, int At)? best = null;
            foreach (var (sound, rx) in SoundRx)
            {
                var m = rx.Match(text);
                if (!m.Success) continue;
                if (best is null || m.Index < best.Value.At) best = (sound, m.Index);
            }
            if (best is { } b) return b.Sound;
        }
        return null;
    }

    static readonly char[] QuoteTrim = ['“', '"', '‘', '\''];

    public static string LastSentence(string s)
    {
        // Whitespace and quote marks, trimmed as one set.
        int a = 0, b = s.Length;
        while (a < b && (char.IsWhiteSpace(s[a]) || QuoteTrim.Contains(s[a]))) a++;
        while (b > a && (char.IsWhiteSpace(s[b - 1]) || QuoteTrim.Contains(s[b - 1]))) b--;
        var trimmed = s[a..b];
        if (trimmed.Length == 0) return trimmed;
        int r = trimmed[..^1].LastIndexOfAny(['.', '!', '?']);
        return r < 0 ? trimmed : trimmed[(r + 1)..];
    }

    /// Sets the tone of every line the narrator and the AI haven't decided. Returns how
    /// many lines changed.
    public static int Apply(Project project, BookIndex? index = null)
    {
        index ??= new BookIndex(project);
        int changed = 0;
        foreach (var (pid, lineIdx) in index.LinesByParagraph)
        {
            if (project.Paragraph(pid, index) is not { } paragraph) continue;
            var lines = lineIdx.Select(i => project.Lines[i]).ToList();
            foreach (var i in lineIdx)
            {
                var line = project.Lines[i];
                if (line.ToneSource is not (null or ToneSource.Rule)) continue;
                var (t, c) = ToneFor(line, paragraph, lines);
                if (line.Tone != t || line.ToneConfidence != c || line.ToneSource != ToneSource.Rule)
                {
                    line.Tone = t;
                    line.ToneConfidence = c;
                    line.ToneSource = ToneSource.Rule;
                    changed++;
                }
            }
        }
        return changed;
    }
}

/// One tone answer from the AI.
public sealed class ToneReply
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("tone")] public string Tone { get; set; } = "";
    [JsonPropertyName("confidence")] public double? Confidence { get; set; }

    public ToneReply() { }
    public ToneReply(string id, string tone, double? confidence) { Id = id; Tone = tone; Confidence = confidence; }
}

/// Asking the AI how each line is said, with the paragraphs around it.
public static class TonePrompt
{
    public const string System =
        "You help an audiobook narrator prepare a novel. For each marked line, decide how the " +
        "character says it, from the words, the speech tag (\"she snapped\"), what just happened " +
        "and how the scene feels. Most lines are said plainly: use neutral unless the passage " +
        "gives a reason. Answer with JSON only.";

    /// Lines the AI should read: not set by the narrator or the AI, and not a clear tag.
    public static bool NeedsModel(Line line)
    {
        if (line.Kind == LineKind.Written) return false;
        return line.ToneSource switch
        {
            ToneSource.User or ToneSource.Ai => false,
            ToneSource.Rule => (line.ToneConfidence ?? 0) < ToneRules.SureCue,
            _ => true,
        };
    }

    public sealed class Chunk
    {
        public Dictionary<string, Guid> Labels { get; init; } = [];
        public List<ChatMessage> Messages { get; init; } = [];
        /// The labels in reading order.
        public List<string> Order { get; init; } = [];

        /// A grammar that needs one answer for every label, in order: the model can't reply
        /// with an empty list (Gemma 4 31B did, for every passage).
        public string Grammar
        {
            get
            {
                var items = Order.Select(l => "\"{\" ws \"\\\"id\\\"\" ws \":\" ws \"\\\"" + l + "\\\"\" ws \",\" ws rest");
                return "root ::= \"{\" ws \"\\\"lines\\\"\" ws \":\" ws \"[\" ws " + string.Join(" ws \",\" ws ", items) + " ws \"]\" ws \"}\"\n" + GrammarTail;
            }
        }
    }

    public static List<Chunk> Chunks(Project project, BookIndex index, int chapterIndex, int maxWords = 900)
    {
        var paragraphs = project.Chapters[chapterIndex].Paragraphs;
        var output = new List<Chunk>();
        int start = 0;
        while (start < paragraphs.Count)
        {
            int end = start, words = 0;
            while (end < paragraphs.Count && (words < maxWords || end == start))
            {
                words += paragraphs[end].WordCount;
                end++;
            }
            var asked = paragraphs.Skip(start).Take(end - start).SelectMany(p => index.LinesIn(p.Id)).Where(i => NeedsModel(project.Lines[i])).ToHashSet();
            if (asked.Count > 0)
                output.Add(Build(project, index, paragraphs.Skip(Math.Max(0, start - 1)).Take(end - Math.Max(0, start - 1)).ToList(), asked,
                                 project.Chapters[chapterIndex].Title));
            start = end;
        }
        return output;
    }

    static Chunk Build(Project project, BookIndex index, List<Paragraph> paragraphs, HashSet<int> asked, string title)
    {
        var labels = new Dictionary<string, Guid>();
        var order = new List<string>();
        var passage = new StringBuilder();
        int n = 0;
        foreach (var p in paragraphs)
        {
            var idx = index.LinesIn(p.Id).OrderBy(i => project.Lines[i].Range.Location).ToList();
            var text = new StringBuilder(p.Text);
            var inserts = new List<(int At, string Tag)>();
            foreach (var i in idx)
            {
                var line = project.Lines[i];
                var who = project.Character(line.SpeakerID)?.Name ?? "someone";
                var kind = line.Kind == LineKind.Spoken ? "" : " " + line.Kind.ToString().ToLowerInvariant();
                if (asked.Contains(i))
                {
                    n++;
                    var label = $"T{n}";
                    labels[label] = line.Id;
                    order.Add(label);
                    inserts.Add((line.Range.Location, $"{{{label}: {who}{kind}}}"));
                }
                else inserts.Add((line.Range.Location, $"{{{who}{kind}}}"));
            }
            foreach (var (at0, tag) in inserts.OrderByDescending(x => x.At))
            {
                int at = Math.Min(at0, text.Length);
                if (at > 0 && "“\"‘'«—".Contains(text[at - 1])) at--;
                text.Insert(at, tag);
            }
            passage.Append(text).Append("\n\n");
        }
        var tones = string.Join(", ", Tones.All.Select(t => t.Raw()));
        var user = $"Passage from “{title}”:\n\n{passage.ToString().Trim()}\n\n" +
                   $"For each of {string.Join(", ", order)}: the tone is one of {tones}. " +
                   "confidence is 0 to 100; give under 50 when the passage doesn't make it clear.\n" +
                   "Reply as {\"lines\":[{\"id\":\"T1\",\"tone\":\"neutral\",\"confidence\":80}]}";
        return new Chunk
        {
            Labels = labels, Order = order,
            Messages = [new ChatMessage(ChatRole.System, System), new ChatMessage(ChatRole.User, user)],
        };
    }

    /// What follows each answer's id.
    const string GrammarTail = """
        rest ::= "\"tone\"" ws ":" ws tone ws "," ws "\"confidence\"" ws ":" ws number ws "}"
        tone ::= "\"neutral\"" | "\"excited\"" | "\"confused\"" | "\"sad\"" | "\"angry\"" | "\"whispered\"" | "\"scared\"" | "\"tender\"" | "\"sarcastic\""
        number ::= [0-9] [0-9]? [0-9]?
        ws ::= [ ]?
        """;

    public const string Grammar = """
        root ::= "{" ws "\"lines\"" ws ":" ws "[" ws ( item ( ws "," ws item )* )? ws "]" ws "}"
        item ::= "{" ws "\"id\"" ws ":" ws string ws "," ws "\"tone\"" ws ":" ws tone ws "," ws "\"confidence\"" ws ":" ws number ws "}"
        tone ::= "\"neutral\"" | "\"excited\"" | "\"confused\"" | "\"sad\"" | "\"angry\"" | "\"whispered\"" | "\"scared\"" | "\"tender\"" | "\"sarcastic\""
        string ::= "\"" ( [^"\\\n] | "\\" ["\\/bfnrt] )* "\""
        number ::= [0-9] [0-9]? [0-9]?
        ws ::= [ ]?
        """;

    sealed class Wrapper { [JsonPropertyName("lines")] public List<ToneReply>? Lines { get; set; } }

    public static List<ToneReply>? Parse(string text)
    {
        if (JsonText.Extract(text) is { } json && Replies.Decode<Wrapper>(json)?.Lines is { } whole) return whole;
        var partial = JsonText.Objects(text).Select(Replies.Decode<ToneReply>).Where(r => r is not null).Cast<ToneReply>().ToList();
        return partial.Count == 0 ? null : partial;
    }

    static readonly Regex Label = new(@"T\d+", RegexOptions.CultureInvariant);

    /// Writes the tones into the project. Returns the lines before the change, for Undo.
    public static List<Line> Apply(IEnumerable<ToneReply> replies, Chunk chunk, Project project)
    {
        var lookup = new Dictionary<Guid, int>();
        for (int i = 0; i < project.Lines.Count; i++) lookup.TryAdd(project.Lines[i].Id, i);
        var before = new List<Line>();
        foreach (var reply in replies)
        {
            // The label alone: replies sometimes copy the speaker from the tag ("T1: Mara").
            var m = Label.Match(reply.Id);
            var label = m.Success ? m.Value : reply.Id;
            if (!chunk.Labels.TryGetValue(label, out var id) || !lookup.TryGetValue(id, out var i)) continue;
            var line = project.Lines[i];
            if (!NeedsModel(line) || Tones.Parse(reply.Tone.ToLowerInvariant()) is not { } tone) continue;
            before.Add(line.Copy());
            double c = Math.Min(1, Math.Max(0, (reply.Confidence ?? 60) / 100));
            // The words and the AI agreeing is worth more than either alone; an exclamation
            // mark the AI reads differently was only ever a guess.
            if (line.Tone == tone && line.ToneSource == ToneSource.Rule) c = Math.Max(c, Math.Min(0.97, (line.ToneConfidence ?? 0) + 0.2));
            line.Tone = tone;
            line.ToneConfidence = c;
            line.ToneSource = ToneSource.Ai;
        }
        return before;
    }
}
