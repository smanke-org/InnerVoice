using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using InnerVoice.App.Document;
using InnerVoice.App.Export;
using InnerVoice.Core;
using UglyToad.PdfPig;
using Xunit;

namespace InnerVoice.App.Tests;

/// Every export of the sample book, in every format, opens in the tools people use.
public class ExportTests
{
    static string Squash(string s) => new(s.Where(c => !char.IsWhiteSpace(c)).ToArray());

    static string PdfText(string path)
    {
        using var pdf = PdfDocument.Open(path);
        return Squash(string.Concat(pdf.GetPages().Select(p => p.Text)));
    }

    [AvaloniaFact]
    public void WritesEveryKindInEveryFormat()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara")).Id;
        var tom = doc.Project.Characters.First(c => c.Name.StartsWith("Tom")).Id;
        var maraName = doc.Project.Character(mara)!.Name.ToUpperInvariant();
        var tomName = doc.Project.Character(tom)!.Name.ToUpperInvariant();
        var reader = new Narrator { Name = "Ann Reader" };
        doc.Change("Add Narrator", p =>
        {
            p.Narrators.Add(reader);
            p.Characters.First(c => c.Id == mara).NarratorID = reader.Id;
        });
        Assert.Contains(doc.Project.Lines, l => l.SpeakerID == tom);
        var maraLine = doc.Project.TextOf(doc.Project.Lines.First(l => l.SpeakerID == mara), doc.Index);
        var title = doc.Project.Title;

        // INNERVOICE_EXPORT_DIR keeps the files to look at.
        var keep = Environment.GetEnvironmentVariable("INNERVOICE_EXPORT_DIR");
        var dir = keep ?? Path.Combine(Path.GetTempPath(), $"iv-export-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var kind in Exporter.Kinds)
            {
                foreach (var format in Exporter.Formats(kind))
                {
                    var options = new Exporter.Options { Kind = kind, Format = format };
                    var path = Path.Combine(dir, $"{ExportWindow.SuggestedName(doc, options)}.{format.Extension()}");
                    Exporter.Write(options, doc, path);
                    Assert.True(new FileInfo(path).Length > 0, path);
                    switch (format)
                    {
                        case Exporter.Format.Pdf:
                        {
                            var text = PdfText(path);
                            Assert.Contains(Squash(title), text);
                            // The tagged manuscript has only the book's title, like the Mac's.
                            if (kind != Exporter.Kind.Manuscript) Assert.Contains(Squash(kind.Title()), text);
                            else Assert.Contains("Characterkey", text);
                            if (kind is Exporter.Kind.NarratorScript or Exporter.Kind.Manuscript)
                                Assert.Contains(Squash(maraLine), text);
                            break;
                        }
                        case Exporter.Format.Word:
                        {
                            using var word = WordprocessingDocument.Open(path, false);
                            var errors = new OpenXmlValidator().Validate(word).ToList();
                            Assert.True(errors.Count == 0, string.Join("\n", errors.Select(e => $"{e.Path?.XPath}: {e.Description}")));
                            var body = word.MainDocumentPart!.Document!.Body!.InnerText;
                            Assert.Contains(title, body);
                            Assert.Contains(kind == Exporter.Kind.Manuscript ? "Character key" : kind.Title(), body);
                            break;
                        }
                        case Exporter.Format.Csv:
                        {
                            var bytes = File.ReadAllBytes(path);
                            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
                            var header = Encoding.UTF8.GetString(bytes[3..]).Split('\n')[0];
                            Assert.Equal(kind == Exporter.Kind.Pronunciations ? "Term,Say It As,Notes,Confirmed,Count"
                                : "Name,Also Called,Narrator,Lines,Words,First Chapter,Age,Gender,Accent & Origin,Voice,Personality,Appearance,History,Relationships,Other,Voice Notes,Notes",
                                header);
                            break;
                        }
                    }
                }
            }

            // One narrator's script holds only their characters' lines.
            var mine = new Exporter.Options { Kind = Exporter.Kind.NarratorScript, NarratorID = reader.Id };
            var path1 = Path.Combine(dir, "narrator.pdf");
            Exporter.Write(mine, doc, path1);
            var script = PdfText(path1);
            Assert.Contains(Squash("Narrator Script · Ann Reader"), script);
            Assert.Contains(Squash(maraName), script);
            Assert.DoesNotContain(Squash(tomName), script);
            Assert.Contains(Squash(maraLine), script);
            // A chapter's recap ("Previously…") follows its title in the script.
            var maraChapter = doc.Index.Positions[doc.Project.Lines.First(l => l.SpeakerID == mara).ParagraphID].Chapter;
            doc.Change("Edit Recap", p => InnerVoice.Core.AI.RecapPrompt.Edit("Mara waited for the boat.", p.Chapters[maraChapter].Id, p));
            var path2 = Path.Combine(dir, "narrator-recap.pdf");
            Exporter.Write(mine, doc, path2);
            Assert.Contains(Squash("Previously… Mara waited for the boat."), PdfText(path2));
            doc.Undo();
            static List<string> Heads(List<Exporter.Para> paras) =>
                paras.Where(p => p.Runs.Count == 1 && p.Runs[0].Size == 11 && p.Runs[0].Weight == Exporter.Weight.SemiBold)
                     .Select(p => p.PlainText).ToList();
            var heads = Heads(Exporter.Build(mine, doc));
            Assert.NotEmpty(heads);
            Assert.All(heads, h => Assert.Contains($"  {maraName}", h));
            var everyone = Heads(Exporter.Build(new Exporter.Options { Kind = Exporter.Kind.NarratorScript }, doc));
            Assert.Contains(everyone, h => h.Contains($"  {tomName}"));
            Assert.True(everyone.Count > heads.Count);

            // The tagged manuscript highlights lines at 28% and marks unsure ones red.
            var manuscript = Exporter.Build(new Exporter.Options { Kind = Exporter.Kind.Manuscript }, doc);
            var highlighted = manuscript.SelectMany(p => p.Runs).Where(r => r.Background is not null && r.Weight == Exporter.Weight.Regular).ToList(); // the key's chips are 30%
            Assert.NotEmpty(highlighted);
            Assert.All(highlighted, r => Assert.Equal(0.28, r.Background!.Value.A, 3));
            Assert.Contains(manuscript, p => p.Runs.Count > 0 && p.Runs[0].Text.StartsWith('[') && p.Runs[0].Text.EndsWith("] "));
            if (doc.Project.Lines.Any(doc.Project.NeedsYou))
                Assert.Contains(highlighted, r => r.Background!.Value.RRGGBB == "EF4444");
        }
        finally { if (keep is null) Directory.Delete(dir, true); }

        // The window builds for every kind.
        var owner = new Window();
        owner.Show();
        var window = new ExportWindow(doc);
        window.Show(owner);
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        window.Close();
        owner.Close();
    }

    /// The pronunciation guide, as the Mac's: terms A to Z with how to say them, confirmed ones
    /// marked, notes under them, dismissed ones left out; the CSV in the book's order.
    [AvaloniaFact]
    public void WritesThePronunciationGuide()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        doc.Change("Add Terms", p =>
        {
            p.Pronunciations.Clear();
            p.Pronunciations.Add(new Pronunciation { Term = "Tresco", SayItAs = "TRESS-koh", Notes = "like the island", Confirmed = true, Occurrences = 3 });
            p.Pronunciations.Add(new Pronunciation { Term = "aldous", Notes = "ask the author" });
            p.Pronunciations.Add(new Pronunciation { Term = "Zennor", SayItAs = "ZEN-er", Dismissed = true });
        });
        var guide = Exporter.Build(new Exporter.Options { Kind = Exporter.Kind.Pronunciations }, doc).Select(p => p.PlainText).ToList();
        Assert.Equal([doc.Project.Title, "Pronunciation Guide", "aldous  —", "ask the author", "Tresco  TRESS-koh  ✓ confirmed", "like the island"], guide);

        var csv = Exporter.Csv(new Exporter.Options { Kind = Exporter.Kind.Pronunciations, Format = Exporter.Format.Csv }, doc);
        Assert.Equal("Term,Say It As,Notes,Confirmed,Count\n\"Tresco\",\"TRESS-koh\",\"like the island\",yes,3\n\"aldous\",\"\",\"ask the author\",no,0", csv);

        // File › Export… offers it to books with AI voices, Narrator Scripts to narration
        // (a project not asked yet shows both).
        doc.Change("Project Type", p => p.Settings.Purpose = null);
        Assert.Equal(Exporter.Kinds, Exporter.KindsShown(doc.Project));
        doc.Change("Project Type", p => p.Settings.Purpose = ProjectPurpose.Narration);
        Assert.DoesNotContain(Exporter.Kind.Pronunciations, Exporter.KindsShown(doc.Project));
        Assert.Contains(Exporter.Kind.NarratorScript, Exporter.KindsShown(doc.Project));
        doc.Change("Project Type", p => p.Settings.Purpose = ProjectPurpose.AiVoices);
        Assert.Contains(Exporter.Kind.Pronunciations, Exporter.KindsShown(doc.Project));
        Assert.DoesNotContain(Exporter.Kind.NarratorScript, Exporter.KindsShown(doc.Project));
    }
}
