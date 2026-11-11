using System.Text.Json.Nodes;
using InnerVoice.Core;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// The C# readers import the sample story in every format the way the Mac's Kit does.
public class ImporterTests
{
    public static TheoryData<string> Formats => new() { "docx", "odt", "epub", "html", "rtf", "pdf" };

    [Theory]
    [MemberData(nameof(Formats))]
    public void MatchesTheKit(string format)
    {
        var path = Fixtures.PathOf($"import/sample.{format}");
        var expected = Fixtures.Json($"import/sample.{format}.expected.json");
        var book = ManuscriptImporter.Read(path);
        Assert.Equal(expected["title"]!.GetValue<string>(), book.Title);
        Assert.Equal(expected["author"]!.GetValue<string>(), book.Author);
        var chapters = expected["chapters"]!.AsArray();
        Assert.Equal(chapters.Select(c => c!["title"]!.GetValue<string>()), book.Chapters.Select(c => c.Title));
        for (int c = 0; c < chapters.Count; c++)
        {
            var want = chapters[c]!["paragraphs"]!.AsArray().Select(p => p!["text"]!.GetValue<string>()).ToList();
            var got = book.Chapters[c].Paragraphs.Select(p => p.Text).ToList();
            Assert.True(want.SequenceEqual(got), $"{format} chapter {c + 1}:\n" + Diff(want, got));
            for (int i = 0; i < want.Count; i++)
            {
                var wp = chapters[c]!["paragraphs"]![i]!;
                // The Mac's HTML import (WebKit) misses block quotes; this one finds them.
                if (format == "html" && wp["style"]!.GetValue<string>() == "body" && book.Chapters[c].Paragraphs[i].Style == ParagraphStyle.BlockQuote) continue;
                Assert.True(wp["style"]!.GetValue<string>() == System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(book.Chapters[c].Paragraphs[i].Style.ToString()),
                    $"{format} ch {c + 1} ¶{i + 1} style {wp["style"]} vs {book.Chapters[c].Paragraphs[i].Style}: {want[i]}");
                var italics = wp["italic"]!.AsArray().Select(x => new TextSpan(x!["location"]!.GetValue<int>(), x["length"]!.GetValue<int>())).ToList();
                Assert.True(italics.SequenceEqual(book.Chapters[c].Paragraphs[i].Italic),
                    $"{format} ch {c + 1} ¶{i + 1} italic [{string.Join(",", italics)}] vs [{string.Join(",", book.Chapters[c].Paragraphs[i].Italic)}]");
            }
        }
    }

    static string Diff(List<string> want, List<string> got)
    {
        for (int i = 0; i < Math.Max(want.Count, got.Count); i++)
        {
            var w = i < want.Count ? want[i] : "(none)";
            var g = i < got.Count ? got[i] : "(none)";
            if (w != g) return $"¶{i + 1}\n  Mac:     {w}\n  Windows: {g}";
        }
        return "";
    }

    /// A printed book's PDF has mirrored margins; an indent on the narrow-margin pages must
    /// still start a paragraph (the Mac's pdfIndentsOnMirroredMargins).
    [Fact]
    public void PdfIndentsOnMirroredMargins()
    {
        var lines = new List<PdfReader.PdfLine>();
        int n = 0;
        for (int page = 0; page < 6; page++)
        {
            double left = page % 2 == 0 ? 63 : 54, right = left + 315;
            for (int row = 0; row < 24; row++)
            {
                bool indented = row % 3 == 0;
                double x = indented ? left + 11.3 : left;
                lines.Add(new(indented ? $"P{n} starts" : "and goes on", new(x, 600 - row * 14, right - x, 12), page, 11, [], false));
                if (indented) n++;
            }
        }
        var blocks = PdfReader.Paragraphs(lines);
        Assert.Equal(n, blocks.Count);
        Assert.All(blocks, b => Assert.True(b.Text.StartsWith('P') && b.Text.Split("starts").Length == 2));
    }

    [Fact]
    public void UnsupportedFilesSayWhatToDo()
    {
        var e = Assert.Throws<ImportException>(() => ManuscriptImporter.Read("/tmp/book.xyz"));
        Assert.Contains("Save it as Word", e.Message);
    }
}
