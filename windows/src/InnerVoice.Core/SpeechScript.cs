// Ported from the Mac's Sources/InnerVoiceKit/SpeechScript.swift: what is said aloud (each
// line in its speaker's voice and tone, the narration in the narrator's), the pauses between
// pieces, and pronunciations. The Mac splits sentences with Apple's NaturalLanguage; Windows
// has none, so `Sentences` is a rule-based splitter tested against the Mac's cases.

using System.Text.RegularExpressions;
using InnerVoice.Core.AI;

namespace InnerVoice.Core;

/// The gap after a piece of speech.
public enum PauseKind
{
    /// Between sentences in the same line or piece of narration.
    Sentence,
    /// Between a line and the narration around it ("…,” she said.).
    Line,
    Paragraph,
    SceneBreak,
    Heading,
    Chapter,
    /// No gap (a single sample line).
    None,
}

/// How long each pause is, in seconds at normal speed. Settings › Voices › Pauses.
public sealed record PauseSettings
{
    public double Sentence { get; set; } = 0.35;
    public double Line { get; set; } = 0.25;
    public double Paragraph { get; set; } = 0.7;
    public double SceneBreak { get; set; } = 1.5;
    public double Heading { get; set; } = 1.0;
    public double Chapter { get; set; } = 2.5;
    /// Speak one sentence at a time, so the gap between sentences is `Sentence` exactly
    /// rather than whatever the voice does on its own.
    public bool SplitSentences { get; set; } = true;

    public static PauseSettings Standard => new();
    public static PauseSettings Tight => new() { Sentence = 0.2, Line = 0.12, Paragraph = 0.45, SceneBreak = 1.0, Heading = 0.7, Chapter = 1.5 };
    public static PauseSettings Relaxed => new() { Sentence = 0.55, Line = 0.4, Paragraph = 1.1, SceneBreak = 2.2, Heading = 1.4, Chapter = 3.5 };

    /// The narrator's settings (the app sets this from Settings › Voices › Pauses); scripts
    /// use it unless they're given others.
    public static PauseSettings Current { get; set; } = new();

    public double Seconds(PauseKind kind) => kind switch
    {
        PauseKind.Sentence => Sentence,
        PauseKind.Line => Line,
        PauseKind.Paragraph => Paragraph,
        PauseKind.SceneBreak => SceneBreak,
        PauseKind.Heading => Heading,
        PauseKind.Chapter => Chapter,
        _ => 0,
    };
}

/// One piece of the book to say aloud: a line in its character's voice, or narration in
/// the narrator's.
public sealed record SpeechSegment
{
    public string Id { get; init; } = "";
    public Guid ParagraphID { get; init; }
    public Guid? LineID { get; init; }
    /// null is the narrator.
    public Guid? SpeakerID { get; init; }
    /// What the voice says, with pronunciations already applied.
    public string Text { get; init; } = "";
    public Tone Tone { get; init; } = Tone.Neutral;
    public LineKind? Kind { get; init; }
    /// Silence after it, in seconds.
    public double PauseAfter { get; set; } = 0.3;
    /// A sigh, laugh or gasp the speech tag asks for.
    public VocalSound? Sound { get; init; }
    /// What kind of gap follows, so the player can use the narrator's current pause settings
    /// without remaking the script.
    public PauseKind Pause { get; set; } = PauseKind.Line;
    /// The line's delivery hint ("barely audible"). Only ElevenLabs is sent it.
    public string? Delivery { get; init; }
    /// Where its words are in the paragraph's text (UTF-16), for read-along highlighting in an
    /// EPUB with audio. A sentence when the text splits the same way after pronunciations,
    /// otherwise the whole line or stretch of narration. null for a scene break.
    public TextSpan? Source { get; set; }
}

/// Splits text into sentences the way Apple's sentence tokenizer does for book prose: after
/// . ! ? or … (with any closing quotes or brackets), before a capital, digit or opening quote,
/// and at line breaks. Abbreviations ("Mr.", "e.g.") and initials ("J. R.") don't end one.
public static class SentenceSplitter
{
    static readonly HashSet<string> Abbreviations = new(StringComparer.OrdinalIgnoreCase)
    {
        "mr", "mrs", "ms", "dr", "st", "mt", "jr", "sr", "prof", "capt", "lt", "col", "gen", "sgt", "rev", "fr", "hon",
        "gov", "sen", "rep", "pres", "insp", "det", "supt", "cpl", "pvt", "adm", "cmdr", "maj", "messrs", "mme", "mlle",
        "vs", "etc", "no", "vol", "ch", "pp", "fig", "approx", "dept", "est", "inc", "ltd", "co", "corp", "ave", "rd", "blvd",
        "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    static readonly HashSet<char> Enders = ['.', '!', '?', '…'];
    static readonly HashSet<char> Closers = ['"', '”', '’', '\'', ')', ']', '»'];
    static readonly HashSet<char> Openers = ['"', '“', '‘', '\'', '(', '[', '«', '—', '–'];

    /// The sentences of `text` as ranges, trimmed of whitespace (one, when it has no break).
    public static List<TextSpan> Ranges(string text)
    {
        var output = new List<TextSpan>();
        int start = 0, i = 0, n = text.Length;
        void Emit(int end)
        {
            int a = start, b = end;
            while (a < b && char.IsWhiteSpace(text[a])) a++;
            while (b > a && char.IsWhiteSpace(text[b - 1])) b--;
            if (b > a) output.Add(new TextSpan(a, b - a));
            start = end;
        }
        while (i < n)
        {
            char c = text[i];
            if (c is '\n' or '\r' or '\u2029' or '\u2028')
            {
                Emit(i);
                i++;
                continue;
            }
            if (!Enders.Contains(c)) { i++; continue; }
            // The run of enders and closing marks.
            int j = i;
            while (j < n && (Enders.Contains(text[j]) || Closers.Contains(text[j]))) j++;
            if (j >= n) { i = j; break; }
            if (!char.IsWhiteSpace(text[j])) { i = j; continue; }   // "3.5", "U.S.A."
            int k = j;
            while (k < n && char.IsWhiteSpace(text[k]) && text[k] is not ('\n' or '\r')) k++;
            if (k >= n) { i = k; break; }
            char next = text[k];
            bool startsSentence = char.IsUpper(next) || char.IsDigit(next) || Openers.Contains(next) || text[k] is '\n' or '\r';
            if (startsSentence && c == '.' && IsAbbreviation(text, i)) startsSentence = false;
            if (startsSentence)
            {
                Emit(j);
                i = k;
            }
            else i = j;
        }
        Emit(n);
        return output;
    }

    /// The word before the period at `dot` is an abbreviation or an initial.
    static bool IsAbbreviation(string text, int dot)
    {
        int a = dot;
        while (a > 0 && (char.IsLetter(text[a - 1]) || text[a - 1] == '.')) a--;
        var word = text[a..dot];
        if (word.Length == 0) return false;
        // An initial: "J." (one capital letter), or dotted letters like "e.g".
        if (word.Length == 1 && char.IsUpper(word[0])) return true;
        if (word.Contains('.')) return true;
        return Abbreviations.Contains(word);
    }

    public static List<string> Sentences(string text)
    {
        var output = Ranges(text).Select(r => text.Substring(r.Location, r.Length)).ToList();
        return output.Count == 0 ? [text] : output;
    }
}

/// Turns paragraphs into what is said aloud: each line in its speaker's voice and tone,
/// and (when asked) the narration around them in the narrator's voice. Pronunciations
/// from the guide replace the words they cover. With `SplitSentences`, every sentence is
/// its own segment, so the pause between sentences is exactly as set.
public static class SpeechScript
{
    public static double ParagraphPause => PauseSettings.Standard.Paragraph;
    public static double ChapterPause => PauseSettings.Standard.Chapter;

    /// Sentences of a piece of text (one, when it has no sentence break).
    public static List<string> Sentences(string text) => SentenceSplitter.Sentences(text);

    public static List<SpeechSegment> Segments(Paragraph paragraph, Project project, BookIndex index,
                                               bool includeNarration, Guid? only = null, PauseSettings? pauses = null)
    {
        pauses ??= PauseSettings.Current;
        var say = Sayings(project);
        switch (paragraph.Style)
        {
            case ParagraphStyle.SceneBreak:
                if (!includeNarration) return [];
                return [new SpeechSegment { Id = $"{Id(paragraph.Id)}-0", ParagraphID = paragraph.Id, Text = "", PauseAfter = pauses.SceneBreak, Pause = PauseKind.SceneBreak }];
            case ParagraphStyle.Heading:
                if (!includeNarration || only is not null) return [];
                return [new SpeechSegment
                {
                    Id = $"{Id(paragraph.Id)}-0", ParagraphID = paragraph.Id, Text = Apply(say, paragraph.Text), PauseAfter = pauses.Heading,
                    Pause = PauseKind.Heading, Source = new TextSpan(0, paragraph.Text.Length),
                }];
        }
        var ns = paragraph.Text;
        var lines = index.LinesIn(paragraph.Id).Select(i => project.Lines[i]).OrderBy(l => l.Range.Location).ToList();
        var output = new List<SpeechSegment>();
        int cursor = 0, n = 0;

        // One piece (narration or a line), split into sentences when asked.
        void Add(string text, TextSpan range, Func<string, bool, SpeechSegment> make)
        {
            var parts = pauses.SplitSentences ? Sentences(text) : [text];
            var spans = pauses.SplitSentences ? SentenceRanges(ns, range) : [range];
            for (int k = 0; k < parts.Count; k++)
            {
                var seg = make(parts[k], k == 0);
                seg.Source = spans.Count == parts.Count ? spans[k] : range;
                seg.Pause = k == parts.Count - 1 ? PauseKind.Line : PauseKind.Sentence;
                seg.PauseAfter = pauses.Seconds(seg.Pause);
                output.Add(seg);
                n++;
            }
        }
        void Narration(int end)
        {
            if (end <= cursor) return;
            var raw = new TextSpan(cursor, end - cursor);
            var piece = Clean(ns.Substring(raw.Location, raw.Length));
            if (includeNarration && only is null && piece.Length > 0)
                Add(Apply(say, piece), CleanRange(piece, ns, raw), (text, _) =>
                    new SpeechSegment { Id = $"{Id(paragraph.Id)}-{n}", ParagraphID = paragraph.Id, Text = text });
        }
        foreach (var line in lines)
        {
            int loc = Math.Max(0, line.Range.Location), end = Math.Min(ns.Length, line.Range.End);
            if (end - loc <= 0 || loc < cursor) continue;
            var r = new TextSpan(loc, end - loc);
            Narration(r.Location);
            var text = Clean(ns.Substring(r.Location, r.Length));
            if (text.Length > 0 && (only is null || line.SpeakerID == only))
            {
                var sound = ToneRules.Sound(line, paragraph, lines);
                Add(Apply(say, text), CleanRange(text, ns, r), (part, first) => new SpeechSegment
                {
                    Id = $"{Id(paragraph.Id)}-{n}", ParagraphID = paragraph.Id, LineID = line.Id, SpeakerID = line.SpeakerID,
                    Text = part, Tone = line.Tone ?? Tone.Neutral, Kind = line.Kind, Sound = first ? sound : null,
                    Delivery = DeliveryPrompt.Clean(line.Delivery ?? ""),
                });
            }
            cursor = r.End;
        }
        Narration(ns.Length);
        if (output.Count > 0)
        {
            output[^1].Pause = PauseKind.Paragraph;
            output[^1].PauseAfter = pauses.Paragraph;
        }
        return output;
    }

    /// The picked chapters (indexes, any order, gaps allowed), read in book order; each ends
    /// with the chapter pause, as a run of chapters does.
    public static List<SpeechSegment> Segments(IEnumerable<int> chapters, Project project, BookIndex index, bool includeNarration,
                                               Guid? only = null, PauseSettings? pauses = null, bool recaps = false) =>
        chapters.Distinct().Order().Where(c => c >= 0 && c < project.Chapters.Count)
            .SelectMany(c => Segments(c, project, index, includeNarration, only: only, pauses: pauses, recaps: recaps)).ToList();

    /// The chapter's recap, read by the narrator: "Previously…", then the recap.
    public static List<SpeechSegment> RecapSegments(int c, Project project, PauseSettings? pauses = null)
    {
        pauses ??= PauseSettings.Current;
        if (c < 0 || c >= project.Chapters.Count || project.Chapters[c].RecapText is not { } recap) return [];
        var chapter = project.Chapters[c];
        var anchor = chapter.Paragraphs.FirstOrDefault(p => p.Style == ParagraphStyle.Heading)?.Id ?? chapter.Paragraphs.FirstOrDefault()?.Id ?? chapter.Id;
        var parts = new List<string> { "Previously…" };
        parts.AddRange(pauses.SplitSentences ? Sentences(recap) : [recap]);
        var say = Sayings(project);
        return parts.Select((part, k) =>
        {
            var pause = k == parts.Count - 1 ? PauseKind.Paragraph : PauseKind.Sentence;
            return new SpeechSegment
            {
                Id = $"{Id(chapter.Id)}-recap-{k}", ParagraphID = anchor, Text = k == 0 ? part : Apply(say, part),
                Pause = pause, PauseAfter = pauses.Seconds(pause),
            };
        }).ToList();
    }

    /// "Chapter 3", "Chapters 1–3, 5": picked chapter indexes as the narrator counts them (from 1).
    public static string ChapterLabel(IEnumerable<int> picked)
    {
        var n = picked.Distinct().Order().Select(x => x + 1).ToList();
        var runs = new List<string>();
        int i = 0;
        while (i < n.Count)
        {
            int j = i;
            while (j + 1 < n.Count && n[j + 1] == n[j] + 1) j++;
            runs.Add(j == i ? $"{n[i]}" : j == i + 1 ? $"{n[i]}, {n[j]}" : $"{n[i]}–{n[j]}");
            i = j + 1;
        }
        return (n.Count == 1 ? "Chapter " : "Chapters ") + string.Join(", ", runs);
    }

    /// A run of paragraphs, from `start` (a paragraph id) to the end of its chapter, or the
    /// whole chapter. `chapters` > 1 carries on into the following chapters.
    public static List<SpeechSegment> Segments(int chapter, Project project, BookIndex index, bool includeNarration, Guid? from = null,
                                               int chapters = 1, Guid? only = null, PauseSettings? pauses = null, bool recaps = false)
    {
        pauses ??= PauseSettings.Current;
        var output = new List<SpeechSegment>();
        for (int c = chapter; c < Math.Min(project.Chapters.Count, chapter + chapters); c++)
        {
            bool started = from is null || c != chapter;
            // "Previously…" after the chapter's title, when the whole chapter is read.
            var recap = recaps && started && includeNarration && only is null ? RecapSegments(c, project, pauses) : [];
            foreach (var p in project.Chapters[c].Paragraphs)
            {
                if (!started && p.Id == from) started = true;
                if (!started) continue;
                if (recap.Count > 0 && p.Style != ParagraphStyle.Heading)
                {
                    output.AddRange(recap);
                    recap = [];
                }
                output.AddRange(Segments(p, project, index, includeNarration, only, pauses));
            }
            output.AddRange(recap);
            if (output.Count > 0)
            {
                output[^1].Pause = PauseKind.Chapter;
                output[^1].PauseAfter = pauses.Chapter;
            }
        }
        return output;
    }

    /// Swift prints UUIDs in capitals; segment ids match the Mac's.
    static string Id(Guid g) => g.ToString().ToUpperInvariant();

    /// Where `cleaned` (a `Clean` of the text at `raw`) sits in the paragraph.
    static TextSpan CleanRange(string cleaned, string ns, TextSpan raw)
    {
        int at = ns.IndexOf(cleaned, raw.Location, raw.Length, StringComparison.Ordinal);
        return at < 0 ? raw : new TextSpan(at, cleaned.Length);
    }

    /// The sentences of `range` in the paragraph, as ranges, trimmed of spaces.
    static List<TextSpan> SentenceRanges(string ns, TextSpan range)
    {
        var text = ns.Substring(range.Location, range.Length);
        var output = SentenceSplitter.Ranges(text).Select(r => new TextSpan(range.Location + r.Location, r.Length)).ToList();
        return output.Count == 0 ? [range] : output;
    }

    const string CleanChars = "“”\"‘’'«»—–-,";

    /// Narration between quotes keeps stray quote marks and dashes; take them off.
    public static string Clean(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && (char.IsWhiteSpace(s[a]) || CleanChars.Contains(s[a]))) a++;
        while (b > a && (char.IsWhiteSpace(s[b - 1]) || CleanChars.Contains(s[b - 1]))) b--;
        return s[a..b];
    }

    public static List<(string Term, string Say)> Sayings(Project project) => project.Pronunciations
        .Where(p => !p.Dismissed && p.SayItAs.Trim().Length > 0)
        .Select(p => (p.Term, p.SayItAs))
        .OrderByDescending(x => x.Term.Length)
        .ToList();

    /// "Kerensa" → "keh-REN-sa" from the pronunciation guide, whole words only.
    public static string Apply(IEnumerable<(string Term, string Say)> sayings, string text)
    {
        var output = text;
        foreach (var (term, say) in sayings)
            output = Regex.Replace(output, @"\b" + Regex.Escape(term) + @"\b", say.Replace("$", "$$"), RegexOptions.CultureInvariant);
        return output;
    }
}
