using System.Text.Json;
using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.AI;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Ports of Tests/InnerVoiceKitTests/PhysicalProfileTests.swift.
public class PhysicalProfileTests
{
    const string Reply = "{\"summary\":\"A tall, wiry woman with cropped gray hair.\",\"summaryInferred\":true,\"age\":{\"text\":\"about sixty\",\"inferred\":true},\"height\":{\"text\":\"Tall\",\"inferred\":false},\"build\":{\"text\":\"\",\"inferred\":false},\"hair\":{\"text\":\"Long and gray\",\"inferred\":false},\"eyes\":{\"text\":\"unknown\",\"inferred\":false},\"skin\":{\"text\":\"\",\"inferred\":false},\"face\":{\"text\":\"\",\"inferred\":false},\"marks\":{\"text\":\"Scar on her left hand\",\"inferred\":false},\"clothing\":{\"text\":\"\",\"inferred\":false},\"other\":{\"text\":\"\",\"inferred\":false},\"changes\":[{\"chapter\":3,\"field\":\"hair\",\"text\":\"cuts it short\",\"inferred\":false},{\"chapter\":99,\"field\":\"marks\",\"text\":\"Burned\",\"inferred\":true}],\"flags\":[{\"field\":\"eyes\",\"text\":\"Blue in chapter 1, green in chapter 4\"},{\"field\":\"wings\",\"text\":\"x\"}]}";

    static PhysicalProfile Parsed() => PhysicalProfilePrompt.Parse(Reply, 10)!;

    [Fact]
    public void ParsesWithInferredLabelsChangesAndFlags()
    {
        var p = PhysicalProfilePrompt.Parse(Reply, 10);
        Assert.NotNull(p);
        Assert.True(p.SummaryInferred);
        Assert.True(p.Value(PhysicalField.Age).Text == "About sixty" && p.Value(PhysicalField.Age).Inferred);
        Assert.False(p.Value(PhysicalField.Height).Inferred);
        Assert.False(p.Values.ContainsKey("eyes") || p.Values.ContainsKey("build"));  // "unknown" and "" are left empty
        Assert.Equal(2, p.Changes.Count);
        Assert.True(p.Changes[0].Chapter == 2 && p.Changes[0].Text == "Cuts it short");
        Assert.Null(p.Changes[1].Chapter);  // past the last chapter
        Assert.True(p.Flags.Count == 1 && p.Flags[0].Field == PhysicalField.Eyes);
        Assert.Null(PhysicalProfilePrompt.Parse("no json", 3));
    }

    [Fact]
    public void IsReplacedOnlyWhileUnedited()
    {
        var p = Parsed();
        var c = new BookCharacter("Edda", "#10B981");
        Assert.True(PhysicalProfile.Put(p, c));
        Assert.False(PhysicalProfile.IsEdited(c));
        var fresh = Parsed(); fresh.Summary = "Rewritten.";
        Assert.True(PhysicalProfile.Put(fresh, c) && c.Physical!.Summary == "Rewritten.");
        c.Physical!.Set(PhysicalField.Eyes, new PhysicalValue("Gray"));
        Assert.True(PhysicalProfile.IsEdited(c));
        var again = Parsed(); again.Summary = "Third.";
        Assert.False(PhysicalProfile.Put(again, c));
        Assert.Equal("Gray", c.Physical.Value(PhysicalField.Eyes).Text);
    }

    [Fact]
    public void RoundTripsAsAJsonObject()
    {
        var c = new BookCharacter("Edda", "#10B981") { Physical = Parsed() };
        var json = JsonSerializer.Serialize(c, ProjectJson.Options);
        Assert.Contains("\"hair\":{", json);  // fields keyed by name, same as the Mac
        var back = JsonSerializer.Deserialize<BookCharacter>(json, ProjectJson.Options)!;
        Assert.True(PhysicalProfile.Same(back.Physical, c.Physical));
        Assert.Null(back.Extra);
        // Older books have no profile.
        var old = JsonSerializer.Deserialize<BookCharacter>(JsonSerializer.Serialize(new BookCharacter("A", "#10B981"), ProjectJson.Options), ProjectJson.Options)!;
        Assert.Null(old.Physical);
        Assert.DoesNotContain("physical", JsonSerializer.Serialize(new BookCharacter("A", "#10B981"), ProjectJson.Options));
    }

    [Fact]
    public void MacWrittenProfileRoundTripsUnchanged()
    {
        // As Swift's JSONEncoder (sorted keys) writes a BookCharacter's profile.
        const string mac = "{\"aliases\":[],\"colorHex\":\"#10B981\",\"facts\":[],\"id\":\"7B1C0E5A-2F4D-4C61-9A3E-5D8B6F0A1C22\",\"name\":\"Edda\",\"notes\":\"\",\"physical\":" +
            "{\"changes\":[{\"chapter\":2,\"field\":\"hair\",\"id\":\"11111111-2222-3333-4444-555555555555\",\"inferred\":false,\"text\":\"Cuts it short\"},{\"field\":\"marks\",\"id\":\"66666666-7777-8888-9999-AAAAAAAAAAAA\",\"inferred\":true,\"text\":\"Burned\"}]," +
            "\"flags\":[{\"field\":\"eyes\",\"id\":\"BBBBBBBB-CCCC-DDDD-EEEE-FFFFFFFFFFFF\",\"text\":\"Blue in chapter 1, green in chapter 4\"}]," +
            "\"summary\":\"A tall woman.\",\"summaryInferred\":true," +
            "\"values\":{\"age\":{\"inferred\":true,\"text\":\"About sixty\"},\"hair\":{\"inferred\":false,\"text\":\"Long and gray\"}}}," +
            "\"physicalGenerated\":{\"changes\":[],\"flags\":[],\"summary\":\"A tall woman.\",\"summaryInferred\":true,\"values\":{}},\"voiceNotes\":\"\"}";
        var c = JsonSerializer.Deserialize<BookCharacter>(mac, ProjectJson.Options)!;
        Assert.Null(c.Extra);
        Assert.Equal("A tall woman.", c.Physical!.Summary);
        Assert.True(c.Physical.Value(PhysicalField.Age).Inferred);
        Assert.Equal(2, c.Physical.Changes[0].Chapter);
        Assert.Null(c.Physical.Changes[1].Chapter);
        Assert.NotNull(c.PhysicalGenerated);
        Assert.Null(c.Physical.Extra);
        var again = ProjectJson.SortKeys(JsonSerializer.SerializeToNode(c, ProjectJson.Options))!.ToJsonString();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(mac), JsonNode.Parse(again)), again);
    }

    [Fact]
    public void CopyIsDeep()
    {
        var c = new BookCharacter("Edda", "#10B981") { Physical = Parsed() };
        var copy = c.Copy();
        copy.Physical!.Summary = "Other";
        copy.Physical.Values["hair"].Text = "Red";
        Assert.NotEqual("Other", c.Physical.Summary);
        Assert.Equal("Long and gray", c.Physical.Value(PhysicalField.Hair).Text);
    }

    [Fact]
    public void CharacterProfilesMarkInferredItems()
    {
        var proj = ProjectBuilder.Make(new ImportedBook("T", "", [new Chapter("Chapter 1", [new Paragraph("“Hello,” said Edda.", ParagraphStyle.Body, null)])]), "t.txt");
        var edda = proj.Characters.First(c => c.Name == "Edda");
        edda.Physical = Parsed();
        var md = CharacterProfiles.Markdown(proj, new BookIndex(proj));
        Assert.Contains("## Edda", md);
        Assert.Contains("### Physical Description", md);
        Assert.Contains("- **Apparent Age:** About sixty *(inferred)*", md);
        Assert.Contains("**Continuity to check**", md);
    }

    [Fact]
    public void BuildSummariesCostIncludesTheProfilePass()
    {
        var proj = ProjectBuilder.Make(new ImportedBook("T", "", [new Chapter("Chapter 1", [new Paragraph("“Hello,” said Edda.", ParagraphStyle.Body, null)])]), "t.txt");
        var one = CostEstimator.BuildSummaries(proj, 1);
        Assert.True(one.OutputTokens >= (int)((700 + 260 + 500) * CostEstimator.Thinking));
    }
}
