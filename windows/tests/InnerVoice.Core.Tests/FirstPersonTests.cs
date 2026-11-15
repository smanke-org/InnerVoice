using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;
using InnerVoice.Core.Speech;

namespace InnerVoice.Core.Tests;

/// First-Person Narrator (the Mac's Tests/InnerVoiceKitTests/FirstPersonTests.swift and the
/// self-test's First-Person checks of VoiceResolver), and the Cast page's sections.
[Collection("Engines")]
public class FirstPersonTests : IDisposable
{
    readonly FakeEngine system = new(VoiceEngine.Apple,
        new VoiceInfo(VoiceEngine.Apple, "n", "Narrator", Gender.Male, Accent: "American", Quality: 2),
        new VoiceInfo(VoiceEngine.Apple, "f", "Aria", Gender.Female, Accent: "American", Quality: 2),
        new VoiceInfo(VoiceEngine.Apple, "m", "Guy", Gender.Male, Accent: "American", Quality: 2));

    public FirstPersonTests()
    {
        VoiceEngineRegistry.Register(system);
        EngineSwitches.Shared = new EngineSwitches();
    }

    public void Dispose() => VoiceEngineRegistry.Unregister(VoiceEngine.Apple);

    [Fact]
    public void TellerGetsOwnRoleOthersByGender()
    {
        var me = new BookCharacter { Name = "Mara", ColorHex = "#2F7FD0" };
        var tom = new BookCharacter { Name = "Tom", ColorHex = "#2F7FD0" };
        var s = new ProjectSettings { CastingStyle = CastingStyle.FirstPerson, PovCharacterID = me.Id };
        Assert.Equal(VoiceRole.Own, CastingStyleRules.Role(me, s, CharacterGender.Female, anyMainMarked: false));
        Assert.Equal(VoiceRole.Men, CastingStyleRules.Role(tom, s, CharacterGender.Male, anyMainMarked: false));
        Assert.Equal(CastingStyleRules.SharedRoles, CastingStyleRules.SharedRolesNeeded(s));
        Assert.True(CastingStyleRules.UsesGender(s));
        Assert.Equal("First-Person Narrator", CastingStyle.FirstPerson.Title(ProjectPurpose.Narration));
        Assert.Equal("First-Person Narrator", CastingStyle.FirstPerson.Title(ProjectPurpose.AiVoices));
    }

    [Fact]
    public void ChapterNarratorsOverrideTheBook()
    {
        Guid me = Guid.NewGuid(), other = Guid.NewGuid(), ch1 = Guid.NewGuid(), ch2 = Guid.NewGuid();
        var s = new ProjectSettings { PovCharacterID = me, FirstPerson = new FirstPersonSettings { ChapterNarrators = new() { [ch2] = other } } };
        Assert.Equal(me, FirstPersonRules.Teller(ch1, s));
        Assert.Equal(other, FirstPersonRules.Teller(ch2, s));
        Assert.Equal(new HashSet<Guid> { me, other }, FirstPersonRules.Tellers(s));
        // Merging and deleting characters keep the map right (the Mac's Actions.swift).
        var merged = Guid.NewGuid();
        FirstPersonRules.Merge(s, other, merged);
        Assert.Equal(merged, FirstPersonRules.Teller(ch2, s));
        FirstPersonRules.Remove(s, merged);
        Assert.Null(s.FirstPerson!.ChapterNarrators);
        Assert.Equal(me, FirstPersonRules.Teller(ch2, s));
    }

    [Fact]
    public void DefaultsAreVariantNarratorReadsNarrationThoughtsVoiced()
    {
        var fp = new FirstPersonSettings();
        Assert.Equal(FirstPersonVoice.Variant, fp.VoiceMode);
        Assert.True(fp.VariantShade.SameAs(Shade.Make(1, 1)));
        Assert.Null(fp.ReadsNarration);
        Assert.Null(fp.VoicesThoughts);
    }

    [Fact]
    public void ReadsAndWritesTheMacsJson()
    {
        // What the Mac's JSONEncoder writes: a [UUID: UUID] is one flat array, key, value.
        const string chapter = "11111111-1111-1111-1111-111111111111", teller = "22222222-2222-2222-2222-222222222222";
        var mac = $$$"""
            {"castingStyle":"firstPerson","colorNarration":false,"showInitials":true,"style":"highlight","unsureBelow":0.6,
             "firstPerson":{"chapterNarrators":["{{{chapter}}}","{{{teller}}}"],"readsNarration":true,"shade":{"pace":2,"pitch":-1},"voice":"different","voicesThoughts":false}}
            """;
        var s = System.Text.Json.JsonSerializer.Deserialize<ProjectSettings>(mac, ProjectJson.Options)!;
        Assert.Equal(CastingStyle.FirstPerson, s.CastingStyle);
        var fp = s.FirstPerson!;
        Assert.Equal(FirstPersonVoice.Different, fp.Voice);
        Assert.True(fp.ReadsNarration);
        Assert.False(fp.VoicesThoughts);
        Assert.Equal((-1, 2), (fp.Shade!.Pitch, fp.Shade.Pace));
        Assert.Equal(Guid.Parse(teller), fp.ChapterNarrators![Guid.Parse(chapter)]);

        // And back, in the same shape, through a whole project.
        var p = new Project { Title = "T", Settings = s };
        var json = JsonNode.Parse(ProjectJson.Encode(p))!["settings"]!;
        Assert.Equal("firstPerson", (string?)json["castingStyle"]);
        var back = json["firstPerson"]!;
        Assert.Equal("different", (string?)back["voice"]);
        Assert.Equal([chapter, teller], back["chapterNarrators"]!.AsArray().Select(n => (string?)n));
        var again = ProjectJson.Decode(ProjectJson.Encode(p)).Settings.FirstPerson!;
        Assert.Equal(fp.ChapterNarrators, again.ChapterNarrators);
        // Unset fields stay out, like Swift's optionals.
        var plain = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new ProjectSettings { FirstPerson = new() }, ProjectJson.Options))!;
        Assert.Empty(plain["firstPerson"]!.AsObject());
        // A copy (undo) is deep.
        var copy = s.Copy();
        copy.FirstPerson!.ChapterNarrators!.Clear();
        Assert.Single(s.FirstPerson!.ChapterNarrators!);
    }

    static (Project P, BookCharacter Mara) Book()
    {
        var chapters = new List<Chapter>
        {
            new("Chapter 1", [new Paragraph("Chapter 1", ParagraphStyle.Heading, null),
                              new Paragraph("“Hello there,” said Mara. The rain fell.", ParagraphStyle.Body, null),
                              new Paragraph("“Hush now,” said Tom.", ParagraphStyle.Body, null)]),
            new("Chapter 2", [new Paragraph("Chapter 2", ParagraphStyle.Heading, null),
                              new Paragraph("“Again,” said Tom. The wind rose.", ParagraphStyle.Body, null)]),
        };
        var p = ProjectBuilder.Make(new ImportedBook("Placeholder Book", "A. Writer", chapters), "t.txt");
        foreach (var c in p.Characters)
            c.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, c.Name == "Mara" ? "f" : "m"), 1, VoiceSource.User), VoiceEngine.Apple);
        p.Settings.SetNarratorVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "n"), 1, VoiceSource.User), VoiceEngine.Apple);
        p.Settings.CastingStyle = CastingStyle.FirstPerson;
        var mara = p.Characters.Single(c => c.Name == "Mara");
        p.Settings.PovCharacterID = mara.Id;
        return (p, mara);
    }

    static VoiceResolver Resolver(Project p, Action<FirstPersonSettings> body)
    {
        var f = new FirstPersonSettings();
        body(f);
        p.Settings.FirstPerson = f;
        return new VoiceResolver(p, VoiceEngine.Apple);
    }

    [Fact]
    public void ResolverRoutesTheICharacter()
    {
        var (p, mara) = Book();
        var segs = SpeechScript.Segments(0, p, new BookIndex(p), includeNarration: true);
        var line = segs.First(s => s.SpeakerID == mara.Id && s.Kind != LineKind.Written);
        var narr = segs.First(s => s.SpeakerID is null && s.LineID is null && s.Source is not null);
        var thought = line with { Kind = LineKind.Thought };
        var maraVoice = new VoiceChoice(VoiceEngine.Apple, "f");

        var r = Resolver(p, f => f.Voice = FirstPersonVoice.Same);
        Assert.Equal(r.Narrator, r.ChoiceFor(line));
        Assert.Null(r.ShadeFor(line, r.Narrator!));

        r = Resolver(p, f => f.Voice = FirstPersonVoice.Variant);
        Assert.Equal(r.Narrator, r.ChoiceFor(line));
        Assert.True(r.ShadeFor(line, r.Narrator!)!.SameAs(FirstPersonSettings.DefaultShade));
        // The narrator keeps the narration by default, unshaded.
        Assert.Equal(r.Narrator, r.ChoiceFor(narr));
        Assert.Null(r.ShadeFor(narr, r.Narrator!));
        Assert.Equal("n", r.Narrator!.VoiceID);  // never the "I" character's voice for the narrator

        r = Resolver(p, f => { f.Voice = FirstPersonVoice.Different; f.ReadsNarration = true; });
        Assert.Equal(maraVoice, r.ChoiceFor(line));
        Assert.Equal(maraVoice, r.ChoiceFor(narr));

        r = Resolver(p, f => { f.Voice = FirstPersonVoice.Variant; f.ReadsNarration = true; });
        Assert.Equal(r.Narrator, r.ChoiceFor(narr));
        Assert.NotNull(r.ShadeFor(narr, r.Narrator!));

        r = Resolver(p, f => { f.Voice = FirstPersonVoice.Different; f.VoicesThoughts = false; });
        Assert.Equal(r.Narrator, r.ChoiceFor(thought));
        Assert.Null(r.ShadeFor(thought, r.Narrator!));
        Assert.Equal(maraVoice, r.ChoiceFor(line));
    }

    [Fact]
    public void ChapterNarratorReadsTheirChapter()
    {
        var (p, mara) = Book();
        var tom = p.Characters.Single(c => c.Name == "Tom");
        var segs = SpeechScript.Segments(1, p, new BookIndex(p), includeNarration: true);
        var narr = segs.First(s => s.SpeakerID is null && s.LineID is null && s.Source is not null);
        var r = Resolver(p, f =>
        {
            f.Voice = FirstPersonVoice.Different;
            f.ReadsNarration = true;
            f.ChapterNarrators = new() { [p.Chapters[1].Id] = tom.Id };
        });
        Assert.Equal(tom.Id, r.Teller(narr));
        Assert.Equal(new VoiceChoice(VoiceEngine.Apple, "m"), r.ChoiceFor(narr));
        var first = SpeechScript.Segments(0, p, new BookIndex(p), includeNarration: true).First(s => s.SpeakerID is null && s.LineID is null && s.Source is not null);
        Assert.Equal(mara.Id, r.Teller(first));
    }

    [Fact]
    public void ProminentCharactersHaveThreePercentAndFiveLines()
    {
        var p = new Project { Title = "T" };
        var chapter = new Chapter("Chapter 1", []);
        p.Chapters.Add(chapter);
        BookCharacter Add(string name, int lines)
        {
            var c = new BookCharacter { Name = name, ColorHex = "#2F7FD0" };
            p.Characters.Add(c);
            for (int i = 0; i < lines; i++)
            {
                var para = new Paragraph($"“Line {i}.”", ParagraphStyle.Body, null);
                chapter.Paragraphs.Add(para);
                p.Lines.Add(new Line { ParagraphID = para.Id, Range = new TextSpan(0, para.Text.Length), SpeakerID = c.Id });
            }
            return c;
        }
        // 200 lines: the bar is 6 (3%, rounded up).
        var lead = Add("Lead", 150);
        var six = Add("Six", 6);
        var five = Add("Five", 5);
        Add("Rest", 39);
        var index = new BookIndex(p);
        Assert.Equal(6, CastProminence.Bar(p, index));
        var groups = CastProminence.Groups(p, index, p.Characters);
        Assert.Equal([CastProminence.ProminentTitle, CastProminence.MinorTitle], groups.Select(g => g.Title));
        Assert.Equal([lead, six, p.Characters[3]], groups[0].Members);
        Assert.Equal([five], groups[1].Members);
        // A small book: never fewer than 5 lines; an empty group is left out.
        var small = new Project { Title = "S" };
        Assert.Equal(5, CastProminence.Bar(small, new BookIndex(small)));
        Assert.Equal([CastProminence.ProminentTitle], CastProminence.Groups(p, index, [lead]).Select(g => g.Title));
    }
}
