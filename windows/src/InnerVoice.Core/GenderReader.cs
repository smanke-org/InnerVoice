// The gender part of VoiceCasting.profile (Sources/InnerVoiceKit/Voices.swift), which the
// color families need. The rest of voice casting is AI voices only and isn't ported.

using System.Text.RegularExpressions;

namespace InnerVoice.Core;

public enum Gender { Female, Male, Neutral }

public static class GenderReader
{
    static readonly HashSet<string> FemaleWords =
    [
        "woman", "female", "girl", "lady", "mother", "daughter", "wife", "sister", "queen", "princess", "aunt", "grandmother",
        "granny", "widow", "mrs", "miss", "ms", "madam", "she", "her", "mum", "mom", "niece", "duchess", "countess", "nun",
        "actress", "waitress", "maid", "matron", "dame",
    ];
    static readonly HashSet<string> MaleWords =
    [
        "man", "male", "boy", "gentleman", "father", "son", "husband", "brother", "king", "prince", "uncle", "grandfather",
        "grandpa", "widower", "mr", "sir", "lord", "he", "his", "him", "dad", "nephew", "duke", "earl", "monk", "priest",
        "waiter", "lad", "fellow", "chap", "da",
    ];
    static readonly HashSet<string> FemaleTitles =
        ["Mrs.", "Mrs", "Miss", "Ms.", "Ms", "Lady", "Dame", "Aunt", "Queen", "Princess", "Madam", "Madame", "Señora", "Mother", "Sister", "Granny", "Grandma"];
    static readonly HashSet<string> MaleTitles =
        ["Mr.", "Mr", "Mister", "Sir", "Lord", "Uncle", "King", "Prince", "Señor", "Father", "Brother", "Grandpa"];

    /// Lowercased runs of letters (Swift: components separated by non-letters).
    public static List<string> WordsIn(string s)
    {
        var output = new List<string>();
        var sb = new System.Text.StringBuilder();
        foreach (var r in s.ToLowerInvariant().EnumerateRunes())
        {
            var cat = System.Text.Rune.GetUnicodeCategory(r);
            bool letter = System.Text.Rune.IsLetter(r) || cat is System.Globalization.UnicodeCategory.NonSpacingMark
                or System.Globalization.UnicodeCategory.SpacingCombiningMark or System.Globalization.UnicodeCategory.EnclosingMark;
            if (letter) sb.Append(r.ToString());
            else if (sb.Length > 0) { output.Add(sb.ToString()); sb.Clear(); }
        }
        if (sb.Length > 0) output.Add(sb.ToString());
        return output;
    }

    /// "Mrs. Penrose" is a woman and "Sir Alec" a man, before the book says anything else.
    public static Core.Gender? TitleGender(string name)
    {
        var first = name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (first is null) return null;
        if (FemaleTitles.Contains(first)) return Core.Gender.Female;
        if (MaleTitles.Contains(first)) return Core.Gender.Male;
        return null;
    }

    public static Core.Gender? GenderIn(string text)
    {
        var w = WordsIn(text);
        if (w.Count == 0) return null;
        int f = w.Count(FemaleWords.Contains), m = w.Count(MaleWords.Contains);
        return f > m ? Core.Gender.Female : m > f ? Core.Gender.Male : null;
    }

    /// Gender in the same order as the Mac: the summary's gender fact, a title, "she said"
    /// after their lines, the voice notes, then the other facts.
    public static Core.Gender? GenderOf(BookCharacter c, Project project, BookIndex index)
    {
        string FactText(params FactKind[] kinds) => string.Join(" ", c.Facts.Where(f => kinds.Contains(f.Kind)).Select(f => f.Text));
        if (GenderIn(FactText(FactKind.Gender)) is { } g1) return g1;
        if (TitleGender(c.Name) is { } g2) return g2;
        if (PronounGender(c.Id, project, index) is { } g3) return g3.Gender;
        if (GenderIn(c.VoiceNotes) is { } g4) return g4;
        return GenderIn(FactText(FactKind.Relationships, FactKind.Appearance, FactKind.Age, FactKind.History, FactKind.Other));
    }

    static readonly Lock CacheLock = new();
    static readonly Dictionary<int, (Core.Gender Gender, bool Sure)> Cache = [];

    /// Pronoun results by what they depend on, so redrawing while the narrator types costs
    /// nothing: the scan reads the whole manuscript.
    public static (Core.Gender Gender, bool Sure)? PronounGender(Guid id, Project project, BookIndex index)
    {
        var h = new HashCode();
        h.Add(id);
        foreach (var c in project.Characters) { h.Add(c.Id); foreach (var n in c.AllNames) h.Add(n); }
        foreach (var line in project.Lines.Where(l => l.SpeakerID == id)) { h.Add(line.ParagraphID); h.Add(line.Range); }
        foreach (var chapter in project.Chapters) { h.Add(chapter.Paragraphs.Count); foreach (var p in chapter.Paragraphs) h.Add(p.Text.Length); }
        int key = h.ToHashCode();
        lock (CacheLock)
        {
            if (Cache.TryGetValue(key, out var hit)) return hit.Sure || hit.Gender != Core.Gender.Neutral ? hit : null;
        }
        var result = ScanPronouns(id, project, index);
        lock (CacheLock)
        {
            if (Cache.Count > 2000) Cache.Clear();
            Cache[key] = result ?? (Core.Gender.Neutral, false);
        }
        return result;
    }

    static readonly Regex SheAfter = new(@"^[\s,.!?…—–”""’']*she\s+[a-z]+", RegexOptions.CultureInvariant);
    static readonly Regex HeAfter = new(@"^[\s,.!?…—–”""’']*he\s+[a-z]+", RegexOptions.CultureInvariant);
    static readonly HashSet<string> Pronouns = ["she", "her", "hers", "herself", "he", "his", "him", "himself"];

    static (Core.Gender Gender, bool Sure)? ScanPronouns(Guid id, Project project, BookIndex index)
    {
        int she = 0, he = 0;
        foreach (var line in project.Lines.Where(l => l.SpeakerID == id))
        {
            var p = project.Paragraph(line.ParagraphID, index);
            if (p is null) continue;
            int start = line.Range.End;
            if (start >= p.Text.Length) continue;
            var after = p.Text.Substring(start, Math.Min(30, p.Text.Length - start)).ToLowerInvariant();
            if (SheAfter.IsMatch(after)) she += 2;
            if (HeAfter.IsMatch(after)) he += 2;
        }
        var me = project.Character(id);
        if (me is null) return null;
        var mine = me.AllNames;
        var others = project.Characters.Where(c => c.Id != id).SelectMany(c => c.AllNames).Where(o => !mine.Contains(o)).ToList();
        foreach (var chapter in project.Chapters)
        {
            foreach (var p in chapter.Paragraphs)
            {
                if (!mine.Any(n => RuleAttribution.ContainsWord(p.Text, n))) continue;
                foreach (var sentence in p.Text.Split(['.', '!', '?']))
                {
                    var name = mine.FirstOrDefault(n => RuleAttribution.ContainsWord(sentence, n));
                    if (name is null || others.Any(o => RuleAttribution.ContainsWord(sentence, o))) continue;
                    int r = sentence.IndexOf(name, StringComparison.Ordinal);
                    if (r < 0) continue;
                    var first = WordsIn(sentence[(r + name.Length)..]).FirstOrDefault(Pronouns.Contains);
                    if (first is null) continue;
                    if (first.StartsWith('h') && first is not ("her" or "hers" or "herself")) he++; else she++;
                }
            }
        }
        if (she >= 2 && she > he * 2) return (Core.Gender.Female, true);
        if (he >= 2 && he > she * 2) return (Core.Gender.Male, true);
        if (she == 1 && he == 0) return (Core.Gender.Female, false);
        if (he == 1 && she == 0) return (Core.Gender.Male, false);
        return null;
    }
}
