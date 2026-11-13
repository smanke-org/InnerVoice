using System.Text.Json;
using System.Text.Json.Nodes;
using InnerVoice.Core.AI;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Ports of the Kit's ClaudeFeatureTests: cost estimate, Clean Up Text, Chapter Recaps,
/// Delivery Hints, Suggest Spellings and older files. (The SpeechScript parts wait for the
/// voices work.) Pure logic; no network.
public class ClaudeFeatureTests
{
    static Project Make(params string[][] chapters) =>
        ProjectBuilder.Make(new ImportedBook("Test", "", chapters.Select((ps, n) =>
            new Chapter($"Chapter {n + 1}", ps.Select(t => new Paragraph(t)).ToList())).ToList()), "t");

    static List<string> Texts(Project p)
    {
        var index = new BookIndex(p);
        return p.Lines.Select(l => p.TextOf(l, index)).ToList();
    }

    // MARK: Cost estimate

    [Fact]
    public void CostEstimateUsesListPricesAndShowsTokensForUnknownModels()
    {
        var e = new CostEstimate(1_000_000, 100_000);
        Assert.Equal(3.0, e.Dollars("claude-sonnet-5-5")!.Value, 6);
        Assert.True(e.Dollars("claude-haiku-5-5") < 0.16);
        Assert.Equal(12.0, e.Dollars("claude-fable-5-2")!.Value, 6);
        Assert.Equal(6.0, e.Dollars("claude-opus-5-5")!.Value, 6);
        Assert.Equal("about $3.00", e.Phrase("claude-sonnet-5-5"));
        Assert.Null(e.Dollars("claude-future-9"));
        Assert.Equal("about 1,100,000 tokens", e.Phrase("claude-future-9"));
        Assert.Contains("estimate", e.Sentence("claude-sonnet-5-5"));
        Assert.Contains("estimate", e.Sentence("claude-future-9"));
        Assert.Equal("less than $0.01", new CostEstimate(10, 10).Phrase("claude-haiku-5-5"));
        Assert.Equal("about $12", new CostEstimate(3_000_000, 0).Phrase("claude-opus-5-5"));
    }

    [Fact]
    public void CostEstimateGrowsWithTheBook()
    {
        var small = Make(["“Hello,” said Tom. The rain fell on the town all night long."]);
        var big = Make(Enumerable.Repeat("“Hello,” said Tom. The rain fell on the town all night long and nobody slept at all.", 400).ToArray());
        var a = CostEstimator.TagSpeakers(small);
        var b = CostEstimator.TagSpeakers(big);
        Assert.True(a.InputTokens > 0 && b.InputTokens > a.InputTokens * 5);
        Assert.True(b.OutputTokens > a.OutputTokens);
        // Only the picked chapters count.
        var two = Make(Enumerable.Repeat("Words and more words in a long chapter of the book.", 200).ToArray(), ["Short."]);
        Assert.True(CostEstimator.TagSpeakers(two, [1]).InputTokens < CostEstimator.TagSpeakers(two, [0]).InputTokens);
        Assert.True(CostEstimator.BuildSummaries(big, 4).InputTokens > CostEstimator.BuildSummaries(big, 1).InputTokens);
        var req = CostEstimator.Requests([[new ChatMessage(ChatRole.User, string.Concat(Enumerable.Repeat("abcd", 1000)))]], 100);
        Assert.True(req.InputTokens >= 1000);
        Assert.Equal(200, req.OutputTokens);
    }

    // MARK: Clean Up Text

    [Fact]
    public void CleanupChunksLabelBodyParagraphsAndParseReplies()
    {
        var p = Make(["The ﬁrst tbe night.", "12", "“Go,” said Tom."]);
        var chunks = CleanupPrompt.Chunks(p);
        Assert.Single(chunks);
        Assert.Equal(3, chunks[0].Labels.Count);
        Assert.Contains("[P1] The ﬁrst tbe night.", chunks[0].Messages[1].Content);
        const string reply = """{"fixes":[{"paragraph":"P1","find":"tbe night","replace":"the night","issue":"ocr","note":"tbe"},{"paragraph":"P2","find":"12","replace":"","issue":"page-number"},{"paragraph":"P3","find":"not there","replace":"x"},{"paragraph":"P1","find":"The ﬁrst tbe night.","replace":"It was a dark and stormy night, said the author.","issue":"other"}]}""";
        var parsed = CleanupPrompt.Parse(reply);
        Assert.Equal(4, parsed?.Count);
        var fixes = CleanupPrompt.Fixes(parsed!, chunks[0], p, new BookIndex(p));
        // The made-up text and the rewrite are refused.
        Assert.Equal(2, fixes.Count);
        Assert.Equal(CleanupIssue.Ocr, fixes[0].Issue);
        Assert.Equal(CleanupIssue.PageNumber, fixes[^1].Issue);
    }

    [Fact]
    public void CleanupKeepsLinesAndTheNarratorsAssignments()
    {
        var p = Make(["Intro text here.", "“Go now, tbe boat leaves,” said Tom. “Hurry.”", "34", "Then she left."]);
        var tom = new BookCharacter("Tom", "#3366CC");
        p.Characters = [tom];
        var paraID = p.Chapters[0].Paragraphs[1].Id;
        var pageID = p.Chapters[0].Paragraphs[2].Id;
        // The narrator assigned the first line; the second is the AI's, with a tone and a hint
        // from the Mac (kept in Extra on Windows).
        var here = p.Lines.Where(l => l.ParagraphID == paraID).ToList();
        Assert.Equal(2, here.Count);
        here[0].SpeakerID = tom.Id;
        here[0].Source = AssignmentSource.User;
        here[1].SpeakerID = tom.Id;
        here[1].Source = AssignmentSource.Ai;
        here[1].Extra = new() { ["tone"] = JsonSerializer.SerializeToElement("scared") };
        here[1].Delivery = "out of breath";
        var ids = here.Select(l => l.Id).ToList();
        var fixes = new[]
        {
            new CleanupFix(paraID, "tbe boat", "the boat", CleanupIssue.Ocr),
            new CleanupFix(pageID, "34", "", CleanupIssue.PageNumber),
        };
        var applied = TextCleanup.Apply(fixes, p);
        Assert.Equal(fixes.Select(f => f.Id).ToHashSet(), applied.ToHashSet());
        Assert.Equal(3, p.Chapters[0].Paragraphs.Count);
        Assert.Equal("“Go now, the boat leaves,” said Tom. “Hurry.”", p.Chapters[0].Paragraphs[1].Text);
        // Same lines, same ids, valid ranges, owners kept.
        var lines = p.Lines.Where(l => l.ParagraphID == paraID).ToList();
        Assert.Equal(ids, lines.Select(l => l.Id));
        Assert.Contains("Go now, the boat leaves,", Texts(p));
        Assert.Contains("Hurry.", Texts(p));
        Assert.True(lines[0].Source == AssignmentSource.User && lines[0].SpeakerID == tom.Id);
        Assert.Equal("scared", lines[1].Extra!["tone"].GetString());
        Assert.Equal("out of breath", lines[1].Delivery);
        Assert.Equal(Tone.Scared, Tones.ToneOf(lines[1]));
        var paragraphs = p.Chapters.SelectMany(c => c.Paragraphs).ToDictionary(x => x.Id);
        Assert.All(p.Lines, l => Assert.True(l.Range.End <= paragraphs[l.ParagraphID].Text.Length));
    }

    [Fact]
    public void CleanupNeverLosesALineTheNarratorAssigned()
    {
        var p = Make(["“Wait for me,” said Ann."]);
        var pid = p.Chapters[0].Paragraphs[0].Id;
        p.Lines[0].Source = AssignmentSource.User;
        var before = ProjectJson.Encode(p);
        // Removing the quote marks would turn the line into narration: refused, text unchanged.
        var applied = TextCleanup.Apply([new CleanupFix(pid, "“Wait for me,”", "Wait for me,")], p);
        Assert.Empty(applied);
        Assert.Equal(before, ProjectJson.Encode(p));
    }

    [Fact]
    public void CleanupShiftsItalicsAndKeepsTheOnlyParagraph()
    {
        Assert.Equal([new TextSpan(8, 5)], TextCleanup.Shift([new TextSpan(10, 5)], new TextSpan(0, 4), 2));
        Assert.Equal([new TextSpan(0, 3)], TextCleanup.Shift([new TextSpan(0, 3)], new TextSpan(5, 4), 2));
        var p = Make(["99"]);
        var pid = p.Chapters[0].Paragraphs[0].Id;
        TextCleanup.Apply([new CleanupFix(pid, "99", "", CleanupIssue.PageNumber)], p);
        Assert.Single(p.Chapters[0].Paragraphs);
        var preview = TextCleanup.Preview(new CleanupFix(pid, "tbe", "the"), "And tbe end.");
        Assert.Equal(("And tbe end.", "And the end."), preview);
    }

    [Fact]
    public void CleanupRefusesRewrites()
    {
        Assert.True(CleanupPrompt.IsRepair("con- tinue", "continue", wholeParagraph: false));
        Assert.True(CleanupPrompt.IsRepair("T H E  E N D", "THE END", wholeParagraph: false));
        Assert.True(CleanupPrompt.IsRepair("night.12 She", "night. She", wholeParagraph: false));
        Assert.False(CleanupPrompt.IsRepair("He walked home.", "He strolled back to his house.", wholeParagraph: false));
        Assert.True(CleanupPrompt.IsRepair("THE LONG ROAD 143", "", wholeParagraph: true));
        Assert.False(CleanupPrompt.IsRepair(string.Concat(Enumerable.Repeat("word ", 30)), "", wholeParagraph: true));
    }

    // MARK: Chapter Recaps

    [Fact]
    public void RecapJobsUseThePreviousStoryChapterAndKeepTheNarratorsOwn()
    {
        var story = Enumerable.Repeat("Mara found the key under the stone and ran to the harbor before dawn.", 15).ToArray();
        var p = Make(story, ["Part Two"], story, story);
        var jobs = RecapPrompt.Jobs(p);
        // Chapter 1 has nothing before it; "Part Two" is too short to recap from but gets a recap.
        Assert.Equal([p.Chapters[1].Id, p.Chapters[2].Id, p.Chapters[3].Id], jobs.Select(j => j.ChapterID));
        Assert.Contains("(Chapter 1)", jobs[1].Messages[1].Content);
        RecapPrompt.Edit("Mara has the key.", p.Chapters[2].Id, p);
        Assert.True(p.Chapters[2].RecapEdited);
        Assert.False(RecapPrompt.Put("Something else.", p.Chapters[2].Id, p));
        Assert.Equal("Mara has the key.", p.Chapters[2].RecapText);
        Assert.Equal([p.Chapters[1].Id, p.Chapters[3].Id], RecapPrompt.Jobs(p).Select(j => j.ChapterID));
        Assert.Single(RecapPrompt.Jobs(p, [3]));
        RecapPrompt.Edit("  ", p.Chapters[2].Id, p);
        Assert.Null(p.Chapters[2].Recap);
        Assert.Null(p.Chapters[2].RecapEdited);
    }

    [Fact]
    public void RecapRepliesAreTidied()
    {
        Assert.Equal("Mara found the key and fled.", RecapPrompt.Parse("""{"recap":"Previously… Mara found the key and fled."}"""));
        Assert.Null(RecapPrompt.Parse("no json"));
        var longText = string.Join(" ", Enumerable.Range(0, 30).Select(n => $"Sentence {n} has five words."));
        var parsed = RecapPrompt.Parse($$"""{"recap":"{{longText}}"}""") ?? "";
        Assert.True(Words.Count(parsed) <= RecapPrompt.MaximumWords && parsed.EndsWith('.'), parsed);
        Assert.True(Words.Count(parsed) > 60);
    }

    [Fact]
    public void RecapIsSpokenAsPreviouslyThenItsSentences()
    {
        var chapter = new Chapter("Two", [new Paragraph("It went on.")]) { Recap = "It began. Then more." };
        Assert.Equal(["Previously…", "It began.", "Then more."], RecapPrompt.SpokenParts(chapter));
        Assert.Empty(RecapPrompt.SpokenParts(new Chapter("One", [])));
    }

    // MARK: Delivery Hints

    static void SetTone(Line l, string tone) => (l.Extra ??= [])["tone"] = JsonSerializer.SerializeToElement(tone);

    [Fact]
    public void DeliveryChunksMarkLinesAndProposalsSkipTheNarratorsHints()
    {
        var p = Make(["“Not here,” Mara whispered. “Not now.”"]);
        var mara = new BookCharacter("Mara", "#3366CC");
        p.Characters = [mara];
        foreach (var l in p.Lines) { l.SpeakerID = mara.Id; SetTone(l, "whispered"); }
        p.Lines[1].Delivery = "pleading";
        p.Lines[1].DeliverySource = ToneSource.User;
        var index = new BookIndex(p);
        var chunks = DeliveryPrompt.Chunks(p, index);
        Assert.True(chunks.Count == 1 && chunks[0].Labels.Count == 1);
        Assert.Contains("{L1 Mara, whispered}“Not here,”", chunks[0].Messages[1].Content);
        var replies = DeliveryPrompt.Parse("""{"lines":[{"line":"L1","hint":"[Barely Audible]"},{"line":"L2","hint":"shouts"}]}""") ?? [];
        var proposals = DeliveryPrompt.Proposals(replies, chunks[0], p, index);
        Assert.Equal(["barely audible"], proposals.Select(x => x.Hint));
        DeliveryPrompt.Apply([.. proposals, new DeliveryProposal(p.Lines[1].Id, "shouts")], p);
        Assert.True(p.Lines[0].Delivery == "barely audible" && p.Lines[0].DeliverySource == ToneSource.Ai);
        Assert.Equal("pleading", p.Lines[1].Delivery);
        // The Mac's field names and values.
        var json = JsonNode.Parse(ProjectJson.Encode(p))!["lines"]![0]!;
        Assert.Equal("barely audible", (string?)json["delivery"]);
        Assert.Equal("ai", (string?)json["deliverySource"]);
        Assert.Equal("whispered", (string?)json["tone"]);
    }

    [Fact]
    public void DeliveryHintsAreCleanedForElevenLabs()
    {
        Assert.Equal("whispers loud aside", DeliveryPrompt.Clean("[whispers] {loud} (aside)"));
        Assert.Null(DeliveryPrompt.Clean("  "));
        Assert.Equal("one two three four five six", DeliveryPrompt.Clean("one two three four five six seven"));
        Assert.Equal("[sad, barely audible]", DeliveryPrompt.AudioTag(Tone.Sad, "barely audible"));
        Assert.Equal("[laughing]", DeliveryPrompt.AudioTag(Tone.Neutral, "laughing"));
        Assert.Equal("[whispers, slowly]", DeliveryPrompt.AudioTag(Tone.Whispered, "slowly"));
        Assert.Null(DeliveryPrompt.AudioTag(null, null));
        var p = Make(["“Run,” said Tom."]);
        DeliveryPrompt.Set("through gritted teeth", [p.Lines[0].Id], p);
        Assert.Equal(ToneSource.User, p.Lines[0].DeliverySource);
        DeliveryPrompt.Set("", [p.Lines[0].Id], p);
        Assert.Null(p.Lines[0].Delivery);
        Assert.Null(p.Lines[0].DeliverySource);
    }

    // MARK: Suggest Spellings

    [Fact]
    public void SpellingsSendOnlyTermsAndTheirSentenceAndNeverOverwrite()
    {
        var p = Make(["The road was long. Siobhan waited by the gate of Dunmore."]);
        var pid = p.Chapters[0].Paragraphs[0].Id;
        p.Pronunciations =
        [
            new Pronunciation { Term = "Siobhan", Occurrences = 3, FirstParagraphID = pid },
            new Pronunciation { Term = "Dunmore", SayItAs = "dun-MORE", Occurrences = 1, FirstParagraphID = pid },
            new Pronunciation { Term = "Gone", Dismissed = true },
        ];
        var index = new BookIndex(p);
        var chunks = SpellingPrompt.Chunks(p, index);
        Assert.True(chunks.Count == 1 && chunks[0].Labels.Count == 1);
        var user = chunks[0].Messages[1].Content;
        Assert.Contains("[T1] Siobhan — “Siobhan waited by the gate of Dunmore.”", user);
        Assert.DoesNotContain("The road was long", user);
        var proposals = SpellingPrompt.Parse("""{"terms":[{"id":"T1","sayItAs":"shiv-AWN","note":"Irish name","confidence":90},{"id":"T9","sayItAs":"x"}]}""", chunks[0], p);
        Assert.Equal("shiv-AWN", Assert.Single(proposals!).SayItAs);
        Assert.Equal(1, SpellingPrompt.Apply(proposals!, p));
        Assert.True(p.Pronunciations[0].SayItAs == "shiv-AWN" && !p.Pronunciations[0].Confirmed);
        Assert.Contains("Claude", p.Pronunciations[0].Notes);
        // Filled in meanwhile: kept.
        Assert.Equal(0, SpellingPrompt.Apply([new SpellingProposal(p.Pronunciations[1].Id, "Dunmore", "DUN-mor")], p));
        Assert.Equal("dun-MORE", p.Pronunciations[1].SayItAs);
    }

    // MARK: Older files

    [Fact]
    public void ProjectsWithoutTheNewFieldsStillOpenAndTheMacsFieldsRoundTrip()
    {
        var p = Make(["“Hi,” said Tom."]);
        var back = ProjectJson.Decode(ProjectJson.Encode(p));
        Assert.Null(back.Chapters[0].Recap);
        Assert.False(JsonNode.Parse(ProjectJson.Encode(p))!["chapters"]![0]!.AsObject().ContainsKey("recap"));
        // A Mac file with a recap and a delivery hint.
        var json = JsonNode.Parse(ProjectJson.Encode(p))!;
        json["chapters"]![0]!["recap"] = "Tom said hello.";
        json["chapters"]![0]!["recapEdited"] = true;
        json["lines"]![0]!["delivery"] = "barely audible";
        json["lines"]![0]!["deliverySource"] = "ai";
        var mac = ProjectJson.Decode(System.Text.Encoding.UTF8.GetBytes(json.ToJsonString()));
        Assert.Equal("Tom said hello.", mac.Chapters[0].RecapText);
        Assert.True(mac.Chapters[0].RecapEdited);
        var again = JsonNode.Parse(ProjectJson.Encode(mac))!;
        Assert.Equal("barely audible", (string?)again["lines"]![0]!["delivery"]);
        Assert.Equal("ai", (string?)again["lines"]![0]!["deliverySource"]);
        Assert.Equal("Tom said hello.", (string?)again["chapters"]![0]!["recap"]);
    }
}
