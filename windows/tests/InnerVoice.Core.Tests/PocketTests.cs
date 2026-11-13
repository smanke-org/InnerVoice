using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// The Mac's PocketTTSTests (Pocket TTS replaced Qwen3-TTS, 2026-11-13): old books switch when
/// opened, the tones, and decoding engine values this version doesn't know. The old book is
/// the Mac's own (Fixtures/legacy-qwen.json, from tools/MacFixtures), and the Mac's migration of
/// it and of the Windows-saved Qwen3-TTS sample are compared field by field, so a book migrates
/// the same on both computers.
public class PocketTests
{
    static Project OldProject() => ProjectJson.Decode(File.ReadAllBytes(Fixtures.PathOf("legacy-qwen.json")));

    static BookCharacter Character(Project p, string name) => p.Characters.First(c => c.Name == name);

    [Fact]
    public void OldEngineReadsAsPocketTTS()
    {
        var p = OldProject();
        Assert.Equal(VoiceEngine.Pocket, p.Settings.VoiceEngine);
        // The voices still sit under "qwen" until migrated.
        Assert.True(LegacyVoices.NeedsMigration(p));
        Assert.Equal(VoiceEngine.Pocket, Character(p, "Mara Quinn").Voices!["qwen"].Choice.Engine);
    }

    [Fact]
    public void OldBooksSwitchToPocketTTS()
    {
        var p = OldProject();
        Assert.True(LegacyVoices.Migrate(p));
        Assert.False(LegacyVoices.NeedsMigration(p));
        Assert.All(p.Characters, c => Assert.False(c.Voices?.ContainsKey("qwen") == true));

        // Ready-made → nearest stock voice (Kind Grandmother → Mia, an older woman: lower and
        // slower), the narrator's own speed and pitch kept on top, still the narrator's choice.
        var edda = Character(p, "Edda Voss").Voice(VoiceEngine.Pocket)!;
        Assert.True(edda.Choice.VoiceID == "stock:mia" && edda.Source == VoiceSource.User);
        Assert.True(edda.Choice.Pitch == 0.85 && edda.Choice.Rate == 0.92);
        Assert.Null(edda.Choice.Design);

        // Designed → the stock voice casting picks for that character, a woman's for Mara.
        var mara = Character(p, "Mara Quinn").Voice(VoiceEngine.Pocket)!;
        Assert.True(mara.Choice.VoiceID.StartsWith("stock:") && mara.Source == VoiceSource.Auto);
        Assert.Equal(Gender.Female, VoiceInfo.Pocket.First(v => v.VoiceID == mara.Choice.VoiceID).Gender);

        // A designed voice the narrator chose stays theirs; the Orpheus voice doesn't move.
        var tom = Character(p, "Tom Hale");
        var tomVoice = tom.Voice(VoiceEngine.Pocket)!;
        Assert.Equal(VoiceSource.User, tomVoice.Source);
        Assert.Equal(Gender.Male, VoiceInfo.Pocket.First(v => v.VoiceID == tomVoice.Choice.VoiceID).Gender);
        Assert.True(tom.Voice(VoiceEngine.Orpheus)?.Choice.VoiceID == "leo" && tom.Voice(VoiceEngine.Orpheus)?.Source == VoiceSource.User);

        // A clone keeps its recording, now spoken by Pocket TTS.
        var pip = Character(p, "Pip").Voice(VoiceEngine.Pocket)!;
        Assert.Equal(new VoiceChoice(VoiceEngine.Pocket, "clone:ABC"), pip.Choice);
        Assert.Equal(VoiceSource.User, pip.Source);

        // The narrator's ready-made voice → the engine's own voice (Alba), still the narrator's choice.
        var narrator = p.Settings.NarratorVoice(VoiceEngine.Pocket)!;
        Assert.True(narrator.Choice.VoiceID == PocketVoice.BuiltInID && narrator.Source == VoiceSource.User);
        Assert.Equal("stock:alba", PocketVoice.BuiltInID);
        Assert.False(p.Settings.NarratorVoices?.ContainsKey("qwen") == true);

        // Roles: ready-made → stock; designed (the narrator's) → a stock voice of that gender, still theirs.
        Assert.Equal("stock:dan", p.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Pocket)?.Choice.VoiceID);
        var women = p.Settings.RoleVoice(VoiceRole.Women, VoiceEngine.Pocket)!;
        Assert.True(women.Source == VoiceSource.User && VoiceInfo.Pocket.First(v => v.VoiceID == women.Choice.VoiceID).Gender == Gender.Female);

        // Nobody without a voice got one, and the date casting last ran is kept.
        Assert.Single(Character(p, "Edda Voss").Voices!);
        Assert.Equal(700_000_000, p.VoicesCast?.Seconds);
        Assert.All(p.Characters.Where(c => c.Name is not ("Edda Voss" or "Mara Quinn" or "Tom Hale" or "Pip")), c => Assert.Null(c.Voice(VoiceEngine.Pocket)));

        // Saved and read again, nothing changes and nothing is left to migrate.
        var again = ProjectJson.Decode(ProjectJson.Encode(p));
        Assert.False(LegacyVoices.Migrate(again));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ProjectJson.Encode(p)), JsonNode.Parse(ProjectJson.Encode(again))));
        Assert.DoesNotContain("\"qwen\"", System.Text.Encoding.UTF8.GetString(ProjectJson.Encode(p)));
    }

    /// The same old book migrates to exactly what the Mac makes of it.
    [Fact]
    public void OldBooksMigrateLikeTheMac()
    {
        var p = OldProject();
        Assert.True(LegacyVoices.Migrate(p));
        var mac = RoundTripTests.Normalize(JsonNode.Parse(File.ReadAllBytes(Fixtures.PathOf("legacy-qwen-migrated-mac.json")))!);
        var windows = RoundTripTests.Normalize(JsonNode.Parse(ProjectJson.Encode(p))!);
        Assert.True(JsonNode.DeepEquals(mac, windows), RoundTripTests.Diff(mac, windows));
    }

    /// The Windows-saved sample from before Pocket TTS (a designed voice the narrator chose, a
    /// ready-made role voice) migrates as the Mac's WindowsFixtures test migrates it.
    [Fact]
    public void TheWindowsSavedQwenSampleMigratesLikeTheMac()
    {
        var contents = ProjectArchive.Read(File.ReadAllBytes(Fixtures.PathOf("sample-windows.innervoice")));
        var p = ProjectJson.Decode(contents.ProjectJson);
        Assert.Equal(VoiceEngine.Pocket, p.Settings.VoiceEngine);
        Assert.Contains(p.Characters, c => c.Voices?.GetValueOrDefault("qwen")?.Choice.VoiceID == "design");
        Assert.True(LegacyVoices.Migrate(p));
        Assert.False(LegacyVoices.NeedsMigration(p));
        Assert.Contains(p.Characters, c => c.Voice(VoiceEngine.Pocket) is { Source: VoiceSource.User } v && v.Choice.VoiceID.StartsWith("stock:"));
        var mac = RoundTripTests.Normalize(JsonNode.Parse(File.ReadAllBytes(Fixtures.PathOf("sample-windows-migrated-mac.json")))!);
        var windows = RoundTripTests.Normalize(JsonNode.Parse(ProjectJson.Encode(p))!);
        Assert.True(JsonNode.DeepEquals(mac, windows), RoundTripTests.Diff(mac, windows));
    }

    /// A book the new Mac saved (engine "pocket", stock voices) opens here and saves back
    /// unchanged, with nothing to migrate.
    [Fact]
    public void AMacSavedPocketBookOpensAndSavesUnchanged()
    {
        var contents = ProjectArchive.Read(File.ReadAllBytes(Fixtures.PathOf("sample-mac.innervoice")));
        var p = ProjectJson.Decode(contents.ProjectJson);
        Assert.Equal(VoiceEngine.Pocket, p.Settings.VoiceEngine);
        var tom = p.Characters.First(c => c.Name.StartsWith("Tom"));
        Assert.Equal(new VoiceChoice(VoiceEngine.Pocket, "stock:dan", pitch: 0.89), tom.Voice(VoiceEngine.Pocket)?.Choice);
        Assert.Equal("stock:leo", p.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Pocket)?.Choice.VoiceID);
        Assert.False(LegacyVoices.NeedsMigration(p));
        Assert.False(LegacyVoices.Migrate(p));
        var mac = RoundTripTests.Normalize(JsonNode.Parse(contents.ProjectJson)!);
        var windows = RoundTripTests.Normalize(JsonNode.Parse(ProjectJson.Encode(p))!);
        Assert.True(JsonNode.DeepEquals(mac, windows), RoundTripTests.Diff(mac, windows));
        // And the voices are ones this PC can speak.
        Assert.NotNull(VoiceInfo.Pocket.FirstOrDefault(v => v.VoiceID == "stock:dan"));
    }

    [Fact]
    public void ProjectsWithoutQwenAreLeftAlone()
    {
        var p = ProjectBuilder.Make(new ImportedBook("Test", "", [new Chapter("Chapter 1", [new Paragraph("“Hi,” said Ann.", ParagraphStyle.Body, null)])]), "t.txt");
        p.Characters[0].SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Orpheus, "tara"), 1, VoiceSource.User), VoiceEngine.Orpheus);
        var before = ProjectJson.Encode(p);
        Assert.False(LegacyVoices.Migrate(p));
        Assert.Equal(before, ProjectJson.Encode(p));
    }

    [Fact]
    public void EveryQwenPresetHasAStockVoiceOfItsGender()
    {
        var genders = new Dictionary<string, Gender>
        {
            ["storyteller"] = Gender.Female, ["bright"] = Gender.Female, ["soft"] = Gender.Female, ["brisk"] = Gender.Female, ["dame"] = Gender.Female,
            ["grandmother"] = Gender.Female, ["lass"] = Gender.Female, ["girl"] = Gender.Female, ["baritone"] = Gender.Male, ["hero"] = Gender.Male,
            ["gentleman"] = Gender.Male, ["elder"] = Gender.Male, ["sailor"] = Gender.Male, ["clerk"] = Gender.Male, ["boy"] = Gender.Male,
        };
        Assert.Equal(16, LegacyVoices.Presets.Count);
        foreach (var (preset, gender) in genders)
        {
            var mapped = LegacyVoices.Mapped(new CastVoice(new VoiceChoice(VoiceEngine.Pocket, "preset:" + preset), 1, VoiceSource.Auto));
            Assert.Equal(gender, VoiceInfo.Pocket.First(v => v.VoiceID == mapped?.Choice.VoiceID).Gender);
        }
        // Children sound higher and a little quicker.
        var girl = LegacyVoices.Mapped(new CastVoice(new VoiceChoice(VoiceEngine.Pocket, "preset:girl"), 1, VoiceSource.Auto));
        Assert.True(PocketVoice.Semitones(girl?.Choice.Pitch) == 3 && girl?.Choice.Rate == 1.05);
        Assert.Null(LegacyVoices.Mapped(new CastVoice(new VoiceChoice(VoiceEngine.Pocket, "design", "A man."), 1, VoiceSource.Auto)));
    }

    [Fact]
    public void UnknownEnginesDoNotStopAProjectOpening()
    {
        var choice = System.Text.Json.JsonSerializer.Deserialize<VoiceChoice>("""{"engine":"someday-engine","voiceID":"x"}""", ProjectJson.Options)!;
        // Mixed names no voices, so the player falls back to the narrator.
        Assert.Equal(VoiceEngine.Mixed, choice.Engine);
        Assert.Equal([VoiceEngine.Apple, VoiceEngine.Pocket, VoiceEngine.Orpheus, VoiceEngine.Elevenlabs, VoiceEngine.Mixed, VoiceEngine.Pocket],
            System.Text.Json.JsonSerializer.Deserialize<VoiceEngine[]>("""["apple","chatterbox","orpheus","elevenlabs","mixed","qwen"]""", ProjectJson.Options));
        // Saved as "pocket"; the unreleased test build's "chatterbox" reads as Pocket TTS.
        Assert.Equal("\"pocket\"", System.Text.Json.JsonSerializer.Serialize(VoiceEngine.Pocket, ProjectJson.Options));
        // Outside a project too (the attribute on the enum).
        Assert.Equal(VoiceEngine.Pocket, System.Text.Json.JsonSerializer.Deserialize<VoiceEngine>("\"qwen\""));
        // A whole project with an engine from the future still opens.
        var p = ProjectJson.Decode(System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Fixtures.PathOf("legacy-qwen.json"))).Replace("\"voiceEngine\":\"qwen\"", "\"voiceEngine\":\"hologram\"")));
        Assert.Equal(VoiceEngine.Mixed, p.Settings.VoiceEngine);
    }

    [Fact]
    public void TonesSetPocketTTSTemperaturePaceAndPitch()
    {
        var all = Tones.All.Select(t => (Tone: t, D: PocketDelivery.For(t))).ToList();
        // Within what the model says clearly, and small enough steps to keep the voice.
        Assert.All(all, x => Assert.True(x.D.Temperature is >= 0.5 and <= 0.95 && Math.Abs(x.D.Semitones) <= 1 && x.D.Rate is >= 0.9 and <= 1.1));
        // Neutral and all narration are the model's own default, an even read.
        Assert.Equal(new PocketDelivery(0.7), PocketDelivery.For(Tone.Neutral));
        Assert.Equal(PocketDelivery.DefaultTemperature, PocketDelivery.Even.Temperature);
        Assert.All(Tones.All, t => Assert.Equal(PocketDelivery.Even, PocketDelivery.For(t, narration: true)));
        double T(Tone x) => PocketDelivery.For(x).Temperature;
        Assert.True(T(Tone.Angry) > T(Tone.Excited) && T(Tone.Excited) > T(Tone.Scared) && T(Tone.Scared) > T(Tone.Sarcastic) && T(Tone.Sarcastic) > T(Tone.Neutral));
        Assert.True(T(Tone.Neutral) > T(Tone.Tender) && T(Tone.Tender) > T(Tone.Sad) && T(Tone.Sad) == T(Tone.Whispered));
        double R(Tone x) => PocketDelivery.For(x).Rate;
        Assert.True(R(Tone.Excited) > 1 && R(Tone.Angry) > 1 && R(Tone.Scared) > 1 && R(Tone.Sad) < R(Tone.Tender) && R(Tone.Tender) < 1);
        Assert.True(PocketDelivery.For(Tone.Excited).Semitones == 1 && PocketDelivery.For(Tone.Sad).Semitones == -1);
        // Pocket TTS can't whisper: a whispered line is said softly, the quietest of all.
        Assert.All(all, x => Assert.True(x.D.Volume >= PocketDelivery.For(Tone.Whispered).Volume));
        Assert.True(PocketDelivery.For(Tone.Whispered).Volume < PocketDelivery.For(Tone.Sad).Volume && PocketDelivery.For(Tone.Sad).Volume < 1);
        // The Mac's exact table.
        Assert.Equal(new PocketDelivery(0.9, 1, 1.06), PocketDelivery.For(Tone.Excited));
        Assert.Equal(new PocketDelivery(0.95, 0, 1.04), PocketDelivery.For(Tone.Angry));
        Assert.Equal(new PocketDelivery(0.85, 1, 1.06), PocketDelivery.For(Tone.Scared));
        Assert.Equal(new PocketDelivery(0.75, 0, 0.97), PocketDelivery.For(Tone.Sarcastic));
        Assert.Equal(new PocketDelivery(0.75, 0, 0.96), PocketDelivery.For(Tone.Confused));
        Assert.Equal(new PocketDelivery(0.55, 0, 0.95, 0.9), PocketDelivery.For(Tone.Tender));
        Assert.Equal(new PocketDelivery(0.5, -1, 0.92, 0.9), PocketDelivery.For(Tone.Sad));
        Assert.Equal(new PocketDelivery(0.5, 0, 0.95, 0.7), PocketDelivery.For(Tone.Whispered));
    }

    [Fact]
    public void PitchBecomesWholeSemitonesOfTheClip()
    {
        Assert.Equal(0, PocketVoice.Semitones(null));
        Assert.Equal(0, PocketVoice.Semitones(1));
        Assert.Equal(3, PocketVoice.Semitones(1.19));
        Assert.Equal(-2, PocketVoice.Semitones(0.89));
        Assert.Equal(4, PocketVoice.Semitones(3));
        Assert.True(PocketVoice.StockName("stock:tara") == "tara" && PocketVoice.StockName("clone:x") is null);
        // Clones are never made older or younger by casting: they're a real person's voice.
        var clone = new VoiceInfo(VoiceEngine.Pocket, "clone:x", "Me", Gender.Female, VoiceAge.Adult, "");
        Assert.Equal(clone.Choice, VoiceCasting.AgeAdjusted(clone, VoiceAge.Child));
    }
}
