using InnerVoice.Core.Import;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace InnerVoice.Core.Tests;

/// Ports of the Kit's pdfLetterSpacedLineStaysInItsParagraph and closesLetterSpacing.
public class PdfLetterSpacingTests
{
    [Fact]
    public void PdfLetterSpacedLineStaysInItsParagraph()
    {
        var builder = new PdfDocumentBuilder();
        var page = builder.AddPage(PageSize.Letter);
        var font = builder.AddStandard14Font(Standard14Font.TimesRoman);
        const string filler = "Tracy didn't bother to examine whatever was in the yellow box and she placed it in the trunk.";
        (string Text, double Kern, double X)[] rows =
        [
            (filler, 0, 90), (filler, 0, 72), (filler, 0, 72),
            ("Tracy didn't bother to examine it; there was something in her face. She seemed anxious or", 0, 72),
            ("concerned. Whatever her mood, she had been", 4.5, 72),
            ("uncharacteristically deferent to the box's contents.", 0, 72),
            (filler, 0, 90), (filler, 0, 72),
        ];
        double y = 720;
        foreach (var (text, kern, x) in rows)
        {
            if (kern == 0) page.AddText(text, 12, new PdfPoint(x, y), font);
            else
            {
                // Each letter placed on its own, far apart, the way justifying layout programs
                // write a squeezed-out line.
                double pen = x;
                foreach (var ch in text)
                {
                    var s = ch.ToString();
                    IReadOnlyList<Letter> measured = page.MeasureText(s, 12, new PdfPoint(pen, y), font);
                    double advance = measured.Count > 0 ? measured[^1].EndBaseLine.X - pen : 3;
                    if (ch != ' ') page.AddText(s, 12, new PdfPoint(pen, y), font);
                    pen += advance + (ch == ' ' ? kern * 2 : kern);
                }
            }
            y -= 15;
        }
        var path = Path.Combine(Path.GetTempPath(), $"spaced-{Guid.NewGuid():N}.pdf");
        File.WriteAllBytes(path, builder.Build());
        try
        {
            var blocks = PdfReader.Read(path);
            var joined = string.Join("\n", blocks.Select(b => b.Text));
            Assert.True(joined.Contains("anxious or concerned. Whatever her mood, she had been uncharacteristically deferent"), joined);
            Assert.Equal(2, blocks.Count);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ClosesLetterSpacing()
    {
        Assert.Equal("or concerned.", PdfReader.CloseLetterSpacing("or c o n c e r n e d ."));
        Assert.Equal("been uncharacteristically", PdfReader.CloseLetterSpacing("b e e n uncharacteristically"));
        Assert.Equal("I said a word", PdfReader.CloseLetterSpacing("I said a word"));
        Assert.Equal("A. B. Smith", PdfReader.CloseLetterSpacing("A. B. Smith"));
    }

    [Fact]
    public void PiecesOnOneBaselineJoinIntoOneLine()
    {
        var lines = new List<PdfReader.PdfLine>
        {
            new("She seemed anxious or", new(72, 600, 200, 12), 0, 12, [], false),
            new("c o n c e r n e d .", new(280, 600.5, 80, 12), 0, 12, [new(0, 1)], false),
            new("Next line", new(72, 585, 100, 12), 0, 12, [], false),
        };
        var joined = PdfReader.JoinSplitLines(lines);
        Assert.Equal(["She seemed anxious or concerned.", "Next line"], joined.Select(l => l.Text));
        Assert.Equal(72 + 288, joined[0].Rect.MaxX, 3);
        Assert.Empty(joined[0].Italic);
    }
}
