// Ported from Sources/InnerVoiceKit/RuleAttribution.swift (RuleAttribution and Exchange).

using System.Text.RegularExpressions;

namespace InnerVoice.Core;

/// Assigns speakers with plain rules: "said Tom", "Tom asked", a quote that carries on,
/// one speaker per paragraph, a named character's action beat, and a two-person
/// back-and-forth. Never changes lines the narrator or the AI assigned.
public static class RuleAttribution
{
    static readonly string[] Verbs =
    [
        "said", "says", "asked", "asks", "replied", "replies", "answered", "whispered", "shouted", "yelled", "cried",
        "called", "muttered", "murmured", "snapped", "added", "continued", "began", "told", "explained", "insisted",
        "demanded", "admitted", "agreed", "argued", "laughed", "sighed", "growled", "hissed", "breathed", "repeated",
        "offered", "suggested", "warned", "wondered", "mumbled", "screamed", "exclaimed", "protested", "pleaded",
        "stammered", "retorted", "chuckled", "grumbled", "drawled", "barked", "sobbed", "gasped", "snarled", "teased",
        "joked", "announced", "declared", "observed", "remarked", "noted", "whimpered", "croaked", "rasped", "purred",
        "thought", "wrote", "read", "sang", "promised", "agreed", "corrected", "interrupted", "conceded", "confirmed",
    ];
    static readonly string VerbGroup = "(?:" + string.Join("|", Verbs) + ")";
    static readonly string TitleGroup = "(?:(?:" + string.Join("|", Names.Titles.Select(Regex.Escape)) + @")\s+)?";
    static readonly string NameGroup = "(" + TitleGroup + @"\p{Lu}[\p{L}’'\-]+(?:\s+\p{Lu}[\p{L}’'\-]+){0,2})";
    const string Lead = @"^[\s,.;:—–!?…”“""’‘«»]*";
    const RegexOptions Opts = RegexOptions.CultureInvariant;

    static readonly Regex AfterVerbName = new(Lead + VerbGroup + @"\s+" + NameGroup, Opts);
    static readonly Regex AfterNameVerb = new(Lead + NameGroup + @"\s+(?:\p{Ll}+ly\s+)?" + VerbGroup + @"\b", Opts);
    static readonly Regex AfterPronoun = new(Lead + @"(?:(I|he|she)\s+(?:\p{Ll}+ly\s+)?" + VerbGroup + @"\b|" + VerbGroup + @"\s+(I|he|she)\b)", Opts | RegexOptions.IgnoreCase);
    static readonly Regex BeforeNameVerb = new(NameGroup + @"\s+(?:\p{Ll}+ly\s+)?" + VerbGroup + @"[^.!?""“”]{0,40}[,:]\s*[“""‘'«]?\s*$", Opts);
    static readonly Regex BeforePronoun = new(@"\b(I|he|she)\s+(?:\p{Ll}+ly\s+)?" + VerbGroup + @"[^.!?""“”]{0,40}[,:]\s*[“""‘'«]?\s*$", Opts | RegexOptions.IgnoreCase);

    /// The confidence of a guess the rules can't be sure of: below the Unsure line, so the
    /// narrator still checks it, but the name is there.
    public const double UnsureGuess = 0.5;

    /// Runs every rule over the lines that have no assignment yet. Returns pronoun hints
    /// (line id → "he", "she", "i") for the AI.
    public static Dictionary<Guid, string> Apply(Project project)
    {
        var hints = new Dictionary<Guid, string>();
        var byParagraph = new BookIndex(project).LinesByParagraph;

        void Set(int i, Guid speaker, double confidence, AssignmentSource source)
        {
            var l = project.Lines[i];
            if (l.Source != AssignmentSource.None) return;
            l.SpeakerID = speaker;
            l.Confidence = confidence;
            l.Source = source;
        }

        // 1. Speech tags next to the line.
        foreach (var chapter in project.Chapters)
        {
            foreach (var paragraph in chapter.Paragraphs)
            {
                if (!byParagraph.TryGetValue(paragraph.Id, out var indices)) continue;
                var text = paragraph.Text;
                for (int n = 0; n < indices.Count; n++)
                {
                    int i = indices[n];
                    var line = project.Lines[i];
                    if (line.Kind == LineKind.Written) continue;
                    int afterStart = line.Range.End;
                    int afterEnd = n + 1 < indices.Count ? project.Lines[indices[n + 1]].Range.Location : text.Length;
                    int beforeStart = n > 0 ? project.Lines[indices[n - 1]].Range.End : 0;
                    var after = afterEnd > afterStart ? Slice(text, afterStart, afterEnd - afterStart) : "";
                    var before = line.Range.Location > beforeStart ? Slice(text, beforeStart, line.Range.Location - beforeStart) : "";
                    var name = Match(AfterVerbName, after) ?? Match(AfterNameVerb, after) ?? Match(BeforeNameVerb, before);
                    if (name is not null && Resolve(name, project, create: true) is { } speaker)
                    {
                        Set(i, speaker, 0.95, AssignmentSource.Rule);
                    }
                    else if ((Match(AfterPronoun, after) ?? Match(BeforePronoun, before)) is { } pronoun)
                    {
                        if (pronoun.Equals("i", StringComparison.OrdinalIgnoreCase) && project.Settings.PovCharacterID is { } pov)
                            Set(i, pov, 0.9, AssignmentSource.Rule);
                        else
                            hints[line.Id] = pronoun.ToLowerInvariant();
                    }
                }
            }
        }

        byParagraph = new BookIndex(project).LinesByParagraph;
        foreach (var chapter in project.Chapters)
        {
            Guid? previousParagraphSpeaker = null;
            var history = new List<Guid?>(); // speaker of each recent dialogue paragraph, null for narration
            foreach (var paragraph in chapter.Paragraphs)
            {
                if (!byParagraph.TryGetValue(paragraph.Id, out var indices) || indices.Count == 0)
                {
                    if (Words.Count(paragraph.Text) > 0) history.Add(null);
                    previousParagraphSpeaker = null;
                    continue;
                }
                // 2. A quote that carries on from the last paragraph keeps its speaker.
                foreach (var i in indices)
                    if (project.Lines[i].ContinuesPrevious && previousParagraphSpeaker is { } s) Set(i, s, 0.9, AssignmentSource.Rule);
                // 3. One speaker per paragraph.
                var spoken = indices.Where(i => project.Lines[i].Kind == LineKind.Spoken).ToList();
                var known = spoken.Select(i => project.Lines[i].SpeakerID).FirstOrDefault(x => x is not null);
                if (known is { } k) foreach (var i in spoken) Set(i, k, 0.85, AssignmentSource.Rule);
                // 4. An action beat: the only character named in the narration is the speaker.
                var paraLines = indices.Select(i => project.Lines[i]).ToList();
                if (spoken.Any(i => project.Lines[i].SpeakerID is null))
                {
                    var narration = NarrationText(paragraph, paraLines);
                    var named = project.Characters.Where(c => c.AllNames.Any(n => ContainsWord(narration, n))).ToList();
                    if (named.Count == 1)
                    {
                        foreach (var i in spoken) Set(i, named[0].Id, 0.7, AssignmentSource.Rule);
                    }
                    else if (named.Count > 1 && spoken.Count > 0 &&
                             SentenceSubject(project.Lines[spoken[0]].Range.Location, paragraph, paraLines, named) is { } subject)
                    {
                        // 4b. Several people named: the one doing something in the sentence just
                        // before the line probably speaks. A guess, so it stays Unsure.
                        foreach (var i in spoken) Set(i, subject, UnsureGuess, AssignmentSource.Rule);
                    }
                }
                // 5. A back-and-forth between two people with no tags alternates.
                bool dialogueOnly = Words.Count(NarrationText(paragraph, paraLines)) <= 3;
                if (dialogueOnly && spoken.All(i => project.Lines[i].SpeakerID is null) && history.Count >= 2 &&
                    history[^2] is { } a && history[^1] is { } b && a != b)
                {
                    foreach (var i in spoken) Set(i, a, 0.65, AssignmentSource.Exchange);
                }
                var speaker = spoken.Select(i => project.Lines[i].SpeakerID).FirstOrDefault(x => x is not null);
                history.Add(dialogueOnly || speaker is not null ? speaker : null);
                if (history.Count > 6) history.RemoveAt(0);
                previousParagraphSpeaker = project.Lines[indices[^1]].SpeakerID;
            }
        }
        return hints;
    }

    static string Slice(string s, int start, int length)
    {
        start = Math.Clamp(start, 0, s.Length);
        length = Math.Clamp(length, 0, s.Length - start);
        return s.Substring(start, length);
    }

    static readonly char[] SentenceEnds = ['.', '!', '?', '…'];
    static readonly char[] SubjectTrim = [.. " \t\n\r “”\"‘’'«»—–,;:"];

    /// The character who starts the last sentence of narration before `location` in the
    /// paragraph (the sentence's subject): "Cyrus flipped …" is Cyrus.
    public static Guid? SentenceSubject(int location, Paragraph paragraph, List<Line> lines, List<BookCharacter> candidates)
    {
        var text = paragraph.Text;
        int end = Math.Min(location, text.Length);
        var narration = new System.Text.StringBuilder();
        int cursor = 0;
        foreach (var l in lines.OrderBy(l => l.Range.Location))
        {
            if (l.Range.Location >= end) continue;
            if (l.Range.Location > cursor) narration.Append(Slice(text, cursor, l.Range.Location - cursor));
            narration.Append(" . ");
            cursor = Math.Max(cursor, l.Range.End);
        }
        if (end > cursor) narration.Append(Slice(text, cursor, end - cursor));
        var sentences = narration.ToString().Split(SentenceEnds)
            .Select(s => s.Trim(SubjectTrim))
            .Where(s => Words.Count(s) >= 2)
            .ToList();
        if (sentences.Count == 0) return null;
        var last = sentences[^1];
        var names = candidates.SelectMany(c => c.AllNames.Select(n => (c.Id, Name: n))).OrderByDescending(t => t.Name.Length);
        foreach (var (id, name) in names)
        {
            if (!last.StartsWith(name + " ", StringComparison.Ordinal)) continue;
            var rest = last[(name.Length + 1)..];
            if (rest.Length > 0 && char.IsLower(rest[0])) return id;
        }
        return null;
    }

    static string? Match(Regex regex, string text)
    {
        var m = regex.Match(text);
        if (!m.Success) return null;
        for (int g = 1; g < m.Groups.Count; g++)
            if (m.Groups[g].Success) return m.Groups[g].Value;
        return null;
    }

    static readonly char[] NarrationTrim = [.. " “”\"‘’'«»—–"];

    /// Text in a paragraph that is not inside any line.
    public static string NarrationText(Paragraph paragraph, IEnumerable<Line> lines)
    {
        var text = paragraph.Text;
        var output = new System.Text.StringBuilder();
        int cursor = 0;
        foreach (var line in lines.OrderBy(l => l.Range.Location))
        {
            if (line.Range.Location > cursor) output.Append(Slice(text, cursor, line.Range.Location - cursor)).Append(' ');
            cursor = Math.Max(cursor, line.Range.End);
        }
        if (cursor < text.Length) output.Append(text[cursor..]);
        return output.ToString().Trim(NarrationTrim);
    }

    public static bool ContainsWord(string text, string word)
    {
        if (word.Length == 0) return false;
        int from = 0;
        while (from <= text.Length - word.Length)
        {
            int r = text.IndexOf(word, from, StringComparison.Ordinal);
            if (r < 0) return false;
            bool beforeLetter = r > 0 && char.IsLetter(text[r - 1]);
            int e = r + word.Length;
            bool afterLetter = e < text.Length && char.IsLetter(text[e]);
            if (!beforeLetter && !afterLetter) return true;
            from = e;
        }
        return false;
    }

    static readonly Regex PossessiveEnd = new("’s$", RegexOptions.CultureInvariant);

    /// Finds the character a name refers to, or adds one.
    public static Guid? Resolve(string rawName, Project project, bool create)
    {
        var cleaned = PossessiveEnd.Replace(rawName.Trim(' ', ',', '.', '’', '\''), "");
        // "Then Tom" is Tom.
        var words = cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).SkipWhile(Names.NotNames.Contains).ToList();
        if (words.Count == 0) return null;
        var name = string.Join(" ", words);
        var c1 = project.Characters.FirstOrDefault(c => c.Name == name || c.Aliases.Contains(name));
        if (c1 is not null) return c1.Id;
        var c2 = project.Characters.FirstOrDefault(c => c.AllNames.Contains(name));
        if (c2 is not null) return c2.Id;
        if (words.Count > 1)
        {
            var c3 = project.Characters.FirstOrDefault(c => words.Any(w => !Names.Titles.Contains(w) && c.AllNames.Contains(w)));
            if (c3 is not null) return c3.Id;
        }
        if (!create || name.Length < 2 || words.All(Names.Titles.Contains)) return null;
        var character = new BookCharacter(name, CharacterPalette.ForNew(name, project.Characters.Select(c => c.ColorHex)));
        project.Characters.Add(character);
        return character.Id;
    }
}

/// Fixing one line of a back-and-forth usually means the rest of it is off by one.
public static class Exchange
{
    public readonly record struct Proposal(Guid LineID, Guid SpeakerID);

    /// After the narrator gives the line `lineID` to a new speaker, proposes the alternating
    /// speakers for the dialogue paragraphs that follow, up to narration or a line the
    /// narrator assigned. `previousSpeaker` is the speaker the line had before.
    public static List<Proposal> Proposals(Project project, Guid lineID, Guid? previousSpeaker)
    {
        var index = new BookIndex(project);
        var output = new List<Proposal>();
        if (!index.LineIndexByID.TryGetValue(lineID, out var li)) return output;
        var line = project.Lines[li];
        if (line.SpeakerID is not { } newSpeaker || !index.Positions.TryGetValue(line.ParagraphID, out var pos)) return output;
        var chapter = project.Chapters[pos.Chapter];
        Guid? other = null;
        if (pos.Paragraph > 0 && index.LinesByParagraph.TryGetValue(chapter.Paragraphs[pos.Paragraph - 1].Id, out var prevLines) &&
            prevLines.Count > 0 && project.Lines[prevLines[0]].SpeakerID is { } s && s != newSpeaker)
            other = s;
        if (other is null && previousSpeaker is { } p && p != newSpeaker) other = p;
        if (other is not { } otherSpeaker) return output;
        bool expectOther = true;
        foreach (var para in chapter.Paragraphs.Skip(pos.Paragraph + 1))
        {
            if (!index.LinesByParagraph.TryGetValue(para.Id, out var indices) || indices.Count == 0) break;
            var lines = indices.Select(i => project.Lines[i]).Where(l => l.Kind == LineKind.Spoken).ToList();
            if (lines.Count == 0) break;
            var narration = RuleAttribution.NarrationText(para, lines);
            if (Words.Count(narration) > 3 || lines.Any(l => l.Source == AssignmentSource.User || (l.Source == AssignmentSource.Rule && l.Confidence >= 0.95))) break;
            var expected = expectOther ? otherSpeaker : newSpeaker;
            foreach (var l in lines.Where(l => l.SpeakerID != expected)) output.Add(new Proposal(l.Id, expected));
            expectOther = !expectOther;
        }
        return output;
    }
}
