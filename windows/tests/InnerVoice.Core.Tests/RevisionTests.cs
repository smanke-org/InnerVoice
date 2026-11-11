using System.Text;
using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// The C# RevisionMerger merges a revised manuscript exactly like the Mac's Kit
/// (windows/Fixtures/revision.json, written by WindowsFixtures.writeRevisionFixture).
public class RevisionTests
{
    static readonly JsonNode Golden = Fixtures.Json("revision.json");

    static Project OldProject() => ProjectJson.Decode(Encoding.UTF8.GetBytes(Golden["oldProject"]!.GetValue<string>()));

    static ImportedBook RevisedBook()
    {
        var blocks = PlainTextReader.Blocks(Golden["revisedMarkdown"]!.GetValue<string>(), markdown: true);
        return new ImportedBook("Sample Story", "A. N. Example", ChapterDetector.Chapters(blocks, "Sample Story"));
    }

    static (Project Old, RevisionMerger.Result Result) Run()
    {
        var old = OldProject();
        return (old, RevisionMerger.Merge(old, RevisedBook(), "Sample Story v2.md"));
    }

    [Fact]
    public void RecordMatches()
    {
        var (_, r) = Run();
        var index = new BookIndex(r.Project);
        var g = Golden["record"]!;
        Assert.Equal(g["fileName"]!.GetValue<string>(), r.Record.FileName);
        Assert.Equal(Ints(g["added"]!), r.Record.Added.Select(id => index.Positions[id].Global).ToList());
        Assert.Equal(Ints(g["changed"]!), r.Record.Changed.Select(id => index.Positions[id].Global).ToList());
        Assert.Equal(g["removed"]!.AsArray().Select(x => (x!["text"]!.GetValue<string>(), x["chapterTitle"]!.GetValue<string>())),
            r.Record.Removed.Select(x => (x.Text, x.ChapterTitle)));
        Assert.Equal(g["unchanged"]!.GetValue<int>(), r.Record.Unchanged);
        Assert.Equal(Golden["revisionCount"]!.GetValue<int>(), r.Project.Revisions.Count);
        Assert.Equal(Golden["sourceFileName"]!.GetValue<string>(), r.Project.SourceFileName);
    }

    [Fact]
    public void ChaptersAndParagraphsKeepIdentity()
    {
        var (old, r) = Run();
        var oldIndex = new BookIndex(old);
        var gc = Golden["chapters"]!.AsArray();
        Assert.Equal(gc.Count, r.Project.Chapters.Count);
        for (int c = 0; c < gc.Count; c++)
        {
            Assert.Equal(gc[c]!["title"]!.GetValue<string>(), r.Project.Chapters[c].Title);
            int? oldChapter = old.Chapters.FindIndex(x => x.Id == r.Project.Chapters[c].Id) is var i && i >= 0 ? i : null;
            Assert.Equal(OptInt(gc[c]!["oldChapter"]), oldChapter);
        }
        var gp = Golden["paragraphs"]!.AsArray();
        var paras = r.Project.Chapters.SelectMany(c => c.Paragraphs).ToList();
        Assert.Equal(gp.Count, paras.Count);
        for (int i = 0; i < gp.Count; i++)
        {
            Assert.Equal(gp[i]!["text"]!.GetValue<string>(), paras[i].Text);
            int? oldGlobal = oldIndex.Positions.TryGetValue(paras[i].Id, out var pos) ? pos.Global : null;
            Assert.True(OptInt(gp[i]!["oldParagraph"]) == oldGlobal, $"paragraph {i}: expected old {OptInt(gp[i]!["oldParagraph"])}, got {oldGlobal}");
        }
    }

    [Fact]
    public void LinesAndSpeakersMatch()
    {
        var (old, r) = Run();
        var p = r.Project;
        var index = new BookIndex(p);
        var oldLines = new Dictionary<Guid, int>();
        for (int i = 0; i < old.Lines.Count; i++) oldLines.TryAdd(old.Lines[i].Id, i);
        var g = Golden["lines"]!.AsArray();
        Assert.Equal(g.Count, p.Lines.Count);
        for (int i = 0; i < g.Count; i++)
        {
            var l = p.Lines[i];
            var e = g[i]!;
            string where = $"line {i} “{p.TextOf(l, index)}”";
            Assert.True(e["paragraph"]!.GetValue<int>() == index.Positions[l.ParagraphID].Global, where);
            Assert.True(Span(e["range"]!) == l.Range, where);
            Assert.True(e["kind"]!.GetValue<string>() == Camel(l.Kind.ToString()), where);
            Assert.True(e["continuesPrevious"]!.GetValue<bool>() == l.ContinuesPrevious, where);
            Assert.True(e["speaker"]?.GetValue<string>() == p.Character(l.SpeakerID)?.Name, $"{where}: expected {e["speaker"]}, got {p.Character(l.SpeakerID)?.Name}");
            Assert.True(e["source"]!.GetValue<string>() == Camel(l.Source.ToString()), $"{where}: source {e["source"]} vs {l.Source}");
            Assert.True(Math.Abs(e["confidence"]!.GetValue<double>() - l.Confidence) < 1e-9, where);
            int? oldLine = oldLines.TryGetValue(l.Id, out var ol) ? ol : null;
            Assert.True(OptInt(e["oldLine"]) == oldLine, $"{where}: old line {OptInt(e["oldLine"])} vs {oldLine}");
        }
        Assert.Equal(Golden["characters"]!.AsArray().Select(x => x!.GetValue<string>()), p.Characters.Select(c => c.Name));
    }

    [Fact]
    public void MarksFactsAndPronunciations()
    {
        var (_, r) = Run();
        var p = r.Project;
        var index = new BookIndex(p);
        int? Pos(Guid? id) => id is { } g && index.Positions.TryGetValue(g, out var pos) ? pos.Global : null;
        Assert.Equal(Ints(Golden["recorded"]!), p.Recorded.Select(id => index.Positions[id].Global).Order().ToList());
        Assert.Equal(Ints(Golden["retakes"]!), p.Retakes.Select(x => index.Positions[x.ParagraphID].Global).ToList());
        Assert.Equal(Golden["facts"]!.AsArray().Select(f => (f!["character"]!.GetValue<string>(), f["text"]!.GetValue<string>(), OptInt(f["sourceParagraph"]))),
            p.Characters.SelectMany(c => c.Facts.Select(f => (c.Name, f.Text, Pos(f.SourceParagraphID)))));
        Assert.Equal(Golden["pronunciations"]!.AsArray().Select(x => (x!["term"]!.GetValue<string>(), OptInt(x["firstParagraph"]))),
            p.Pronunciations.Select(x => (x.Term, Pos(x.FirstParagraphID))));
    }

    [Fact]
    public void DiffMatchesCollectionDifference()
    {
        var cases = Golden["diffCases"]!.AsArray();
        Assert.NotEmpty(cases);
        foreach (var c in cases)
        {
            var o = c!["old"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            var n = c["new"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
            var (removed, inserted) = RevisionMerger.Diff(o, n);
            string where = $"[{string.Join("", o)}] → [{string.Join("", n)}]";
            Assert.True(Ints(c["removed"]!).SequenceEqual(removed), $"{where}: removed {string.Join(",", removed)}");
            Assert.True(Ints(c["inserted"]!).SequenceEqual(inserted), $"{where}: inserted {string.Join(",", inserted)}");
        }
    }

    [Fact]
    public void DiffOfLargeBookIsMinimal()
    {
        // Thousands of paragraphs with a few edits (one insert, one removal, one edit): the
        // script is the edit distance, two removals and two insertions.
        var o = Enumerable.Range(0, 5000).Select(i => $"p{i}").ToList();
        var n = o.ToList();
        n.RemoveAt(4000);
        n.Insert(100, "new");
        n[2500] = "edited";
        var (removed, inserted) = RevisionMerger.Diff(o, n);
        Assert.Equal(2, removed.Count);
        Assert.Equal(2, inserted.Count);
    }

    [Fact]
    public void OldProjectIsUnchanged()
    {
        var old = OldProject();
        var before = ProjectJson.Encode(old);
        var r = RevisionMerger.Merge(old, RevisedBook(), "Sample Story v2.md");
        Assert.Equal(Encoding.UTF8.GetString(before), Encoding.UTF8.GetString(ProjectJson.Encode(old)));
        Assert.NotSame(old, r.Project);
        Assert.Equal(old.Revisions.Count + 1, r.Project.Revisions.Count);
    }

    static string Camel(string s) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(s);
    static TextSpan Span(JsonNode n) => new(n["location"]!.GetValue<int>(), n["length"]!.GetValue<int>());
    static List<int> Ints(JsonNode n) => n.AsArray().Select(x => x!.GetValue<int>()).ToList();
    static int? OptInt(JsonNode? n) => n?.GetValue<int>();
}
