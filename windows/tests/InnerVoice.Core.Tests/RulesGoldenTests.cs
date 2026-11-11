using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// The C# port finds the same blocks, chapters, lines and speakers as the Mac's Kit.
public class RulesGoldenTests
{
    static readonly JsonNode Golden = Fixtures.Json("sample-rules.json");

    [Fact]
    public void Blocks()
    {
        var blocks = PlainTextReader.Blocks(Fixtures.SampleMarkdown, markdown: true);
        var g = Golden["blocks"]!.AsArray();
        Assert.Equal(g.Count, blocks.Count);
        for (int i = 0; i < blocks.Count; i++)
        {
            Assert.Equal(g[i]!["text"]!.GetValue<string>(), blocks[i].Text);
            Assert.Equal(g[i]!["isBlockQuote"]!.GetValue<bool>(), blocks[i].IsBlockQuote);
            Assert.Equal(g[i]!["headingLevel"]?.GetValue<int>(), blocks[i].HeadingLevel);
            Assert.Equal(Spans(g[i]!["italic"]!), blocks[i].Italic);
        }
    }

    [Fact]
    public void ChaptersAndParagraphs()
    {
        var p = Fixtures.SampleProject();
        var g = Golden["chapters"]!.AsArray();
        Assert.Equal(g.Count, p.Chapters.Count);
        for (int c = 0; c < g.Count; c++)
        {
            Assert.Equal(g[c]!["title"]!.GetValue<string>(), p.Chapters[c].Title);
            var gp = g[c]!["paragraphs"]!.AsArray();
            Assert.Equal(gp.Count, p.Chapters[c].Paragraphs.Count);
            for (int i = 0; i < gp.Count; i++)
            {
                var para = p.Chapters[c].Paragraphs[i];
                Assert.Equal(gp[i]!["text"]!.GetValue<string>(), para.Text);
                Assert.Equal(gp[i]!["style"]!.GetValue<string>(), JsonNamingCamel(para.Style.ToString()));
                Assert.Equal(Spans(gp[i]!["italic"]!), para.Italic);
            }
        }
    }

    [Fact]
    public void LinesAndSpeakers()
    {
        var p = Fixtures.SampleProject();
        var index = new BookIndex(p);
        var g = Golden["lines"]!.AsArray();
        Assert.Equal(g.Count, p.Lines.Count);
        for (int i = 0; i < g.Count; i++)
        {
            var l = p.Lines[i];
            var e = g[i]!;
            string where = $"line {i} “{p.TextOf(l, index)}”";
            Assert.True(e["paragraph"]!.GetValue<int>() == index.Positions[l.ParagraphID].Global, where);
            Assert.True(Span(e["range"]!) == l.Range, where);
            Assert.True(e["kind"]!.GetValue<string>() == JsonNamingCamel(l.Kind.ToString()), where);
            Assert.True(e["continuesPrevious"]!.GetValue<bool>() == l.ContinuesPrevious, where);
            Assert.True(e["speaker"]?.GetValue<string>() == p.Character(l.SpeakerID)?.Name, $"{where}: expected {e["speaker"]}, got {p.Character(l.SpeakerID)?.Name}");
            Assert.True(e["source"]!.GetValue<string>() == JsonNamingCamel(l.Source.ToString()), $"{where}: source {e["source"]} vs {l.Source}");
            Assert.True(Math.Abs(e["confidence"]!.GetValue<double>() - l.Confidence) < 1e-9, where);
        }
    }

    [Fact]
    public void CharactersAndColors()
    {
        var p = Fixtures.SampleProject();
        var g = Golden["characters"]!.AsArray();
        Assert.Equal(g.Select(x => x!["name"]!.GetValue<string>()), p.Characters.Select(c => c.Name));
        Assert.Equal(g.Select(x => x!["colorHex"]!.GetValue<string>()), p.Characters.Select(c => c.ColorHex));
    }

    [Fact]
    public void QuoteStyle()
    {
        var p = Fixtures.SampleProject();
        Assert.Equal(Golden["quoteStyle"]!.GetValue<string>(),
            JsonNamingCamel(DialogueFinder.DetectStyle(p.Chapters.SelectMany(c => c.Paragraphs)).ToString()));
    }

    static string JsonNamingCamel(string s) => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(s);
    static TextSpan Span(JsonNode n) => new(n["location"]!.GetValue<int>(), n["length"]!.GetValue<int>());
    static List<TextSpan> Spans(JsonNode n) => n.AsArray().Select(x => Span(x!)).ToList();
}
