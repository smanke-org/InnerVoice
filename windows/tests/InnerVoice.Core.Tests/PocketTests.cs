using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// The Mac's PocketTTSTests, and the file format since Qwen3-TTS came back beside Pocket TTS
/// (2026-11-14): a book saved with Qwen3-TTS opens as Qwen3-TTS, with its voices as they were
/// and nothing migrated (the old book is the Mac's own, Fixtures/legacy-qwen.json from
/// tools/MacFixtures, and the Windows-saved Qwen3-TTS sample); "chatterbox" still reads as
/// Pocket TTS and an engine this version doesn't know as Mix Engines; and the tones.
public class PocketTests
{
    static Project OldProject() => ProjectJson.Decode(File.ReadAllBytes(Fixtures.PathOf("legacy-qwen.json")));

    static BookCharacter Character(Project p, string name) => p.Characters.First(c => c.Name == name);

    /// Every number as the double it means, so two ways of writing one compare equal.
    static JsonNode Numbers(JsonNode n) => n switch
    {
        JsonObject o => new JsonObject(o.Select(kv => KeyValuePair.Create(kv.Key, kv.Value is null ? null : Numbers(kv.Value)))),
        JsonArray a => new JsonArray(a.Select(x => x is null ? null : Numbers(x)).ToArray()),
        JsonValue v when v.GetValueKind() == System.Text.Json.JsonValueKind.Number => JsonValue.Create(v.GetValue<double>()),
        _ => n.DeepClone(),
    };

    [Fact]
    public void AQwenBookStaysQwen()
    {
        var p = OldProject();
        Assert.Equal(VoiceEngine.Qwen, p.Settings.VoiceEngine);
        // The voices stay under "qwen", spoken by Qwen3-TTS: presets, designs and clones.
        Assert.All(p.Characters.Where(c => c.Voices?.ContainsKey("qwen") == true), c => Assert.Equal(VoiceEngine.Qwen, c.Voices!["qwen"].Choice.Engine));
        var edda = Character(p, "Edda Voss").Voice(VoiceEngine.Qwen)!;
        Assert.True(edda.Choice.VoiceID == "preset:grandmother" && edda.Source == VoiceSource.User && edda.Choice.Pitch == 0.95);
        var mara = Character(p, "Mara Quinn").Voice(VoiceEngine.Qwen)!;
        Assert.Equal(new VoiceChoice(VoiceEngine.Qwen, "design", "A woman in her thirties with a warm voice."), mara.Choice);
        Assert.Equal(new VoiceChoice(VoiceEngine.Qwen, "clone:ABC"), Character(p, "Pip").Voice(VoiceEngine.Qwen)?.Choice);
        Assert.Equal("preset:narrator", p.Settings.NarratorVoice(VoiceEngine.Qwen)?.Choice.VoiceID);
        Assert.Equal("preset:baritone", p.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Qwen)?.Choice.VoiceID);
        // Nothing moved to Pocket TTS.
        Assert.All(p.Characters, c => Assert.Null(c.Voice(VoiceEngine.Pocket)));
        Assert.Null(p.Settings.NarratorVoice(VoiceEngine.Pocket));
        // Saved back exactly as the Mac wrote it (the same numbers: JSONSerialization wrote
        // them with 17 digits, 0.94999999999999996 for 0.95).
        var mac = Numbers(RoundTripTests.Normalize(JsonNode.Parse(File.ReadAllBytes(Fixtures.PathOf("legacy-qwen.json")))!));
        var windows = Numbers(RoundTripTests.Normalize(JsonNode.Parse(ProjectJson.Encode(p))!));
        Assert.True(JsonNode.DeepEquals(mac, windows), RoundTripTests.Diff(mac, windows));
    }

    /// The Windows-saved sample from before Pocket TTS (a designed voice the narrator chose, a
    /// ready-made role voice) opens as Qwen3-TTS and saves back unchanged.
    [Fact]
    public void TheWindowsSavedQwenSampleOpensAsQwen()
    {
        var contents = ProjectArchive.Read(File.ReadAllBytes(Fixtures.PathOf("sample-windows.innervoice")));
        var p = ProjectJson.Decode(contents.ProjectJson);
        Assert.Equal(VoiceEngine.Qwen, p.Settings.VoiceEngine);
        Assert.Contains(p.Characters, c => c.Voice(VoiceEngine.Qwen) is { Source: VoiceSource.User } v && v.Choice.VoiceID == "design");
        var before = RoundTripTests.Normalize(JsonNode.Parse(contents.ProjectJson)!);
        var after = RoundTripTests.Normalize(JsonNode.Parse(ProjectJson.Encode(p))!);
        Assert.True(JsonNode.DeepEquals(before, after), RoundTripTests.Diff(before, after));
    }

    /// A book the Mac saved with Pocket TTS (engine "pocket", stock voices) opens here and saves
    /// back unchanged.
    [Fact]
    public void AMacSavedPocketBookOpensAndSavesUnchanged()
    {
        var contents = ProjectArchive.Read(File.ReadAllBytes(Fixtures.PathOf("sample-mac.innervoice")));
        var p = ProjectJson.Decode(contents.ProjectJson);
        Assert.Equal(VoiceEngine.Pocket, p.Settings.VoiceEngine);
        var tom = p.Characters.First(c => c.Name.StartsWith("Tom"));
        Assert.Equal(new VoiceChoice(VoiceEngine.Pocket, "stock:dan", pitch: 0.89), tom.Voice(VoiceEngine.Pocket)?.Choice);
        Assert.Equal("stock:leo", p.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Pocket)?.Choice.VoiceID);
        var mac = RoundTripTests.Normalize(JsonNode.Parse(contents.ProjectJson)!);
        var windows = RoundTripTests.Normalize(JsonNode.Parse(ProjectJson.Encode(p))!);
        Assert.True(JsonNode.DeepEquals(mac, windows), RoundTripTests.Diff(mac, windows));
        // And the voices are ones this PC can speak.
        Assert.NotNull(VoiceInfo.Pocket.FirstOrDefault(v => v.VoiceID == "stock:dan"));
    }

    /// The sixteen ready-made Qwen3-TTS voices, with the Mac's voice IDs, narrator first.
    [Fact]
    public void TheSixteenQwenPresets()
    {
        var genders = new Dictionary<string, Gender>
        {
            ["narrator"] = Gender.Neutral, ["storyteller"] = Gender.Female, ["bright"] = Gender.Female, ["soft"] = Gender.Female, ["brisk"] = Gender.Female,
            ["dame"] = Gender.Female, ["grandmother"] = Gender.Female, ["lass"] = Gender.Female, ["girl"] = Gender.Female, ["baritone"] = Gender.Male,
            ["hero"] = Gender.Male, ["gentleman"] = Gender.Male, ["elder"] = Gender.Male, ["sailor"] = Gender.Male, ["clerk"] = Gender.Male, ["boy"] = Gender.Male,
        };
        Assert.Equal(genders.Keys.Select(k => "preset:" + k), VoiceInfo.QwenPresets.Select(v => v.VoiceID));
        foreach (var v in VoiceInfo.QwenPresets)
        {
            Assert.Equal(genders[v.VoiceID["preset:".Length..]], v.Gender);
            Assert.Equal(VoiceEngine.Qwen, v.Engine);
            Assert.False(string.IsNullOrWhiteSpace(v.Design));
        }
    }

    [Fact]
    public void UnknownEnginesDoNotStopAProjectOpening()
    {
        var choice = System.Text.Json.JsonSerializer.Deserialize<VoiceChoice>("""{"engine":"someday-engine","voiceID":"x"}""", ProjectJson.Options)!;
        // Mixed names no voices, so the player falls back to the narrator.
        Assert.Equal(VoiceEngine.Mixed, choice.Engine);
        Assert.Equal([VoiceEngine.Apple, VoiceEngine.Pocket, VoiceEngine.Orpheus, VoiceEngine.Elevenlabs, VoiceEngine.Mixed, VoiceEngine.Qwen, VoiceEngine.Pocket],
            System.Text.Json.JsonSerializer.Deserialize<VoiceEngine[]>("""["apple","chatterbox","orpheus","elevenlabs","mixed","qwen","pocket"]""", ProjectJson.Options));
        // Saved as "pocket" and "qwen"; the unreleased test build's "chatterbox" reads as Pocket TTS.
        Assert.Equal("\"pocket\"", System.Text.Json.JsonSerializer.Serialize(VoiceEngine.Pocket, ProjectJson.Options));
        Assert.Equal("\"qwen\"", System.Text.Json.JsonSerializer.Serialize(VoiceEngine.Qwen, ProjectJson.Options));
        // Outside a project too (the attribute on the enum): "qwen" is Qwen3-TTS, never Pocket TTS.
        Assert.Equal(VoiceEngine.Qwen, System.Text.Json.JsonSerializer.Deserialize<VoiceEngine>("\"qwen\""));
        Assert.Equal(VoiceEngine.Pocket, System.Text.Json.JsonSerializer.Deserialize<VoiceEngine>("\"chatterbox\""));
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
