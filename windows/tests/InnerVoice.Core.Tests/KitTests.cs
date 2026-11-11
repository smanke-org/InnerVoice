using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Ports of the Mac's Kit unit tests (Tests/InnerVoiceKitTests/InnerVoiceKitTests.swift) whose
/// subject exists in the C# port. Each test keeps the Swift name in PascalCase.
public class KitTests
{
    // MARK: - Helpers

    static Paragraph Para(string text, List<TextSpan>? italic = null, ParagraphStyle style = ParagraphStyle.Body) =>
        new(text, style, italic);

    static ImportedBook Book(params string[] paragraphs) =>
        new("Test", "", [new Chapter("Chapter 1", paragraphs.Select(t => Para(t)).ToList())]);

    static string? SpeakerOf(string prefix, Project p)
    {
        var index = new BookIndex(p);
        var line = p.Lines.FirstOrDefault(l => p.TextOf(l, index).StartsWith(prefix, StringComparison.Ordinal));
        return p.Character(line?.SpeakerID)?.Name;
    }

    static Line? LineStarting(string prefix, Project p)
    {
        var index = new BookIndex(p);
        return p.Lines.FirstOrDefault(l => p.TextOf(l, index).StartsWith(prefix, StringComparison.Ordinal));
    }

    static TextSpan SpanOf(string text, string part)
    {
        int i = text.IndexOf(part, StringComparison.Ordinal);
        Assert.True(i >= 0, $"“{part}” not in “{text}”");
        return new TextSpan(i, part.Length);
    }

    static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "InnerVoiceTests-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        return dir;
    }

    // MARK: - Chapters

    [Fact]
    public void HeadingsAreRecognized()
    {
        foreach (var h in new[] { "Chapter 1", "CHAPTER TWELVE", "Chapter Twenty-One", "Prologue", "Part II", "Epilogue: After", "7", "ch. 3" })
            Assert.True(ChapterDetector.LooksLikeHeading(h), h);
        foreach (var h in new[] { "“Chapter one,” she said.", "The chapter was long and the night was longer than any before it.", "I was seven" })
            Assert.False(ChapterDetector.LooksLikeHeading(h), h);
    }

    [Fact]
    public void ChaptersSplitAtHeadings()
    {
        var blocks = new[]
        {
            new RawBlock("My Book"), new RawBlock("Chapter 1"), new RawBlock("It began."),
            new RawBlock("Chapter 2"), new RawBlock("The Storm", [], HeadingLevel: 2), new RawBlock("It rained."),
        };
        var chapters = ChapterDetector.Chapters(blocks, "x");
        Assert.Equal(["Chapter 1", "Chapter 2: The Storm"], chapters.Select(c => c.Title));
        Assert.Equal(["It rained."], chapters[1].Paragraphs.Select(p => p.Text));
    }

    [Fact]
    public void SceneBreaksAreKept()
    {
        Assert.True(ChapterDetector.IsSceneBreak("* * *"));
        Assert.True(ChapterDetector.IsSceneBreak("⁂"));
        Assert.False(ChapterDetector.IsSceneBreak("Yes."));
    }

    // MARK: - Dialogue

    [Fact]
    public void CurlyStraightAndUKQuotes()
    {
        var curly = DialogueFinder.FindAll([new Chapter("c", [Para("“Hello,” she said. “Come in.”")])]);
        Assert.Equal(2, curly.Count);
        var straight = DialogueFinder.FindAll([new Chapter("c", [Para("\"Hello there,\" he said.")])]);
        Assert.Single(straight);
        var uk = Enumerable.Range(0, 12).Select(_ => Para("‘Don’t go,’ she said. ‘It’s late.’")).ToList();
        var ukLines = DialogueFinder.FindAll([new Chapter("c", uk)]);
        Assert.Equal(DialogueFinder.QuoteStyle.Single, DialogueFinder.DetectStyle(uk));
        Assert.Equal(24, ukLines.Count);
    }

    [Fact]
    public void NestedQuotesStayInside()
    {
        var p = Para("“She told me, ‘Run,’ and I ran,” he said.");
        var lines = new DialogueFinder(DialogueFinder.QuoteStyle.Double).Find(p, openFromPrevious: false).Lines;
        Assert.Single(lines);
        Assert.Equal("She told me, ‘Run,’ and I ran,", p.Substring(lines[0].Range));
    }

    [Fact]
    public void QuotesAcrossParagraphsContinue()
    {
        var chapters = new List<Chapter> { new("c", [Para("“It was long ago. We were young."), Para("“Then the war came.”")]) };
        var lines = DialogueFinder.FindAll(chapters);
        Assert.Equal(2, lines.Count);
        Assert.True(lines[1].ContinuesPrevious);
    }

    [Fact]
    public void DashDialogue()
    {
        var p = Para("—Where are you going? —asked Juan.");
        var lines = new DialogueFinder(DialogueFinder.QuoteStyle.Double).Find(p, openFromPrevious: false).Lines;
        Assert.Single(lines);
        Assert.Equal("Where are you going?", p.Substring(lines[0].Range));
    }

    [Fact]
    public void ItalicThoughtsAndWrittenText()
    {
        var text = "He looked away. This cannot be happening, he thought.";
        var p = Para(text, [new TextSpan(16, 28)]);
        var lines = new DialogueFinder(DialogueFinder.QuoteStyle.Double).Find(p, openFromPrevious: false).Lines;
        Assert.NotEmpty(lines);
        Assert.Equal(LineKind.Thought, lines[0].Kind);
        var letter = Para("Dear John, I have gone away.", style: ParagraphStyle.BlockQuote);
        var written = new DialogueFinder(DialogueFinder.QuoteStyle.Double).Find(letter, openFromPrevious: false).Lines;
        Assert.NotEmpty(written);
        Assert.Equal(LineKind.Written, written[0].Kind);
    }

    [Fact]
    public void ShortMentionsAreNotLines()
    {
        var p = Para("He used the word “love” too often.");
        Assert.Empty(new DialogueFinder(DialogueFinder.QuoteStyle.Double).Find(p, openFromPrevious: false).Lines);
    }

    // MARK: - Rules

    [Fact]
    public void SpeechTagsAssignSpeakers()
    {
        var p = ProjectBuilder.Make(Book(
            "“You're late,” said Tom Hale.",
            "“The path flooded,” Mara replied.",
            "Then Tom Hale sighed. “Tea?”",
            "“Please,” said Mara.",
            "“Milk?”",
            "“No.”"), "t.txt");
        Assert.Equal("Tom Hale", SpeakerOf("You're late", p));
        Assert.Equal("Mara", SpeakerOf("The path flooded", p));
        Assert.Equal("Tom Hale", SpeakerOf("Tea?", p));
        Assert.Equal("Mara", SpeakerOf("Please", p));
        // The back-and-forth alternates: Tom, then Mara.
        Assert.Equal("Tom Hale", SpeakerOf("Milk?", p));
        Assert.Equal("Mara", SpeakerOf("No.", p));
        var index = new BookIndex(p);
        Assert.Equal(AssignmentSource.Exchange, p.Lines.First(l => p.TextOf(l, index) == "Milk?").Source);
    }

    [Fact]
    public void FirstNameMatchesFullName()
    {
        var p = new Project { Title = "t" };
        p.Characters = [new BookCharacter("Mara Quinn", "#10B981")];
        Assert.Equal(p.Characters[0].Id, RuleAttribution.Resolve("Mara", p, create: true));
        Assert.Equal(p.Characters[0].Id, RuleAttribution.Resolve("Then Mara", p, create: true));
        Assert.Null(RuleAttribution.Resolve("He", p, create: true));
        Assert.Single(p.Characters);
    }

    [Fact]
    public void ExchangeProposalsAlternate()
    {
        var p = ProjectBuilder.Make(Book("“Ready?” asked Ann.", "“Yes,” said Ben.", "“Now?”", "“Now.”", "“Go!”"), "t");
        var index = new BookIndex(p);
        var now = p.Lines.First(l => p.TextOf(l, index) == "Now?");
        var ann = p.Characters.First(c => c.Name == "Ann").Id;
        var ben = p.Characters.First(c => c.Name == "Ben").Id;
        // The rules said Ann; the narrator says it's Ben.
        var old = now.SpeakerID;
        int i = p.Lines.FindIndex(l => l.Id == now.Id);
        p.Lines[i].SpeakerID = ben;
        p.Lines[i].Source = AssignmentSource.User;
        var proposals = Exchange.Proposals(p, now.Id, old);
        Assert.Equal(2, proposals.Count);
        Assert.Equal(ann, proposals[0].SpeakerID);
    }

    [Fact]
    public void PaletteNeverRed()
    {
        Assert.All(CharacterPalette.Swatches, s => Assert.False(CharacterPalette.IsTooRed(s.Hex), s.Hex));
        Assert.True(CharacterPalette.IsTooRed("#E11D48") || CharacterPalette.IsTooRed("#EF4444"));
        var used = CharacterPalette.Swatches.Take(3).Select(s => s.Hex).ToList();
        Assert.DoesNotContain(CharacterPalette.Next(used), used);
    }

    // MARK: - AI replies

    [Fact]
    public void JsonExtractionToleratesChatter()
    {
        // The Swift test goes through AttributionPrompt.parse (not ported); this checks the
        // JSON extraction it relies on and reads the same fields.
        var reply = "<think>hmm</think>Sure! {\"lines\":[{\"id\":\"L1\",\"speaker\":\"Tom\",\"kind\":\"spoken\",\"confidence\":91}]} Done.";
        var json = JsonText.Extract(reply);
        Assert.NotNull(json);
        var first = JsonNode.Parse(json!)!["lines"]!.AsArray()[0]!;
        Assert.Equal("Tom", first["speaker"]!.GetValue<string>());
        Assert.Equal(91, first["confidence"]!.GetValue<int>());
    }

    [Fact]
    public void MainCharactersStartHiddenInTheIndex()
    {
        var lines = Enumerable.Repeat("“Yes,” said Ann.", 10).Append("“No,” said Ben.").ToArray();
        var p = ProjectBuilder.Make(Book(lines), "t");
        var index = new BookIndex(p);
        var ann = p.Characters.First(c => c.Name == "Ann");
        var ben = p.Characters.First(c => c.Name == "Ben");
        Assert.False(index.ShowsInIndex(ann));
        Assert.True(index.ShowsInIndex(ben));
    }

    [Fact]
    public void DuplicatesAreSuggested()
    {
        var p = new Project { Title = "t" };
        p.Characters = [new BookCharacter("Mara", "#111111"), new BookCharacter("Mara Quinn", "#222222"), new BookCharacter("Tom", "#333333")];
        var pairs = Duplicates.Pairs(p);
        Assert.Single(pairs);
        Assert.Equal(p.Characters[0].Id, pairs[0].Merge);
    }

    // MARK: - Importers

    [Fact]
    public void ImportsMarkdown()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "book.md");
            File.WriteAllText(path, "# Chapter 1\n\nThe wind rose.\n\n“Close the door,” said Tom.\n\n*This cannot last much longer,* she thought.\n\n# Chapter 2\n\n“Morning.”\n");
            var imported = ManuscriptImporter.Read(path);
            Assert.Equal(2, imported.Chapters.Count);
            Assert.Single(imported.Chapters[0].Paragraphs[2].Italic);
            Assert.DoesNotContain("*", imported.Chapters[0].Paragraphs[2].Text);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void PronunciationCandidates()
    {
        var p = ProjectBuilder.Make(Book("They walked to Penhallow with Siobhan.", "At Penhallow the gwyllion waited.", "The gwyllion howled."), "t");
        var known = new HashSet<string> { "walked", "they", "with", "waited", "the", "howled" };
        var found = PronunciationFinder.Candidates(p, known.Contains);
        var terms = found.Select(c => c.Term).ToHashSet();
        Assert.Contains("Penhallow", terms);
        Assert.Contains("Siobhan", terms);
        Assert.Contains("gwyllion", terms);
        Assert.DoesNotContain("They", terms);
    }

    // MARK: - Names

    [Fact]
    public void CharacterFirstNamesSkipTitles()
    {
        static string First(string n) => new BookCharacter(n, "#0EA5E9").FirstName;
        Assert.Equal("Mara", First("Mara Quinn"));
        Assert.Equal("Edda", First("Dr. Edda Voss"));
        Assert.Equal("Captain", First("Captain"));
        Assert.Equal("Mr. Hale", First("Mr. Hale"));
        Assert.Equal("Mister Waterford", First("Mister Waterford"));
        Assert.Equal("Grey-hair", First("Grey-hair"));
        Assert.Equal("W", new BookCharacter("Mister Waterford", "#0EA5E9").Initials);
    }

    // MARK: - Speaker order and pins

    [Fact]
    public void SpeakerOrderPutsPinsFirstByFirstName()
    {
        var p = new Project { Title = "T" };
        p.Characters =
        [
            new BookCharacter("Zed Arlo", "#0EA5E9"),
            new BookCharacter("Dr. Edda Voss", "#0EA5E9") { Pinned = true },
            new BookCharacter("Mara Quinn", "#0EA5E9"),
            new BookCharacter("Bram Toll", "#0EA5E9") { Pinned = true },
            new BookCharacter("Ada Wren", "#0EA5E9"),
        ];
        var order = p.SpeakerOrder();
        Assert.Equal(["Bram Toll", "Dr. Edda Voss"], order.Pinned.Select(c => c.Name));
        Assert.Equal(["Ada Wren", "Mara Quinn", "Zed Arlo"], order.Others.Select(c => c.Name));
        var edda = p.Characters[1];
        Assert.True(edda.Matches("edd"));
        Assert.True(edda.Matches("VOSS"));
        Assert.True(edda.Matches("ev"));
        Assert.False(edda.Matches("mara"));
    }

    // MARK: - Colors

    [Fact]
    public void AutomaticColorsFollowGender()
    {
        var p = ProjectBuilder.Make(Book(
            "“Ready?” asked Tom. He looked tired.", "“Yes,” said Ada. She smiled.", "“Now?” asked Tom.", "“Now,” said Ada.",
            "“Tea?” asked Quill.", "“Please,” said Mrs. Penrose.", "“Go,” said Bo.", "“Stop,” said Cal. He meant it."), "t");
        int I(string n) => p.Characters.FindIndex(c => c.Name == n);
        CharacterPalette.Family? FamilyOf(string n) => CharacterPalette.FamilyOf(p.Characters[I(n)].ColorHex);
        p.Characters[I("Tom")].Facts = [new Fact { Kind = FactKind.Gender, Text = "Male" }];
        p.Characters[I("Ada")].Facts = [new Fact { Kind = FactKind.Gender, Text = "Female" }];
        p.Characters[I("Bo")].ColorHex = "#336699";                    // picked with Other… before Automatic existed
        p.Characters[I("Cal")].ColorHex = "#EC4899";                   // pink, chosen by the narrator
        p.Characters[I("Cal")].ColorChosen = true;
        // A title picks the family the moment the character is made.
        Assert.Equal(CharacterPalette.Family.Feminine, FamilyOf("Mrs. Penrose"));
        CharacterPalette.RecolorAutomatic(p, new BookIndex(p));
        Assert.Equal(CharacterPalette.Family.Masculine, FamilyOf("Tom"));
        Assert.Equal(CharacterPalette.Family.Feminine, FamilyOf("Ada"));
        Assert.Equal(CharacterPalette.Family.Feminine, FamilyOf("Mrs. Penrose"));
        Assert.Equal(CharacterPalette.Family.Neutral, FamilyOf("Quill"));
        Assert.Equal("#336699", p.Characters[I("Bo")].ColorHex);
        Assert.Equal("#EC4899", p.Characters[I("Cal")].ColorHex);
        // Automatic characters don't share a color while the family has one free.
        var auto = p.Characters.Where(c => c.ColorIsAutomatic).Select(c => c.ColorHex.ToUpperInvariant()).ToList();
        Assert.Equal(auto.Count, auto.ToHashSet().Count);
        Assert.DoesNotContain("#EC4899", auto);
        // Running it again changes nothing: colors don't shuffle.
        var before = p.Characters.Select(c => c.ColorHex).ToList();
        Assert.Equal(0, CharacterPalette.RecolorAutomatic(p, new BookIndex(p)));
        Assert.Equal(before, p.Characters.Select(c => c.ColorHex));
        // The book says more: Quill is a woman. Only Quill moves.
        p.Characters[I("Quill")].Facts = [new Fact { Kind = FactKind.Gender, Text = "A woman in her forties" }];
        Assert.Equal(1, CharacterPalette.RecolorAutomatic(p, new BookIndex(p)));
        Assert.Equal(CharacterPalette.Family.Feminine, FamilyOf("Quill"));
        // A big cast shares colors within its family, never across.
        var masculine = CharacterPalette.SwatchesIn(CharacterPalette.Family.Masculine).Select(s => s.Hex);
        Assert.False(string.IsNullOrEmpty(CharacterPalette.Next(masculine, CharacterPalette.Family.Masculine)));
        Assert.Equal(CharacterPalette.Family.Feminine,
            CharacterPalette.FamilyOf(CharacterPalette.Next(CharacterPalette.Swatches.Select(s => s.Hex), CharacterPalette.Family.Feminine)));
    }

    // MARK: - Deleting text

    static (Project P, Guid Keep, Guid Gone, Guid Next) DeletionProject()
    {
        var keep = new Paragraph("“Hello,” said Ada. ISBN 978-0-00-000000-0 “Bye.”", italic: [new TextSpan(19, 4)]);
        var gone = new Paragraph("Also by this author: Another Book.");
        var next = new Paragraph("“Next,” said Bram.");
        var p = new Project
        {
            Title = "T",
            Chapters =
            [
                new Chapter("One", [keep, gone, next]),
                new Chapter("Ads", [new Paragraph("Read the next book!")]),
            ],
        };
        var ada = new BookCharacter("Ada", "#0EA5E9");
        p.Characters = [ada];
        p.Lines =
        [
            new Line(keep.Id, new TextSpan(0, 8), speakerID: ada.Id),
            new Line(keep.Id, SpanOf(keep.Text, "“Bye.”"), speakerID: ada.Id),
            new Line(next.Id, new TextSpan(0, 7)),
        ];
        p.Recorded = [gone.Id];
        p.Retakes = [new Retake { ParagraphID = gone.Id, Note = "x" }];
        return (p, keep.Id, gone.Id, next.Id);
    }

    [Fact]
    public void DeletingTextMovesLaterLines()
    {
        var (p, keep, _, _) = DeletionProject();
        var isbn = SpanOf(p.Chapters[0].Paragraphs[0].Text, "ISBN 978-0-00-000000-0");
        p.DeleteText(new() { [keep] = [isbn] });
        var after = p.Chapters[0].Paragraphs[0];
        Assert.Equal("“Hello,” said Ada. “Bye.”", after.Text);
        Assert.Equal("“Hello,”", after.Substring(p.Lines[0].Range));
        Assert.Equal("“Bye.”", after.Substring(p.Lines[1].Range));
        Assert.Empty(after.Italic);   // the italic run was the word ISBN
    }

    [Fact]
    public void DeletingTextTrimsAndDropsLines()
    {
        var (p, keep, _, _) = DeletionProject();
        // Cut from inside the first line to just before the second: the first is trimmed.
        p.DeleteText(new() { [keep] = [new TextSpan(7, 12)] });
        var after = p.Chapters[0].Paragraphs[0];
        Assert.Equal("“Hello,", after.Substring(p.Lines[0].Range));
        Assert.StartsWith("“Hello,ISBN", after.Text);
        Assert.Equal(3, p.Lines.Count);
        // A cut that swallows a whole line drops it.
        var bye = SpanOf(after.Text, "“Bye.”");
        p.DeleteText(new() { [keep] = [bye] });
        Assert.Equal(2, p.Lines.Count);
    }

    [Fact]
    public void DeletingWholeParagraphsAndChapters()
    {
        var (p, _, gone, next) = DeletionProject();
        var ads = p.Chapters[1].Id;
        p.DeleteText(new() { [gone] = [new TextSpan(0, 999)] }, [ads]);
        Assert.Single(p.Chapters);
        Assert.Equal(2, p.Chapters[0].Paragraphs.Count);
        Assert.Empty(p.Recorded);
        Assert.Empty(p.Retakes);
        Assert.Equal(next, p.Lines.Last().ParagraphID);
    }

    [Fact]
    public void DeletingChapterHeadings()
    {
        var (p, _, _, _) = DeletionProject();
        var first = p.Chapters[0].Copy();
        var second = p.Chapters[1].Copy();
        var lines = p.Lines.ConvertAll(l => l.Copy());
        // Part of a heading: the rest stays.
        var q = p.Copy();
        q.DeleteText([], headings: new() { [first.Id] = new TextSpan(0, first.Title.Length - 1) });
        Assert.Equal(1, q.Chapters[0].Title.Length);
        // A later heading, all of it: its text joins the chapter before, lines untouched.
        q = p.Copy();
        q.DeleteText([], headings: new() { [second.Id] = new TextSpan(0, second.Title.Length) });
        Assert.Equal(p.Chapters.Count - 1, q.Chapters.Count);
        Assert.Equal(first.Paragraphs.Select(x => x.Id).Concat(second.Paragraphs.Select(x => x.Id)), q.Chapters[0].Paragraphs.Select(x => x.Id));
        Assert.Equal(lines.Count, q.Lines.Count);
        for (int i = 0; i < lines.Count; i++)
        {
            Assert.Equal(lines[i].Id, q.Lines[i].Id);
            Assert.Equal(lines[i].ParagraphID, q.Lines[i].ParagraphID);
            Assert.True(lines[i].SameAssignment(q.Lines[i]), $"line {i} changed");
            Assert.Equal(lines[i].ContinuesPrevious, q.Lines[i].ContinuesPrevious);
        }
        // The first heading: its text stays, with no heading.
        p.DeleteText([], headings: new() { [first.Id] = new TextSpan(0, 999) });
        Assert.Equal("", p.Chapters[0].Title);
        Assert.Equal("Opening", p.Chapters[0].ShownTitle());
        Assert.Equal(first.Paragraphs.Select(x => x.Id), p.Chapters[0].Paragraphs.Select(x => x.Id));
    }

    // MARK: - Unsure guesses

    [Fact]
    public void PronounTagGuessesTheSentenceSubject()
    {
        var p = ProjectBuilder.Make(Book(
            "“Sit,” said Cyrus Vane.",
            "“Thank you,” said Barnabe.",
            "Cyrus flipped to the second and third pages of the book and passed the volume to Barnabe. “You can,” he said, “if you can explain why some of the words have changed.”",
            "Barnabe looked up at Cyrus and frowned. “Changed?” he asked."), "t.txt");
        var you = LineStarting("You can", p);
        Assert.Equal("Cyrus Vane", p.Character(you?.SpeakerID)?.Name);
        Assert.NotNull(you);
        Assert.True(p.NeedsYou(you!));   // a guess: still for the narrator to check
        var rest = LineStarting("if you can", p);
        Assert.Equal("Cyrus Vane", p.Character(rest?.SpeakerID)?.Name);
        var changed = LineStarting("Changed", p);
        Assert.Equal("Barnabe", p.Character(changed?.SpeakerID)?.Name);
    }

    // MARK: - Activity

    [Fact]
    public void ActivityLogAddsUpAndCombinesMacs()
    {
        // The C# log has no combining across Macs; this checks one log's record, totals and trim.
        var a = new ActivityLog();
        var b = new ActivityLog();
        var day1 = new DateTime(2026, 11, 1);
        var day2 = new DateTime(2026, 11, 2);
        a.Record(ActivityKey.LinesAI, 10, day1);
        a.Record(ActivityKey.AiKept("M"), 9, day1);
        a.Record(ActivityKey.AiChanged("M"), 1, day2);
        b.Record(ActivityKey.LinesAI, 5, day2);
        Assert.Equal(10, a.Lifetime[ActivityKey.LinesAI]);
        Assert.Equal("2026-11-01", a.FirstDay);
        Assert.False(a.Totals(day2).ContainsKey(ActivityKey.LinesAI));
        Assert.Equal(5, b.Totals(day2)[ActivityKey.LinesAI]);
        Assert.Equal("2026-11-02", b.FirstDay);
        var m = ModelAccuracy.All(a.Lifetime);
        Assert.True(m.Count == 1 && m[0].Reviewed == 10 && m[0].Rate == 0.9);
        a.Trim(keepDays: 1, now: new DateTime(2026, 12, 1));
        Assert.Empty(a.Days);
        Assert.Equal(10, a.Lifetime[ActivityKey.LinesAI]);
    }

    // MARK: - Guided steps

    [Fact]
    public void GuidedStepsFollowTheBook()
    {
        // The C# steps are the Narration Tools ones only (no Voices or Listen).
        var p = new Project { Title = "t", Chapters = [new Chapter("One", [new Paragraph("“Hi,” said Ada.")])] };
        var para = p.Chapters[0].Paragraphs[0].Id;
        p.Lines = [new Line(para, new TextSpan(0, 6), LineKind.Spoken, speakerID: null, confidence: 0, source: AssignmentSource.None)];
        var steps = new GuidedSteps(p, speaking: 0);
        Assert.Equal(
            [GuidedStep.Manuscript, GuidedStep.Speakers, GuidedStep.Review, GuidedStep.Characters, GuidedStep.Pronunciations, GuidedStep.Record],
            steps.All.Select(s => s.Step));
        Assert.Equal(GuidedStep.Manuscript, steps.Current?.Step);
        // Marking the manuscript ready moves on to Tag Speakers.
        GuidedSteps.MarkStep(p.Settings, GuidedStep.Manuscript, done: true);
        steps = new GuidedSteps(p, speaking: 0);
        Assert.Equal(GuidedStep.Speakers, steps.Current?.Step);
        // Tagged with a line that needs the narrator: Review is next and says how many.
        p.LastTagged = AppleDate.Now;
        steps = new GuidedSteps(p, speaking: 0);
        Assert.Equal(GuidedStep.Review, steps.Current?.Step);
        Assert.Equal("1 to review", steps.Current?.Detail);
        // Skipping review moves on; the characters haven't been read.
        GuidedSteps.MarkStep(p.Settings, GuidedStep.Review, done: true);
        steps = new GuidedSteps(p, speaking: 1);
        Assert.Equal(GuidedStep.Characters, steps.Current?.Step);
        p.SummariesBuilt = AppleDate.Now;
        steps = new GuidedSteps(p, speaking: 1);
        Assert.Equal(GuidedStep.Pronunciations, steps.Current?.Step);
        GuidedSteps.MarkStep(p.Settings, GuidedStep.Pronunciations, done: true);
        steps = new GuidedSteps(p, speaking: 1);
        Assert.Equal(GuidedStep.Record, steps.Current?.Step);
        Assert.Equal("0 of 1 recorded", steps.All[^1].Detail);
        GuidedSteps.MarkStep(p.Settings, GuidedStep.Record, done: true);
        Assert.True(new GuidedSteps(p, speaking: 1).AllDone);
        // Taking a mark off brings the step back.
        GuidedSteps.MarkStep(p.Settings, GuidedStep.Review, done: false);
        Assert.Equal(GuidedStep.Review, new GuidedSteps(p, speaking: 1).Current?.Step);
    }
}
