using System.Text.Json.Nodes;
using InnerVoice.Core;

namespace InnerVoice.Core.Tests;

/// A project saved by the Mac opens and saves on Windows without losing or changing anything.
public class RoundTripTests
{
    static ProjectArchive.Contents MacContents() => ProjectArchive.Read(File.ReadAllBytes(Fixtures.PathOf("sample-mac.innervoice")));

    [Fact]
    public void MacProjectDecodes()
    {
        var c = MacContents();
        var p = ProjectJson.Decode(c.ProjectJson);
        Assert.Equal("Sample Story", p.Title);
        Assert.Equal(3, p.Chapters.Count);
        Assert.Equal(ProjectPurpose.AiVoices, p.Settings.Purpose);
        Assert.Equal(799_999_999.123456, p.ImportedAt.Seconds);
        Assert.Contains(p.Characters, ch => ch.Name.StartsWith("Tom") && ch.IsPinned && ch.Facts.Count == 1);
        Assert.Equal("gemma-4-e2b", p.Lines[0].AiModel);
        Assert.Equal(2, p.Recorded.Count);
        Assert.Single(p.Retakes);
        Assert.Single(p.Revisions);
        Assert.True(c.Audio.ContainsKey("Kerensa.m4a"));
        Assert.Equal("Sample Story.md", c.Original?.Name);
    }

    [Fact]
    public void ModeledFieldsNeverLandInExtra()
    {
        // A misspelled property name would put a known field in Extra and drop it from the model.
        var p = ProjectJson.Decode(MacContents().ProjectJson);
        Assert.Equal(["tonesTagged", "voicesCast"], p.Extra!.Keys.Order().ToArray());
        Assert.Equal(["cloudVoicesAllowed", "narratorVoices", "voiceEngine"], p.Settings.Extra!.Keys.Order().ToArray());
        foreach (var l in p.Lines)
            Assert.All(l.Extra?.Keys.ToList() ?? [], k => Assert.Contains(k, new[] { "tone", "toneConfidence", "toneSource" }));
        foreach (var c in p.Characters) Assert.All(c.Extra?.Keys.ToList() ?? [], k => Assert.Equal("voices", k));
        foreach (var ch in p.Chapters)
        {
            Assert.Null(ch.Extra);
            Assert.All(ch.Paragraphs, para => Assert.Null(para.Extra));
        }
        Assert.All(p.Retakes, r => Assert.Null(r.Extra));
        Assert.All(p.Pronunciations, r => Assert.Null(r.Extra));
        Assert.All(p.Narrators, r => Assert.Null(r.Extra));
        Assert.All(p.Revisions, r => Assert.Null(r.Extra));
        Assert.All(p.Characters.SelectMany(c => c.Facts), f => Assert.Null(f.Extra));
    }

    [Fact]
    public void SaveWritesTheSameJson()
    {
        var c = MacContents();
        var p = ProjectJson.Decode(c.ProjectJson);
        var again = ProjectJson.Encode(p);
        var a = Normalize(JsonNode.Parse(c.ProjectJson)!);
        var b = Normalize(JsonNode.Parse(again)!);
        Assert.True(JsonNode.DeepEquals(a, b), Diff(a, b));
    }

    [Fact]
    public void CopyIsDeepAndEqual()
    {
        var p = ProjectJson.Decode(MacContents().ProjectJson);
        var copy = p.Copy();
        copy.Lines[0].SpeakerID = null;
        copy.Characters[0].Facts.Clear();
        copy.Chapters[0].Paragraphs[0].Text = "changed";
        Assert.NotNull(p.Lines[0].SpeakerID ?? p.Lines.First(l => l.SpeakerID != null).SpeakerID);
        Assert.NotEqual("changed", p.Chapters[0].Paragraphs[0].Text);
        var fresh = p.Copy();
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(ProjectJson.Encode(p)), JsonNode.Parse(ProjectJson.Encode(fresh))));
    }

    [Fact]
    public void ArchiveKeepsEveryEntry()
    {
        var c = MacContents();
        var p = ProjectJson.Decode(c.ProjectJson);
        var data = ProjectArchive.Write(new ProjectArchive.Contents { ProjectJson = ProjectJson.Encode(p), Original = c.Original, Audio = c.Audio });
        var back = ProjectArchive.Read(data);
        Assert.Equal(c.Original!.Value.Data, back.Original!.Value.Data);
        Assert.Equal(c.Audio["Kerensa.m4a"], back.Audio["Kerensa.m4a"]);
        if (Fixtures.ShouldWrite) File.WriteAllBytes(Fixtures.PathOf("sample-windows.innervoice"), data);
    }

    [Fact]
    public void UppercaseUuidsAndAppleDates()
    {
        var p = new Project { Title = "T", ImportedAt = new AppleDate(1.5) };
        var json = JsonNode.Parse(ProjectJson.Encode(p))!;
        Assert.Equal(1.5, json["importedAt"]!.GetValue<double>());
        var line = new Line(Guid.NewGuid(), new TextSpan(1, 2));
        p.Lines.Add(line);
        var text = System.Text.Encoding.UTF8.GetString(ProjectJson.Encode(p));
        Assert.Contains(line.Id.ToString().ToUpperInvariant(), text);
        Assert.DoesNotContain(line.Id.ToString().ToLowerInvariant(), text);
        // Non-optional fields are always written; null optionals are left out.
        Assert.NotNull(json["settings"]!["showInitials"]);
        Assert.NotNull(json["settings"]!["colorNarration"]);
        Assert.Null(json["settings"]!["purpose"]);
        Assert.Null(json["summariesBuilt"]);
        Assert.NotNull(json["format"]);
    }

    /// Sets (recorded, reviewed) have no order.
    static JsonNode Normalize(JsonNode n)
    {
        switch (n)
        {
            case JsonObject o:
                var obj = new JsonObject();
                foreach (var (k, v) in o.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    var child = v is null ? null : Normalize(v);
                    if (k is "recorded" or "reviewed" && child is JsonArray arr)
                        child = new JsonArray(arr.Select(x => x!.GetValue<string>()).Order().Select(s => (JsonNode)JsonValue.Create(s)).ToArray());
                    obj[k] = child;
                }
                return obj;
            case JsonArray a:
                return new JsonArray(a.Select(x => x is null ? null : Normalize(x)).ToArray());
            default:
                return n.DeepClone();
        }
    }

    static string Diff(JsonNode a, JsonNode b)
    {
        var sa = a.ToJsonString();
        var sb = b.ToJsonString();
        int i = 0;
        while (i < sa.Length && i < sb.Length && sa[i] == sb[i]) i++;
        return $"First difference at {i}:\nMac:     {Snip(sa, i)}\nWindows: {Snip(sb, i)}";
    }

    static string Snip(string s, int i) => s.Substring(Math.Max(0, i - 120), Math.Min(240, s.Length - Math.Max(0, i - 120)));
}
