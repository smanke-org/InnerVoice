// Ported from Sources/InnerVoiceKit/ProjectBuilder.swift. The tone rules (AI voices only)
// aren't run.

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
            // Made on Windows: Narration Tools.
            Settings = new ProjectSettings { Purpose = ProjectPurpose.Narration },
        };
        project.Lines = DialogueFinder.FindAll(book.Chapters);
        RuleAttribution.Apply(project);
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
