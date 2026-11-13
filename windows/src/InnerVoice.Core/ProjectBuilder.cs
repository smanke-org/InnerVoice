// Ported from Sources/InnerVoiceKit/ProjectBuilder.swift.

using System.Text.RegularExpressions;
using InnerVoice.Core.Import;

namespace InnerVoice.Core;

/// Turns an imported book into a project: finds the lines and runs the plain rules.
public static class ProjectBuilder
{
    public static Project Make(ImportedBook book, string fileName)
    {
        var project = new Project
        {
            Title = book.Title,
            Author = book.Author,
            SourceFileName = fileName,
            Chapters = book.Chapters,
        };
        project.Lines = DialogueFinder.FindAll(book.Chapters);
        RuleAttribution.Apply(project);
        ToneRules.Apply(project);
        return project;
    }
}

/// Finds names, places and made-up words a narrator may need to look up.
public static class PronunciationFinder
{
    public readonly record struct Candidate(string Term, int Occurrences, Guid FirstParagraphID);

    static readonly Regex Tokenizer = new(@"[\p{L}][\p{L}’'\-]*[\p{L}]|[\p{L}]", RegexOptions.CultureInvariant);

    /// `isKnownWord` says whether a lowercase word is in the dictionary. Capitalized words
    /// that appear mid-sentence and aren't dictionary words, and repeated non-dictionary
    /// lowercase words, are candidates.
    public static List<Candidate> Candidates(Project project, Func<string, bool> isKnownWord)
    {
        var counts = new Dictionary<string, int>();
        var first = new Dictionary<string, Guid>();
        var midSentenceCapital = new HashSet<string>();
        foreach (var chapter in project.Chapters)
        {
            foreach (var p in chapter.Paragraphs.Where(p => p.Style != ParagraphStyle.Heading))
            {
                bool previousEndsSentence = true;
                int cursor = 0;
                foreach (Match m in Tokenizer.Matches(p.Text))
                {
                    var gap = p.Text[cursor..m.Index];
                    if (gap.Any(ch => ".!?“\"‘:—".Contains(ch))) previousEndsSentence = true;
                    cursor = m.Index + m.Length;
                    var word = m.Value;
                    if (word.EndsWith("’s", StringComparison.Ordinal) || word.EndsWith("'s", StringComparison.Ordinal)) word = word[..^2];
                    if (word.Length < 3) { previousEndsSentence = false; continue; }
                    counts[word] = counts.GetValueOrDefault(word) + 1;
                    first.TryAdd(word, p.Id);
                    if (char.IsUpper(word[0]) && !previousEndsSentence) midSentenceCapital.Add(word);
                    previousEndsSentence = false;
                }
            }
        }
        var output = new List<Candidate>();
        var seenLower = new HashSet<string>();
        foreach (var (word, count) in counts)
        {
            var lower = word.ToLowerInvariant();
            if (Names.NotNames.Contains(word) || Names.Titles.Contains(word)) continue;
            bool capitalized = char.IsUpper(word[0]);
            if (capitalized)
            {
                if (!midSentenceCapital.Contains(word)) continue;
                if (isKnownWord(lower) || isKnownWord(word)) continue;
            }
            else
            {
                var cap = char.ToUpperInvariant(word[0]) + word[1..];
                if (count < 2 || isKnownWord(lower) || counts.ContainsKey(cap)) continue;
            }
            if (!seenLower.Add(lower)) continue;
            int occurrences = count + (capitalized ? 0 : counts.GetValueOrDefault(Capitalized(word)));
            output.Add(new Candidate(word, occurrences, first[word]));
        }
        return output.OrderByDescending(c => c.Occurrences).ThenBy(c => c.Term, StringComparer.Ordinal).ToList();
    }

    /// Swift's `String.capitalized`: first letter of each word upper, the rest lower.
    static string Capitalized(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();

    /// The word Fix Pronunciation… offers (the Mac's `pronunciationWord`): a selection of up
    /// to three words, else the word at `position`, without a possessive ending ('s, ’s) or
    /// punctuation around it, since one entry covers those. null when there's no word.
    public static string? WordToFix(string text, int position, TextSpan? selection = null)
    {
        var range = selection is { Length: > 0 } sel ? sel : WordAt(text, position);
        if (range.Length <= 0 || range.Location < 0 || range.End > text.Length) return null;
        var w = Trim(text.Substring(range.Location, range.Length));
        foreach (var end in new[] { "'s", "’s" })
            if (w.ToLowerInvariant().EndsWith(end, StringComparison.Ordinal)) w = w[..^2];
        if (w.Length == 0 || w.Length > 60 || w.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 3 || !w.Any(char.IsLetter)) return null;
        return w;
    }

    static string Trim(string s)
    {
        int a = 0, b = s.Length;
        while (a < b && (char.IsWhiteSpace(s[a]) || char.IsPunctuation(s[a]))) a++;
        while (b > a && (char.IsWhiteSpace(s[b - 1]) || char.IsPunctuation(s[b - 1]))) b--;
        return s[a..b];
    }

    /// The word around `position`, like double-clicking it: letters and digits, with
    /// apostrophes inside ("Barnaby's"); a hyphen ends it.
    static TextSpan WordAt(string text, int position)
    {
        bool InWord(int i) => i >= 0 && i < text.Length && (char.IsLetterOrDigit(text[i])
            || (text[i] is '\'' or '’' && i > 0 && i + 1 < text.Length && char.IsLetterOrDigit(text[i - 1]) && char.IsLetterOrDigit(text[i + 1])));
        int pos = Math.Clamp(position, 0, text.Length);
        if (!InWord(pos))
        {
            if (!InWord(pos - 1)) return new TextSpan(pos, 0);
            pos--;
        }
        int start = pos, end = pos + 1;
        while (InWord(start - 1)) start--;
        while (InWord(end)) end++;
        return new TextSpan(start, end - start);
    }

    /// Adds new candidates to the project's list, keeping the narrator's entries and dismissals.
    public static void Refresh(Project project, Func<string, bool> isKnownWord)
    {
        var found = Candidates(project, isKnownWord);
        var byTerm = new Dictionary<string, int>();
        for (int i = 0; i < project.Pronunciations.Count; i++) byTerm.TryAdd(project.Pronunciations[i].Term.ToLowerInvariant(), i);
        foreach (var c in found)
        {
            if (byTerm.TryGetValue(c.Term.ToLowerInvariant(), out var i))
            {
                project.Pronunciations[i].Occurrences = c.Occurrences;
                project.Pronunciations[i].FirstParagraphID = c.FirstParagraphID;
            }
            else
            {
                project.Pronunciations.Add(new Pronunciation { Term = c.Term, Occurrences = c.Occurrences, FirstParagraphID = c.FirstParagraphID });
                byTerm[c.Term.ToLowerInvariant()] = project.Pronunciations.Count - 1;
            }
        }
    }
}
