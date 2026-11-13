using System.Text;
using System.Text.Json;

namespace InnerVoice.Core.Tests;

/// Ports of the Mac's Tests/InnerVoiceKitTests/PronunciationMatchTests.swift.
public class PronunciationMatchTests
{
    static string Say(string text) => SpeechScript.Apply([new SpeechScript.Saying("Barnaby", "BAR-nuh-bee")], text);

    [Theory]
    [InlineData("Barnaby ran.", "BAR-nuh-bee ran.")]
    [InlineData("Barnaby's hat", "BAR-nuh-bee's hat")]
    [InlineData("Barnaby’s hat", "BAR-nuh-bee’s hat")]
    [InlineData("the Barnabys’ house", "the BAR-nuh-bees’ house")]
    [InlineData("BARNABY!", "BAR-nuh-bee!")]
    [InlineData("Barnabyish and SirBarnaby", "Barnabyish and SirBarnaby")]
    public void PronunciationCoversPossessivesPluralsAndCase(string text, string expected) =>
        Assert.Equal(expected, Say(text));

    [Fact]
    public void PluralsWholeWordsMatchCaseAndLiteralSpellings()
    {
        Assert.Equal("the RAWSSes", SpeechScript.Apply([new("Ross", "RAWSS")], "the Rosses"));
        Assert.Equal("James", SpeechScript.Apply([new("Jam", "jahm")], "James"));
        Assert.Equal("Vill will go.", SpeechScript.Apply([new("Will", "Vill", MatchCase: true)], "Will will go."));
        Assert.Equal("$1 an", SpeechScript.Apply([new("Ann", "$1 an")], "Ann"));
    }

    [Fact]
    public void SayingsAreTrimmedLongestFirstAndSkipEmptyTerms()
    {
        var p = new Project
        {
            Pronunciations =
            [
                new Pronunciation { Term = " Ann ", SayItAs = " an ", MatchCase = true },
                new Pronunciation { Term = "Annabel", SayItAs = "ANN-uh-bel" },
                new Pronunciation { Term = "  ", SayItAs = "nothing" },
                new Pronunciation { Term = "Kerensa", SayItAs = "" },
                new Pronunciation { Term = "Gone", SayItAs = "gon", Dismissed = true },
            ],
        };
        var sayings = SpeechScript.Sayings(p);
        Assert.Equal([new SpeechScript.Saying("Annabel", "ANN-uh-bel"), new SpeechScript.Saying("Ann", "an", true)], sayings);
    }

    [Fact]
    public void MatchCaseRoundTripsAndOldFilesRead()
    {
        const string old = """{"id":"9C1B5C1E-6C47-4E43-9B39-2F9E5C2B8A11","term":"X","sayItAs":"","notes":"","confirmed":false,"occurrences":0,"dismissed":false,"addedByUser":false}""";
        var p = JsonSerializer.Deserialize<Pronunciation>(old, ProjectJson.Options)!;
        Assert.False(p.MatchCase);
        // Off is left out of the file, like the Mac writes it.
        Assert.DoesNotContain("matchCase", JsonSerializer.Serialize(p, ProjectJson.Options));
        var q = p.Copy();
        q.MatchCase = true;
        var json = JsonSerializer.Serialize(q, ProjectJson.Options);
        Assert.Contains("\"matchCase\":true", json);
        var back = JsonSerializer.Deserialize<Pronunciation>(json, ProjectJson.Options)!;
        Assert.True(back.MatchCase);
        Assert.True(back.Copy().MatchCase);

        // Through a whole project.json too.
        var project = new Project { Pronunciations = [q] };
        var again = ProjectJson.Decode(ProjectJson.Encode(project));
        Assert.True(again.Pronunciations[0].MatchCase);
        Assert.Null(again.Pronunciations[0].Extra);
    }

    [Fact]
    public void PlayButtonSaysTheFirstSentenceWithEveryEntry()
    {
        var para = new Paragraph("The tide was out. Barnaby met Kerensa at the quay. Then rain.");
        var p = new Project
        {
            Chapters = [new Chapter("One", [para])],
            Pronunciations = [new Pronunciation { Term = "Kerensa", SayItAs = "keh-REN-sa" }],
        };
        var index = new BookIndex(p);
        // Not in the project yet, so not in Sayings: its own spelling is used anyway.
        var entry = new Pronunciation { Term = "Barnaby", SayItAs = "BAR-nuh-bee", FirstParagraphID = para.Id };
        Assert.Equal("BAR-nuh-bee met keh-REN-sa at the quay.", SpeechScript.PronunciationSample(entry, p, index));
        // Without a first use, the term alone.
        Assert.Equal("BAR-nuh-bee", SpeechScript.PronunciationSample(new Pronunciation { Term = "Barnaby", SayItAs = "BAR-nuh-bee" }, p, index));
        // A long sentence: the term alone.
        var longPara = new Paragraph("Barnaby " + string.Join(" ", Enumerable.Repeat("walked", 45)) + ".");
        p.Chapters[0].Paragraphs.Add(longPara);
        index = new BookIndex(p);
        Assert.Equal("Barnaby", SpeechScript.PronunciationSample(new Pronunciation { Term = "Barnaby", FirstParagraphID = longPara.Id }, p, index));
    }

    [Fact]
    public void FixPronunciationTakesTheClickedWordOrASelection()
    {
        const string text = "“Barnaby’s hat,” said the Barnabys’ cook, Jean-Luc. 42!";
        Assert.Equal("Barnaby", PronunciationFinder.WordToFix(text, text.IndexOf("rnaby’s", StringComparison.Ordinal)));
        Assert.Equal("Barnabys", PronunciationFinder.WordToFix(text, text.IndexOf("Barnabys", StringComparison.Ordinal) + 3));
        // At the end of a word (the click lands after its last letter).
        Assert.Equal("hat", PronunciationFinder.WordToFix(text, text.IndexOf("hat", StringComparison.Ordinal) + 3));
        Assert.Equal("Luc", PronunciationFinder.WordToFix(text, text.IndexOf("Luc", StringComparison.Ordinal)));
        Assert.Null(PronunciationFinder.WordToFix(text, text.IndexOf("42", StringComparison.Ordinal)));
        Assert.Null(PronunciationFinder.WordToFix(text, text.IndexOf(", Jean", StringComparison.Ordinal) + 1));
        // A selection: up to three words, punctuation trimmed.
        int at = text.IndexOf("the Barnabys", StringComparison.Ordinal);
        Assert.Equal("the Barnabys", PronunciationFinder.WordToFix(text, at, new TextSpan(at, "the Barnabys’ ".Length)));
        int from = text.IndexOf("said", StringComparison.Ordinal);
        Assert.Null(PronunciationFinder.WordToFix(text, from, new TextSpan(from, "said the Barnabys’ cook".Length)));
    }
}
