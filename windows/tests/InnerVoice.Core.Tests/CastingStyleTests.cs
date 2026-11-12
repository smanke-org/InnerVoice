using System.Text.Json.Nodes;
using InnerVoice.Core;

namespace InnerVoice.Core.Tests;

/// Casting Style gives the Mac's answers (Fixtures/casting-style.json, written by the Mac's
/// WindowsFixtures.writeCastingFixture): who reads each character, shades and narrators.
public class CastingStyleTests
{
    /// The same project the Mac's castingProject() makes.
    static Project CastingProject()
    {
        var p = Fixtures.SampleProject();
        void Edit(string prefix, Action<BookCharacter> body)
        {
            if (p.Characters.FirstOrDefault(c => c.Name.StartsWith(prefix, StringComparison.Ordinal)) is { } c) body(c);
        }
        Edit("Tom", c => c.Facts = [new Fact { Kind = FactKind.Age, Text = "About sixty" }, new Fact { Kind = FactKind.Voice, Text = "A gravelly, deep voice, a little husky" }]);
        Edit("Mara", c => c.VoiceNotes = "Steady.\n\nVoice brief:\nPitch: high and bright\nPace: quick, clipped");
        Edit("Pip", c => c.Facts = [new Fact { Kind = FactKind.Appearance, Text = "A small boy" }]);
        return p;
    }

    static Dictionary<string, Action<Project>> Scenarios => new()
    {
        ["just me, a woman narrating"] = p => { p.Settings.CastingStyle = CastingStyle.OneVoice; p.Settings.NarratorGender = CharacterGender.Female; },
        ["just me, a man narrating, strong"] = p =>
        {
            p.Settings.CastingStyle = CastingStyle.OneVoice; p.Settings.NarratorGender = CharacterGender.Male; p.Settings.ShadeStrength = ShadeStrength.Strong;
        },
        ["just me, not said, subtle, Tom's own shade"] = p =>
        {
            p.Settings.CastingStyle = CastingStyle.OneVoice;
            p.Settings.ShadeStrength = ShadeStrength.Subtle;
            if (p.Characters.FirstOrDefault(c => c.Name.StartsWith("Tom")) is { } tom) tom.Shade = Shade.Make(1, 0, "warm");
        },
        ["three narrators, a two-person book"] = p =>
        {
            p.Settings.CastingStyle = CastingStyle.Shared;
            p.Settings.NarrationRole = VoiceRole.Women;
            p.Narrators = [new Narrator { Name = "Alex", Role = VoiceRole.Men }, new Narrator { Name = "Jo", Role = VoiceRole.Women }];
            foreach (var c in p.Characters) c.NarratorID = null;
            if (p.Characters.FirstOrDefault(c => c.Name.StartsWith("Pip")) is { } pip) pip.Gender = CharacterGender.Other;
        },
        ["full cast, Mara main, minor by gender"] = p =>
        {
            p.Settings.CastingStyle = CastingStyle.FullCast;
            p.Settings.MinorVoices = MinorVoices.ByGender;
            p.Narrators = [new Narrator { Name = "Sam", Role = VoiceRole.Narrator }, new Narrator { Name = "Alex", Role = VoiceRole.Men }];
            if (p.Characters.FirstOrDefault(c => c.Name.StartsWith("Mara")) is { } mara) mara.IsMain = true;
        },
        ["full cast, nobody main"] = p => p.Settings.CastingStyle = CastingStyle.FullCast,
        ["just me, a woman narrating, Tom set male"] = p =>
        {
            p.Settings.CastingStyle = CastingStyle.OneVoice;
            p.Settings.NarratorGender = CharacterGender.Female;
            if (p.Characters.FirstOrDefault(c => c.Name.StartsWith("Tom")) is { } tom) tom.Gender = CharacterGender.Male;
        },
    };

    static string Raw(VoiceRole r) => r switch
    {
        VoiceRole.Narrator => "narrator", VoiceRole.Men => "men", VoiceRole.Women => "women", VoiceRole.Other => "other", _ => "own",
    };

    [Fact]
    public void ReviewAndSuggestionsMatchTheMac()
    {
        var golden = Fixtures.Json("casting-style.json");
        var p = CastingProject();
        var index = new BookIndex(p);
        string[] Names(IEnumerable<Guid> ids) => ids.Select(id => p.Character(id)!.Name).ToArray();
        Assert.Equal(golden["genderToReview"]!.AsArray().Select(n => (string)n!).ToArray(), Names(CastingStyleRules.GenderToReview(p, index)));
        Assert.Equal(golden["suggestedMain"]!.AsArray().Select(n => (string)n!).ToArray(), Names(CastingStyleRules.SuggestedMain(p, index)));
    }

    [Fact]
    public void EveryScenarioMatchesTheMac()
    {
        var golden = Fixtures.Json("casting-style.json")["scenarios"]!.AsArray();
        Assert.Equal(Scenarios.Count, golden.Count);
        foreach (var scenario in golden)
        {
            var name = (string)scenario!["name"]!;
            var p = CastingProject();
            Scenarios[name](p);
            var index = new BookIndex(p);
            var readers = CastingStyleRules.Readers(p, index);
            foreach (var (who, role) in scenario["readers"]!.AsObject())
                Assert.True((string)role! == Raw(readers[p.Characters.First(c => c.Name == who).Id]), $"{name}: {who} is read by {role}");
            var shades = CastingStyleRules.Shades(p, index, p.Settings.NarratorGender);
            var expected = scenario["shades"]!.AsObject();
            Assert.True(expected.Count == shades.Count, $"{name}: {shades.Count} shades, the Mac has {expected.Count}");
            foreach (var (who, s) in expected)
            {
                var mine = shades[p.Characters.First(c => c.Name == who).Id];
                Assert.True((int)s!["pitch"]! == mine.Pitch && (int)s["pace"]! == mine.Pace && (string?)s["words"] == mine.Words,
                            $"{name}: {who} is {mine.Pitch} {mine.Pace} {mine.Words}, the Mac has {s.ToJsonString()}");
            }
            var narrators = scenario["narrators"]!.AsObject();
            foreach (var c in p.Characters)
            {
                var g = c.Gender ?? ShadeProfile.Read(c, p, index).Gender?.ToCharacterGender();
                var n = CastingStyleRules.NarratorFor(c, p, g);
                Assert.True(narrators.TryGetPropertyValue(c.Name, out var want) ? (string?)want == n?.Name : n is null,
                            $"{name}: {c.Name} is read by {n?.Name ?? "nobody"}");
            }
        }
    }

    [Fact]
    public void NewFieldsAreModeledAndRoundTrip()
    {
        var p = CastingProject();
        p.Settings.CastingStyle = CastingStyle.Shared;
        p.Settings.NarratorGender = CharacterGender.Female;
        p.Characters[0].Gender = CharacterGender.Other;
        p.Characters[0].Shade = Shade.Make(-9, 5, "  ");
        p.Narrators = [new Narrator { Name = "Jo", Role = VoiceRole.Women }];
        var json = JsonNode.Parse(ProjectJson.Encode(p))!;
        Assert.Equal("shared", (string?)json["settings"]!["castingStyle"]);
        Assert.Equal("other", (string?)json["characters"]![0]!["gender"]);
        Assert.Equal(-4, (int)json["characters"]![0]!["shade"]!["pitch"]!);
        Assert.Null(json["characters"]![0]!["shade"]!["words"]);
        Assert.Equal("women", (string?)json["narrators"]![0]!["role"]);
        var back = ProjectJson.Decode(ProjectJson.Encode(p));
        Assert.Equal(CastingStyle.Shared, back.Settings.CastingStyle);
        Assert.Equal(VoiceRole.Women, back.Narrators[0].Role);
        // Unset fields are left out, as the Mac writes them.
        var plain = JsonNode.Parse(ProjectJson.Encode(Fixtures.SampleProject()))!;
        Assert.Null(plain["settings"]!["castingStyle"]);
        Assert.Null(plain["characters"]![0]!["isMain"]);
    }
}
