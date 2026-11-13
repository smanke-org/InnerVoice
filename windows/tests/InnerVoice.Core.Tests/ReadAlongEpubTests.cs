using System.IO.Compression;
using System.Text;
using System.Xml;
using InnerVoice.Core.Import;

namespace InnerVoice.Core.Tests;

/// Ports of the Kit's read-along EPUB tests (EpubTests.swift): the page, the package and its
/// overlays, the clock and the identifier. No audio: a few fake bytes stand in for the M4A.
public class ReadAlongEpubTests
{
    static Project Sample()
    {
        var chapter = new Chapter("Chapter 1 & More",
        [
            new Paragraph("“Busy.” The doctor folded her gloves. “Your father wrote to Kerensa.”"),
            new Paragraph("She laid an envelope on the table. It was sealed.", italic: [new TextSpan(10, 4)]),
            new Paragraph("* * *", ParagraphStyle.SceneBreak),
        ]);
        return ProjectBuilder.Make(new ImportedBook("Placeholder <Book>", "A. Writer", [chapter]), "t.txt");
    }

    static bool WellFormed(string xml)
    {
        try
        {
            var doc = new XmlDocument { XmlResolver = null };
            doc.LoadXml(xml.Replace("<!DOCTYPE html>", ""));
            return true;
        }
        catch (XmlException) { return false; }
    }

    [Fact]
    public void SegmentsKnowWhereTheirWordsAre()
    {
        var p = Sample();
        var segs = ReadAlongEpub.Segments(p.Chapters[0], p);
        var texts = segs.Where(s => s.Source is not null)
            .Select(s => p.Chapters[0].Paragraphs.First(x => x.Id == s.ParagraphID).Substring(s.Source!.Value)).ToList();
        Assert.Equal(["Busy.", "The doctor folded her gloves.", "Your father wrote to Kerensa.", "She laid an envelope on the table.", "It was sealed."], texts);
        Assert.Contains(segs, s => s.Source is null); // the scene break
    }

    [Fact]
    public void ReadAlongPageWrapsEachSentence()
    {
        var p = Sample();
        var segs = ReadAlongEpub.Segments(p.Chapters[0], p);
        var page = ReadAlongEpub.MakePage(p.Chapters[0], 1, segs, "en");
        Assert.True(segs.Where(s => s.Source is not null).Select(s => s.Id).ToHashSet().IsSubsetOf(page.Fragments.Keys));
        Assert.Contains("<h1>Chapter 1 &amp; More</h1>", page.Xhtml);
        Assert.Contains("<span id=\"c1-1\">Busy.</span>", page.Xhtml);
        Assert.Contains("<i>", page.Xhtml);
        Assert.Contains("<p class=\"break\">* * *</p>", page.Xhtml);
        Assert.True(WellFormed(page.Xhtml));
    }

    [Fact]
    public void ReadAlongEpubPackageAndOverlays()
    {
        var p = Sample();
        var segs = ReadAlongEpub.Segments(p.Chapters[0], p);
        var page = ReadAlongEpub.MakePage(p.Chapters[0], 1, segs, "en");
        // Each segment a second long, back to back.
        var timing = segs.Select((s, i) => (s.Id, i)).ToDictionary(x => x.Id, x => ((double)x.i, x.i + 1.0));
        var clips = ReadAlongEpub.Clips(page.Fragments, timing, segs.Count);
        Assert.Equal(page.Fragments.Values.Distinct().Count(), clips.Count);
        Assert.Equal(clips.Select(c => c.Begin).Order(), clips.Select(c => c.Begin));

        var dir = Path.Combine(Path.GetTempPath(), $"iv-readalong-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var audio = Path.Combine(dir, "a.m4a");
            File.WriteAllBytes(audio, Enumerable.Repeat((byte)7, 5000).ToArray());
            var book = new ReadAlongEpub.Book(p.Title, p.Author, "en", ReadAlongEpub.Identifier(p.Title, p.Author), DateTime.UtcNow,
            [
                new(p.Chapters[0].Title, page.Xhtml, audio, 61.5, clips),
                new("Silent", page.Xhtml, null, 0, []),
            ]);
            var path = Path.Combine(dir, "book.epub");
            ReadAlongEpub.Write(book, path);
            var data = File.ReadAllBytes(path);
            // mimetype first, stored, no extra field.
            Assert.Equal(new byte[] { 0x50, 0x4B, 0x03, 0x04 }, data[..4]);
            Assert.True(data[8] == 0 && data[9] == 0, "mimetype is stored");
            Assert.True(data[28] == 0 && data[29] == 0, "no extra field");
            Assert.Equal("mimetype", Encoding.ASCII.GetString(data, 30, 8));
            Assert.Equal("application/epub+zip", Encoding.ASCII.GetString(data, 38, 20));

            using var zip = ZipFile.OpenRead(path);
            string Read(string name)
            {
                using var r = new StreamReader(zip.GetEntry(name)!.Open());
                return r.ReadToEnd();
            }
            var audioEntry = zip.GetEntry("OEBPS/audio/c001.m4a")!;
            using (var ms = new MemoryStream())
            {
                audioEntry.Open().CopyTo(ms);
                Assert.Equal(Enumerable.Repeat((byte)7, 5000), ms.ToArray());
            }
            Assert.Equal(audioEntry.Length, audioEntry.CompressedLength); // audio stored
            Assert.Null(zip.GetEntry("OEBPS/audio/c002.m4a"));
            var opf = Read("OEBPS/content.opf");
            Assert.Contains("media-overlay=\"mo-c001\"", opf);
            Assert.DoesNotContain("media-overlay=\"mo-c002\"", opf);
            Assert.Contains("<meta property=\"media:duration\" refines=\"#mo-c001\">0:01:01.500</meta>", opf);
            Assert.Contains("<meta property=\"media:duration\">0:01:01.500</meta>", opf);
            Assert.Contains("media:active-class", opf);
            Assert.Contains("Placeholder &lt;Book&gt;", opf);
            Assert.Contains("properties=\"nav\"", opf);
            var smil = Read("OEBPS/smil/c001.smil");
            Assert.Equal(clips.Count, smil.Split("<par ").Length - 1);
            Assert.Contains("<text src=\"../text/c001.xhtml#c1-1\"/><audio src=\"../audio/c001.m4a\" clipBegin=\"0:00:00.000\"", smil);
            foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xhtml") || e.FullName.EndsWith(".opf") || e.FullName.EndsWith(".smil") || e.FullName.EndsWith(".xml")))
                Assert.True(WellFormed(Read(entry.FullName)), $"{entry.FullName} is well-formed");
            zip.Dispose();

            // Reading it back: the nav gives the chapters.
            var back = ManuscriptImporter.Read(path);
            Assert.Equal(["Chapter 1 & More", "Silent"], back.Chapters.Select(c => c.Title));
            Assert.Equal(p.Chapters[0].Paragraphs[0].Text, back.Chapters[0].Paragraphs.First().Text);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ReadAlongClockAndIdentifier()
    {
        Assert.Equal("0:00:00.000", ReadAlongEpub.Clock(0));
        Assert.Equal("1:02:05.046", ReadAlongEpub.Clock(3725.0456));
        Assert.Equal(ReadAlongEpub.Identifier("A", "B"), ReadAlongEpub.Identifier("A", "B"));
        Assert.NotEqual(ReadAlongEpub.Identifier("A", "B"), ReadAlongEpub.Identifier("A", "C"));
        Assert.Matches("^urn:uuid:[0-9a-f]{8}-[0-9a-f]{4}-3[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$", ReadAlongEpub.Identifier("A", "B"));
        Assert.Equal("en", ReadAlongEpub.LanguageOf(Sample()));
    }
}
