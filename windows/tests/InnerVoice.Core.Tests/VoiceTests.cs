using System.Text.Json;
using InnerVoice.Core;
using InnerVoice.Core.AI;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Ports of the Mac's voice tests (Tests/InnerVoiceKitTests: tones, casting, speech scripts,
/// pauses, Series Voices, voice descriptions), with the Swift names in PascalCase.
public class VoiceTests
{
    static ImportedBook Book(params string[] paragraphs) =>
        new("Test", "", [new Chapter("Chapter 1", paragraphs.Select(t => new Paragraph(t, ParagraphStyle.Body, null)).ToList())]);

    static Project Make(params string[] paragraphs) => ProjectBuilder.Make(Book(paragraphs), "t.txt");

    static Line? LineStarting(Project p, string prefix)
    {
        var index = new BookIndex(p);
        return p.Lines.FirstOrDefault(l => p.TextOf(l, index).StartsWith(prefix, StringComparison.Ordinal));
    }

    // MARK: Tones

    [Fact]
    public void SpeechTagsGiveTones()
    {
        var p = Make("“Get out,” Tom snapped.", "“I can’t,” Mara whispered.", "“We found it!” said Pip.", "“Fine,” said Tom.",
                     "Edda wiped her eyes. “He was my brother.”");
        Assert.Equal(Tone.Angry, LineStarting(p, "Get out")?.Tone);
        Assert.Equal(ToneRules.SureCue, LineStarting(p, "Get out")?.ToneConfidence);
        Assert.Equal(Tone.Whispered, LineStarting(p, "I can")?.Tone);
        // An exclamation mark alone is a guess for the AI; it shows as unsure.
        Assert.Equal(Tone.Excited, LineStarting(p, "We found")?.Tone);
        Assert.True(p.ToneNeedsYou(LineStarting(p, "We found")!));
        Assert.Equal(Tone.Neutral, LineStarting(p, "Fine")?.Tone);
        Assert.False(p.ToneNeedsYou(LineStarting(p, "Fine")!));
        Assert.Equal(Tone.Sad, LineStarting(p, "He was")?.Tone);
    }

    [Fact]
    public void SpeechTagsGiveSounds()
    {
        var p = Make("“Not again,” Tom sighed.", "Pip laughed. “You fell in!”", "“Look out!” Mara gasped, and then said nothing.", "“Fine,” said Tom.");
        var segs = SpeechScript.Segments(0, p, new BookIndex(p), includeNarration: false);
        VocalSound? Sound(string prefix) => segs.First(s => s.Text.StartsWith(prefix, StringComparison.Ordinal)).Sound;
        Assert.Equal(VocalSound.Sigh, Sound("Not again"));
        Assert.Equal(VocalSound.Laugh, Sound("You fell"));
        Assert.Equal(VocalSound.Gasp, Sound("Look out"));
        Assert.Null(Sound("Fine"));
    }

    [Fact]
    public void TonePromptAgreementAndNarratorTones()
    {
        var p = Make("“We found it!” said Pip.", "“Stop,” said Tom.");
        var index = new BookIndex(p);
        var chunks = TonePrompt.Chunks(p, index, 0);
        Assert.Single(chunks);
        Assert.Equal(2, chunks[0].Labels.Count);   // the guess and the plain line; nothing sure
        var found = p.Lines[0].Id;
        var label = chunks[0].Labels.First(kv => kv.Value == found).Key;
        TonePrompt.Apply([new ToneReply(label, "excited", 50)], chunks[0], p);
        Assert.Equal(ToneSource.Ai, p.Lines[0].ToneSource);
        Assert.True((p.Lines[0].ToneConfidence ?? 0) > 0.6);   // the rule and the AI agree
        // The narrator's tone is never asked about or changed.
        p.Lines[1].Tone = Tone.Tender;
        p.Lines[1].ToneSource = ToneSource.User;
        Assert.False(TonePrompt.NeedsModel(p.Lines[1]));
        ToneRules.Apply(p);
        Assert.Equal(Tone.Tender, p.Lines[1].Tone);
        Assert.Equal("sad", TonePrompt.Parse("{\"lines\":[{\"id\":\"T1\",\"tone\":\"sad\",\"confidence\":70}]}")?.First().Tone);
        // The grammar needs every line answered, by label.
        Assert.Contains("\\\"T1\\\"", chunks[0].Grammar);
        Assert.Contains("\\\"T2\\\"", chunks[0].Grammar);
        Assert.StartsWith("root ::=", chunks[0].Grammar);
        // A reply that copies the speaker into the id still lands on its line.
        var q = Make("“Stop,” said Tom.");
        var qc = TonePrompt.Chunks(q, new BookIndex(q), 0);
        TonePrompt.Apply([new ToneReply("T1: Tom", "angry", 80)], qc[0], q);
        Assert.Equal(Tone.Angry, q.Lines[0].Tone);
    }

    [Fact]
    public void TonePromptMarksLinesInThePassage()
    {
        var p = Make("“Stop,” said Tom.");
        var chunk = TonePrompt.Chunks(p, new BookIndex(p), 0)[0];
        Assert.Contains("{T1: Tom}“Stop,”", chunk.Messages[1].Content);
        Assert.Equal(TonePrompt.System, chunk.Messages[0].Content);
    }

    [Fact]
    public void OldLinesDecodeWithoutTones()
    {
        const string json = """{"id":"6A4F1C2E-1111-4222-8333-944455556666","paragraphID":"6A4F1C2E-1111-4222-8333-944455556667","range":{"location":0,"length":4},"kind":"spoken","confidence":0.9,"source":"rule","continuesPrevious":false}""";
        var line = JsonSerializer.Deserialize<Line>(json, ProjectJson.Options)!;
        Assert.Null(line.Tone);
        Assert.Null(line.ToneSource);
        // And none is written back.
        Assert.DoesNotContain("tone", JsonSerializer.Serialize(line, ProjectJson.Options));
    }

    // MARK: Casting

    static Project CastingProject()
    {
        var p = Make("“Morning,” said Edda Voss.", "“Morning,” said Mara Quinn.", "“Tea?” said Tom Hale.", "“Please,” Mara said.", "“Two sugars,” said Edda.");
        foreach (var c in p.Characters)
        {
            if (c.Name == "Edda Voss")
                c.Facts = [new Fact { Kind = FactKind.Gender, Text = "Woman" }, new Fact { Kind = FactKind.Age, Text = "About 60" },
                           new Fact { Kind = FactKind.Accent, Text = "Soft Scottish accent" }];
            else if (c.Name == "Mara Quinn")
                c.Facts = [new Fact { Kind = FactKind.Relationships, Text = "Daughter of the late keeper" }];
        }
        return p;
    }

    static readonly VoiceInfo[] SystemVoices =
    [
        new(VoiceEngine.Apple, "f-us", "Ava", Gender.Female, Accent: "American", Quality: 2),
        new(VoiceEngine.Apple, "f-sc", "Fiona", Gender.Female, Accent: "Scottish", Quality: 1),
        new(VoiceEngine.Apple, "m-gb", "Daniel", Gender.Male, Accent: "British", Quality: 1),
        new(VoiceEngine.Apple, "m-us", "Evan", Gender.Male, Accent: "American", Quality: 2),
        new(VoiceEngine.Apple, "n-us", "Sky", Gender.Neutral, Accent: "American", Quality: 2),
    ];

    static BookCharacter Named(Project p, string name) => p.Characters.First(c => c.Name == name);

    [Fact]
    public void CastingReadsTheSummary()
    {
        var p = CastingProject();
        var index = new BookIndex(p);
        var profile = VoiceCasting.Profile(Named(p, "Edda Voss"), p, index);
        Assert.Equal(Gender.Female, profile.Gender);
        Assert.Equal(VoiceAge.Older, profile.Age);
        Assert.Equal("Scottish", profile.Accent);
        Assert.Equal(Gender.Female, VoiceCasting.Profile(Named(p, "Mara Quinn"), p, index).Gender);
        Assert.Null(VoiceCasting.Profile(Named(p, "Tom Hale"), p, index).Gender);   // nothing says

        VoiceCasting.Cast(p, index, VoiceEngine.Apple, SystemVoices);
        CastVoice Cast(string name) => Named(p, name).Voice(VoiceEngine.Apple)!;
        Assert.Equal("f-sc", Cast("Edda Voss").Choice.VoiceID);
        Assert.True((Cast("Edda Voss").Choice.Pitch ?? 1) < 1);   // older: a little lower
        // Two women in the same scene get different voices.
        Assert.Equal("f-us", Cast("Mara Quinn").Choice.VoiceID);
        Assert.False(p.VoiceNeedsYou(Named(p, "Edda Voss")));
        // Tom: no gender known, so casting is unsure and asks.
        Assert.True(p.VoiceNeedsYou(Named(p, "Tom Hale")));
        Assert.NotNull(p.Settings.NarratorVoice(VoiceEngine.Apple));
    }

    [Fact]
    public void CastingKeepsTheNarratorsChoice()
    {
        var p = CastingProject();
        var index = new BookIndex(p);
        var edda = Named(p, "Edda Voss");
        edda.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "f-us"), 1, VoiceSource.User), VoiceEngine.Apple);
        VoiceCasting.Cast(p, index, VoiceEngine.Apple, SystemVoices);
        Assert.Equal("f-us", edda.Voice(VoiceEngine.Apple)?.Choice.VoiceID);
        // Mara can't have Edda's voice in the same scene, so she gets another.
        Assert.NotEqual("f-us", Named(p, "Mara Quinn").Voice(VoiceEngine.Apple)?.Choice.VoiceID);
        // Each engine keeps its own casting.
        VoiceCasting.Cast(p, index, VoiceEngine.Orpheus, VoiceInfo.Orpheus);
        Assert.Equal("f-us", edda.Voice(VoiceEngine.Apple)?.Choice.VoiceID);
        Assert.Equal(Gender.Female, VoiceInfo.Orpheus.First(v => v.VoiceID == edda.Voice(VoiceEngine.Orpheus)?.Choice.VoiceID).Gender);
    }

    /// The Mac's pocketCastsStockVoicesByGenderAndAge.
    [Fact]
    public void PocketCastsStockVoicesByGenderAndAge()
    {
        var p = CastingProject();
        VoiceCasting.Cast(p, new BookIndex(p), VoiceEngine.Pocket, VoiceInfo.Pocket);
        // Edda is about 60: a woman's stock voice, made lower and slower (stock voices are young).
        var edda = Named(p, "Edda Voss").Voice(VoiceEngine.Pocket)?.Choice;
        Assert.Equal(Gender.Female, VoiceInfo.Pocket.First(v => v.VoiceID == edda?.VoiceID).Gender);
        Assert.True(edda?.Pitch == 0.89 && edda?.Rate == 0.92);
        Assert.Equal(-2, PocketVoice.Semitones(edda?.Pitch));
        // Nothing is designed from a description any more.
        Assert.All(p.Characters, c => Assert.Null(c.Voice(VoiceEngine.Pocket)?.Choice.Design));
        // The narrator gets the engine's own voice, the best there is.
        Assert.Equal(PocketVoice.BuiltInID, p.Settings.NarratorVoice(VoiceEngine.Pocket)?.Choice.VoiceID);
    }

    /// The Mac's pocketGivesThinCharactersDistinctStockVoices.
    [Fact]
    public void PocketGivesThinCharactersDistinctStockVoices()
    {
        var p = Make("“Where is it?” asked Ann.", "“Gone,” said Bea.", "“Look again,” said Ann.", "“I did,” said Bea.");
        var index = new BookIndex(p);
        VoiceCasting.Cast(p, index, VoiceEngine.Pocket, VoiceInfo.Pocket);
        var speaking = p.Characters.Where(c => (index.StatsFor(c.Id)?.Lines ?? 0) > 0).ToList();
        var ids = speaking.Select(c => c.Voice(VoiceEngine.Pocket)?.Choice.VoiceID).OfType<string>().ToList();
        Assert.Equal(speaking.Count, ids.Count);
        Assert.All(ids, id => Assert.StartsWith(PocketVoice.StockPrefix, id));
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // Nobody shares the narrator's voice.
        Assert.DoesNotContain(p.Settings.NarratorVoice(VoiceEngine.Pocket)?.Choice.VoiceID ?? "", ids);
    }


    [Fact]
    public void MixedCastingUsesRealEngines()
    {
        var p = Make("“Where is it?” asked Ann.", "“Gone,” said Bea.", "“Hush,” said Cal.");
        VoiceCasting.Cast(p, new BookIndex(p), VoiceEngine.Mixed, [.. VoiceInfo.Pocket, .. VoiceInfo.Orpheus]);
        var choices = p.Characters.Select(c => c.Voice(VoiceEngine.Mixed)?.Choice).OfType<VoiceChoice>().ToList();
        Assert.NotEmpty(choices);
        Assert.All(choices, c => Assert.True(c.Engine is VoiceEngine.Pocket or VoiceEngine.Orpheus));
        Assert.NotEqual(VoiceEngine.Mixed, p.Settings.NarratorVoice(VoiceEngine.Mixed)?.Choice.Engine);
    }

    [Fact]
    public void CastingReadsGenderAndAgeFromVoiceNotes()
    {
        var p = Make("“Who’s there?” asked Robin.");
        var c = Named(p, "Robin");
        c.VoiceNotes = "An elderly woman, papery and quick.";
        var profile = VoiceCasting.Profile(c, p, new BookIndex(p));
        Assert.True(profile.Gender == Gender.Female && profile.Age == VoiceAge.Older);
        Assert.Contains("Gender: your voice notes", profile.Evidence);
    }

    [Fact]
    public void VoiceBriefsShapeCasting()
    {
        var p = Make("“Who’s there?” asked Tom.", "“Only me,” said Mara.");
        var tom = Named(p, "Tom");
        tom.VoiceNotes = "Warm underneath.\n\nVoice brief:\nPitch: Low, chest voice\nPace: Slow and deliberate";
        var profile = VoiceCasting.Profile(tom, p, new BookIndex(p));
        Assert.True(profile.PitchLean == -1 && profile.PaceLean == -1);
        var described = VoiceCasting.Describe(profile, "Tom");
        Assert.Contains("low-pitched", described);
        Assert.Contains("speaking slowly", described);
    }

    [Fact]
    public void TheNarratorsGenderComesFirst()
    {
        var p = Fixtures.SampleProject();
        var index = new BookIndex(p);
        var c = new BookCharacter("Mrs. Example", "#3366CC") { Gender = CharacterGender.Male };
        Assert.Equal(Gender.Male, VoiceCasting.Profile(c, p, index).Gender);
        Assert.Contains("Gender: you chose", VoiceCasting.Profile(c, p, index).Evidence);
        c.Gender = CharacterGender.Other;
        Assert.Equal(Gender.Neutral, VoiceCasting.Profile(c, p, index).Gender);
        c.Gender = null;
        Assert.Equal(Gender.Female, VoiceCasting.Profile(c, p, index).Gender);
    }

    static readonly VoiceInfo[] Catalog =
    [
        new(VoiceEngine.Apple, "n", "Narrator", Gender.Neutral, Accent: "American", Quality: 3),
        new(VoiceEngine.Apple, "m1", "Man", Gender.Male, Accent: "American", Quality: 2),
        new(VoiceEngine.Apple, "m2", "Boy", Gender.Male, VoiceAge.Child, "American", 3),
        new(VoiceEngine.Apple, "w1", "Woman", Gender.Female, Accent: "American", Quality: 2),
        new(VoiceEngine.Apple, "x1", "Neutral", Gender.Neutral, Accent: "American", Quality: 1),
    ];

    [Fact]
    public void SharedRolesGetDistinctFittingVoices()
    {
        var p = new Project { Title = "T" };
        p.Settings.CastingStyle = CastingStyle.Shared;
        p.Settings.SetNarratorVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "n"), 1, VoiceSource.User), VoiceEngine.Apple);
        Assert.Equal(3, VoiceCasting.CastRoles(p, VoiceEngine.Apple, Catalog));
        // An adult man over a higher-quality boy; never the narrator's voice.
        Assert.Equal("m1", p.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Apple)?.Choice.VoiceID);
        Assert.Equal("w1", p.Settings.RoleVoice(VoiceRole.Women, VoiceEngine.Apple)?.Choice.VoiceID);
        Assert.Equal("x1", p.Settings.RoleVoice(VoiceRole.Other, VoiceEngine.Apple)?.Choice.VoiceID);
        // A voice the narrator chose stays; automatic ones are kept only when asked.
        p.Settings.SetRoleVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "m2"), 1, VoiceSource.User), VoiceRole.Men, VoiceEngine.Apple);
        VoiceCasting.CastRoles(p, VoiceEngine.Apple, Catalog);
        Assert.Equal("m2", p.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Apple)?.Choice.VoiceID);
        Assert.Equal(VoiceSource.Auto, p.Settings.RoleVoice(VoiceRole.Women, VoiceEngine.Apple)?.Source);
        // The Mac's JSON shape: role → engine → voice.
        var json = System.Text.Json.Nodes.JsonNode.Parse(ProjectJson.Encode(p))!["settings"]!["roleVoices"]!;
        Assert.Equal("m2", (string?)json["men"]!["apple"]!["choice"]!["voiceID"]);
    }

    [Fact]
    public void OnlyStylesThatShareVoicesCastThem()
    {
        var p = new Project { Title = "T" };
        p.Settings.CastingStyle = CastingStyle.OneVoice;
        Assert.Equal(0, VoiceCasting.CastRoles(p, VoiceEngine.Apple, Catalog));
        p.Settings.CastingStyle = CastingStyle.FullCast;
        Assert.Equal(0, VoiceCasting.CastRoles(p, VoiceEngine.Apple, Catalog));
        p.Settings.MinorVoices = MinorVoices.ByGender;
        Assert.Equal(3, VoiceCasting.CastRoles(p, VoiceEngine.Apple, Catalog));
    }

    [Fact]
    public void EngineRawValuesMatchTheMac()
    {
        Assert.Equal(["apple", "pocket", "orpheus", "elevenlabs", "mixed"], Enum.GetValues<VoiceEngine>().Select(e => e.Raw()));
        Assert.Equal("Windows Voices", VoiceEngine.Apple.Title());
        Assert.True(VoiceEngine.Elevenlabs.IsCloud());
        Assert.Equal(VoiceEngine.Elevenlabs, VoiceEngines.Parse("elevenlabs"));
        var settings = new ProjectSettings { VoiceEngine = VoiceEngine.Elevenlabs };
        Assert.Contains("\"voiceEngine\":\"elevenlabs\"", JsonSerializer.Serialize(settings, ProjectJson.Options));
        Assert.Equal("[whispers]", Tone.Whispered.AudioTag());
        Assert.Equal("[sighs]", VocalSound.Sigh.AudioTag());
    }

    // MARK: Speech script

    [Fact]
    public void SpeechScriptSplitsNarrationAndAppliesPronunciations()
    {
        var p = Make("“Busy.” The doctor folded her gloves. “Your father wrote to Kerensa.”", "She laid an envelope on the table.");
        p.Pronunciations = [new Pronunciation { Term = "Kerensa", SayItAs = "keh-REN-sa" }];
        var index = new BookIndex(p);
        var all = SpeechScript.Segments(0, p, index, includeNarration: true);
        Assert.Equal(["Busy.", "The doctor folded her gloves.", "Your father wrote to keh-REN-sa.", "She laid an envelope on the table."], all.Select(s => s.Text));
        Assert.Equal(2, all.Count(s => s.LineID is not null));
        var dialogue = SpeechScript.Segments(0, p, index, includeNarration: false);
        Assert.Equal(["Busy.", "Your father wrote to keh-REN-sa."], dialogue.Select(s => s.Text));
        Assert.Equal(SpeechScript.ChapterPause, dialogue[^1].PauseAfter);
    }

    [Fact]
    public void SentencesGetTheirOwnPauses()
    {
        var p = Make("The tide was in. The boat was gone. “Stay here. Don’t move,” said Tom. Nobody answered.");
        var index = new BookIndex(p);
        var settings = PauseSettings.Standard;
        var split = SpeechScript.Segments(0, p, index, includeNarration: true, pauses: settings);
        Assert.Equal(["The tide was in.", "The boat was gone.", "Stay here.", "Don’t move"], split.Take(4).Select(s => s.Text));
        Assert.True(split[0].Pause == PauseKind.Sentence && split[1].Pause == PauseKind.Line);   // narration, then the line starts
        Assert.True(split[2].Pause == PauseKind.Sentence && split[2].LineID is not null && split[3].LineID == split[2].LineID);
        Assert.Equal(PauseKind.Chapter, split[^1].Pause);
        settings.SplitSentences = false;
        var whole = SpeechScript.Segments(0, p, index, includeNarration: true, pauses: settings);
        Assert.True(whole.Count < split.Count && whole[0].Text == "The tide was in. The boat was gone.");
        Assert.True(PauseSettings.Tight.Paragraph < PauseSettings.Standard.Paragraph && PauseSettings.Standard.Paragraph < PauseSettings.Relaxed.Paragraph);
        Assert.Equal(PauseSettings.Standard, new PauseSettings());
    }

    [Fact]
    public void SpeechScriptReadsPickedChaptersInBookOrder()
    {
        var chapters = Enumerable.Range(1, 4).Select(n => new Chapter($"Chapter {n}", [new Paragraph($"Part {n} begins.", ParagraphStyle.Body, null)])).ToList();
        var p = ProjectBuilder.Make(new ImportedBook("Test", "", chapters), "t.txt");
        var index = new BookIndex(p);
        var picked = SpeechScript.Segments([3, 0, 3, 9], p, index, includeNarration: true);
        Assert.Equal(["Part 1 begins.", "Part 4 begins."], picked.Select(s => s.Text).Where(t => t.StartsWith("Part")));
        Assert.Equal(2, picked.Count(s => s.Pause == PauseKind.Chapter));
        var all = SpeechScript.Segments([0, 1, 2, 3], p, index, includeNarration: true);
        Assert.Equal(SpeechScript.Segments(0, p, index, includeNarration: true, chapters: 4).Select(s => s.Id), all.Select(s => s.Id));
        Assert.Equal("Chapter 3", SpeechScript.ChapterLabel([2]));
        Assert.Equal("Chapters 1–3, 5, 7, 8", SpeechScript.ChapterLabel([4, 0, 1, 2, 6, 7]));
    }

    [Fact]
    public void RecapsAreReadAfterTheTitle()
    {
        var chapters = new List<Chapter>
        {
            new("Chapter 1", [new Paragraph("Chapter 1", ParagraphStyle.Heading, null), new Paragraph("The tide came in.", ParagraphStyle.Body, null)]),
        };
        var p = ProjectBuilder.Make(new ImportedBook("Test", "", chapters), "t.txt");
        p.Chapters[0].Recap = "Mara found the letter. She kept it.";
        var segs = SpeechScript.Segments(0, p, new BookIndex(p), includeNarration: true, recaps: true);
        Assert.Equal(["Chapter 1", "Previously…", "Mara found the letter.", "She kept it.", "The tide came in."], segs.Select(s => s.Text));
        Assert.Equal(PauseKind.Paragraph, segs[3].Pause);
        Assert.Equal(PauseKind.Chapter, segs[^1].Pause);
        Assert.Equal(RecapPrompt.SpokenParts(p.Chapters[0]), segs.Skip(1).Take(3).Select(s => s.Text));
    }

    [Fact]
    public void SpeechSegmentsKnowWhereTheirWordsAre()
    {
        var p = Make("“Busy.” The doctor folded her gloves. “Your father wrote to Kerensa.”", "Mara read it twice. It was sealed.");
        p.Pronunciations = [new Pronunciation { Term = "Kerensa", SayItAs = "keh-REN-sa" }];
        var segs = SpeechScript.Segments(0, p, new BookIndex(p), includeNarration: true, pauses: new PauseSettings());
        foreach (var seg in segs.Where(s => s.Text.Length > 0))
        {
            var para = p.Chapters[0].Paragraphs.First(x => x.Id == seg.ParagraphID);
            var source = Assert.IsType<TextSpan>(seg.Source);
            var words = para.Substring(source);
            // The words match what's said, before pronunciations.
            Assert.Equal(seg.Text, SpeechScript.Apply(SpeechScript.Sayings(p), words));
        }
        Assert.Contains(segs, s => s.Text == "It was sealed.");
    }

    // The cases Apple's sentence tokenizer handles in the Mac's tests, plus book prose it
    // must not split.
    [Theory]
    [InlineData("The tide was in. The boat was gone.", new[] { "The tide was in.", "The boat was gone." })]
    [InlineData("Stay here. Don’t move", new[] { "Stay here.", "Don’t move" })]
    [InlineData("Busy.", new[] { "Busy." })]
    [InlineData("Mara found the letter. She kept it.", new[] { "Mara found the letter.", "She kept it." })]
    [InlineData("Mr. Hale came in. Dr. Voss left.", new[] { "Mr. Hale came in.", "Dr. Voss left." })]
    [InlineData("J. R. Tolkien wrote it.", new[] { "J. R. Tolkien wrote it." })]
    [InlineData("Wait! Who’s there? Nobody.", new[] { "Wait!", "Who’s there?", "Nobody." })]
    [InlineData("He paused… then went on.", new[] { "He paused… then went on." })]
    [InlineData("It cost 3.50 in all. Fine.", new[] { "It cost 3.50 in all.", "Fine." })]
    [InlineData("“Go,” she said. “Now.”", new[] { "“Go,” she said.", "“Now.”" })]
    [InlineData("She said, “Run!” Then she ran.", new[] { "She said, “Run!”", "Then she ran." })]
    [InlineData("Apples, e.g. red ones, are best.", new[] { "Apples, e.g. red ones, are best." })]
    [InlineData("First line\nSecond line", new[] { "First line", "Second line" })]
    [InlineData("  ", new[] { "  " })]
    public void SentenceSplitterMatchesTheMac(string text, string[] expected) =>
        Assert.Equal(expected, SpeechScript.Sentences(text));

    // MARK: Series Voices and Voice Notes

    [Fact]
    public void SeriesVoicesMatchAndCarryOver()
    {
        // The earlier book: three voiced characters and a narrator voice.
        var old = new Project();
        var mara = new BookCharacter("Mara Quinn", "#111111") { Aliases = ["Miss Quinn"], VoiceNotes = "Warm, clipped." };
        var tom = new BookCharacter("Tom Hale", "#222222");
        var edda = new BookCharacter("Doctor Edda Voss", "#333333");
        var nobody = new BookCharacter("Silent Sam", "#444444");
        old.Characters = [mara, tom, edda, nobody];
        var tara = new VoiceChoice(VoiceEngine.Pocket, "stock:tara");
        mara.SetVoice(new CastVoice(tara, 0.6, VoiceSource.Auto), VoiceEngine.Pocket);
        mara.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "karen"), 0.6, VoiceSource.Auto), VoiceEngine.Apple);
        tom.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Orpheus, "leo"), 1, VoiceSource.User), VoiceEngine.Orpheus);
        edda.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Orpheus, "zac"), 1, VoiceSource.User), VoiceEngine.Orpheus);
        old.Settings.VoiceEngine = VoiceEngine.Pocket;
        old.Settings.SetNarratorVoice(new CastVoice(VoiceInfo.Pocket[0].Choice, 1, VoiceSource.Auto), VoiceEngine.Pocket);

        // This book: Mara goes by "Miss Quinn" as her name now, Tom keeps his, Edda is just "Edda".
        var @new = new Project();
        @new.Characters = [new BookCharacter("Miss Quinn", "#aaaaaa"), new BookCharacter("Tom Hale", "#bbbbbb"),
                           new BookCharacter("Edda", "#cccccc"), new BookCharacter("Pip", "#dddddd")];
        var oldIndex = new BookIndex(old);
        // Only characters with a voice are offered.
        Assert.Equal(3, SeriesVoices.Voiced(old, oldIndex).Count);
        var matches = SeriesVoices.Suggest(old, oldIndex, @new);
        var byName = matches.ToDictionary(m => m.Source);
        Assert.True(byName[mara.Id].Target == @new.Characters[0].Id && byName[mara.Id].How == SeriesVoices.How.Name);
        Assert.True(byName[tom.Id].Target == @new.Characters[1].Id && byName[tom.Id].How == SeriesVoices.How.Name);
        Assert.True(byName[edda.Id].Target == @new.Characters[2].Id && byName[edda.Id].How == SeriesVoices.How.FirstName);

        // Leave Tom out, then apply.
        var chosen = matches.Select(m => m.Source == tom.Id ? new SeriesVoices.Match(tom.Id, null, null) : m).ToList();
        Assert.Equal(2, SeriesVoices.Apply(chosen, old, @new, new SeriesVoices.Options()));
        var q = @new.Characters[0];
        Assert.True(q.Voice(VoiceEngine.Pocket)?.Choice == tara && q.Voice(VoiceEngine.Pocket)?.Source == VoiceSource.User);
        Assert.Equal("karen", q.Voice(VoiceEngine.Apple)?.Choice.VoiceID);
        Assert.Equal("Warm, clipped.", q.VoiceNotes);
        Assert.Null(@new.Characters[1].Voices);
        Assert.Equal("zac", @new.Characters[2].Voice(VoiceEngine.Orpheus)?.Choice.VoiceID);
        Assert.True(@new.Settings.VoiceEngine == VoiceEngine.Pocket && @new.Settings.NarratorVoice(VoiceEngine.Pocket)?.Source == VoiceSource.User);

        // Casting keeps carried-over voices.
        VoiceCasting.Cast(@new, new BookIndex(@new), VoiceEngine.Orpheus, VoiceInfo.Orpheus);
        Assert.Equal("zac", @new.Characters[2].Voice(VoiceEngine.Orpheus)?.Choice.VoiceID);
    }

    [Fact]
    public void VoiceNotesBecomeAVoiceDescription()
    {
        var c = new BookCharacter("Mara Quinn", "#10B981");
        Assert.Null(VoiceDescription.Notes(c));
        Assert.Equal("A woman.", VoiceDescription.Design("A woman.", c));
        c.VoiceNotes = "Warm underneath\n\nVoice brief:\nPitch: Low\nPace: Unhurried\nContrast: Lower than Tom";
        c.VoiceBriefGenerated = "Voice brief:\nPitch: Low\nPace: Unhurried\nContrast: Lower than Tom";
        var notes = VoiceDescription.Notes(c) ?? "";
        // The narrator's own words first; headings and who they contrast with are left out.
        Assert.Equal("Warm underneath. Pitch: Low. Pace: Unhurried.", notes);
        Assert.Equal("A woman. Voice notes: " + notes, VoiceDescription.Design("A woman.", c));
        // Cut at a whole line to fit.
        c.VoiceNotes = string.Concat(Enumerable.Repeat("A long note about her voice.\n", 3)) + string.Join("\n", Enumerable.Range(1, 40).Select(n => $"Line {n} about the rasp."));
        var shortened = VoiceDescription.Design("A woman.", c, limit: 200);
        Assert.True(shortened.Length <= 200 && shortened.EndsWith('.'));
    }

    [Fact]
    public void VoicesCopyDeeply()
    {
        var p = Make("“Hello,” said Tom.");
        var tom = Named(p, "Tom");
        tom.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "a"), 1, VoiceSource.User), VoiceEngine.Apple);
        p.Settings.SetNarratorVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "n"), 1, VoiceSource.User), VoiceEngine.Apple);
        var copy = p.Copy();
        Named(copy, "Tom").Voice(VoiceEngine.Apple)!.Choice.VoiceID = "b";
        copy.Settings.NarratorVoice(VoiceEngine.Apple)!.Choice.Pitch = 1.2;
        Assert.Equal("a", tom.Voice(VoiceEngine.Apple)!.Choice.VoiceID);
        Assert.Null(p.Settings.NarratorVoice(VoiceEngine.Apple)!.Choice.Pitch);
        tom.SetVoice(null, VoiceEngine.Apple);
        Assert.Null(tom.Voices);
    }
}
