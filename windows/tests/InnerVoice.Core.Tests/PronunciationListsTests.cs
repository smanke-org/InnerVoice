using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InnerVoice.Core.Tests;

/// This Book's and This PC's pronunciations (PronunciationLists, PronunciationFile): ports of
/// the Mac's Tests/InnerVoiceKitTests/PronunciationListsTests.swift, then the files and books
/// the two apps hand each other.
public class PronunciationListsTests
{
    static Project Book(params (string Term, string Say)[] own) => new()
    {
        Title = "T",
        Pronunciations = own.Select(o => new Pronunciation { Term = o.Term, SayItAs = o.Say, AddedByUser = true }).ToList(),
    };

    static PronunciationList Mac() => new("MAC",
    [
        new ListedPronunciation("Kerensa", "keh-REN-sa"),
        new ListedPronunciation("Siobhan", "shi-VAWN"),
    ]);

    static string? Say(List<SpeechScript.Saying> say, string term) => say.FirstOrDefault(s => s.Term == term)?.Say;

    [Fact]
    public void TheBookWinsAndTheComputerFillsIn()
    {
        var p = Book(("Kerensa", "KER-en-sa"));
        var say = PronunciationLists.Sayings(p, Mac());
        Assert.Equal("KER-en-sa", Say(say, "Kerensa"));
        Assert.Equal("shi-VAWN", Say(say, "Siobhan"));
        Assert.Equal(2, say.Count);
        Assert.Equal("shi-VAWN met KER-en-sa.", SpeechScript.Apply(say, "Siobhan met Kerensa."));
    }

    [Fact]
    public void ABookCanTurnTheComputerListOff()
    {
        var p = Book();
        p.Settings.UsesComputerPronunciations = false;
        Assert.Empty(PronunciationLists.Sayings(p, Mac()));
        Assert.True(p.Settings.ComputerPronunciationsOff);
        p.Settings.UsesComputerPronunciations = true;
        Assert.Null(p.Settings.ComputerPronunciationsOff);
    }

    [Fact]
    public void TheBookKeepsACopyThatPlaysOnAnotherComputer()
    {
        var p = Book();
        p.ComputerPronunciations = PronunciationLists.CopyForSaving(p, Mac());
        Assert.Equal(2, p.ComputerPronunciations!.Count);
        Assert.All(p.ComputerPronunciations, e => Assert.Equal("MAC", e.List));
        // Saved and read back.
        var read = ProjectJson.Decode(ProjectJson.Encode(p));
        // Another computer with its own list: its words win, the book's copy fills in.
        var pc = new PronunciationList("PC", [new ListedPronunciation("Siobhan", "sha-VAHN")]);
        var there = PronunciationLists.Sayings(read, pc);
        Assert.Equal("sha-VAHN", Say(there, "Siobhan"));
        Assert.Equal("keh-REN-sa", Say(there, "Kerensa"));
        // No list there at all: the copy is used as it is.
        Assert.Equal(2, PronunciationLists.Sayings(read, null).Count);
        // Saved there: the copy keeps the Mac's word (still from the Mac) and the PC's.
        var again = PronunciationLists.CopyForSaving(read, pc) ?? [];
        Assert.Equal("MAC", again.First(e => e.Term == "Kerensa").List);
        Assert.Equal("PC", again.First(e => e.Term == "Siobhan").List);
        // The page's sources: This PC's word, then the copy's.
        Assert.Equal([PronunciationSource.Computer, PronunciationSource.BookCopy],
            PronunciationLists.ComputerEntries(read, pc).Select(x => x.Source));
    }

    [Fact]
    public void AWordDeletedOnThisComputerIsNotBroughtBack()
    {
        var p = Book();
        p.ComputerPronunciations = PronunciationLists.CopyForSaving(p, Mac());
        var mac = Mac();
        mac.Entries.RemoveAll(e => e.Term == "Siobhan");
        Assert.Equal(["Kerensa"], PronunciationLists.Sayings(p, mac).Select(s => s.Term));
        Assert.Equal(["Kerensa"], PronunciationLists.CopyForSaving(p, mac)!.Select(e => e.Term));
    }

    [Fact]
    public void OlderBooksReadUnchanged()
    {
        var p = Book(("Kerensa", "KER-en-sa"));
        var json = Encoding.UTF8.GetString(ProjectJson.Encode(p));
        Assert.DoesNotContain("computerPronunciations", json);
        var read = ProjectJson.Decode(Encoding.UTF8.GetBytes(json));
        Assert.True(read.Settings.UsesComputerPronunciations);
        Assert.Null(read.ComputerPronunciations);
        Assert.Null(PronunciationLists.CopyForSaving(read, null));
    }

    [Fact]
    public void TurnedOffTheCopyStaysAsItWas()
    {
        var p = Book();
        p.ComputerPronunciations = PronunciationLists.CopyForSaving(p, Mac());
        p.Settings.UsesComputerPronunciations = false;
        Assert.Same(p.ComputerPronunciations, PronunciationLists.CopyForSaving(p, new PronunciationList("PC")));
        Assert.Empty(PronunciationLists.ComputerEntries(p, Mac()));
    }

    [Fact]
    public void MergingAsksOnlyAboutRealConflicts()
    {
        var list = Mac();
        ListedPronunciation[] incoming =
        [
            new("kerensa", "KER-en-sa"),             // conflict
            new("Siobhan", "shi-VAWN", "Irish"),     // same
            new("Aoife", "EE-fa"),                    // new
            new("", "x"),                             // skipped
        ];
        Assert.Equal(["kerensa"], PronunciationLists.Conflicts(incoming, list).Select(e => e.Term));
        var kept = list.Copy();
        Assert.Equal(new PronunciationLists.MergeResult(1, 0, 1, 1), PronunciationLists.Merge(incoming, kept, PronunciationConflict.KeepExisting));
        Assert.Equal("keh-REN-sa", kept.Entry("Kerensa")!.SayItAs);
        Assert.Equal("Irish", kept.Entry("Siobhan")!.Notes);
        var r = PronunciationLists.Merge(incoming, list, PronunciationConflict.Replace);
        Assert.Equal(1, r.Replaced);
        Assert.Equal("KER-en-sa", list.Entry("Kerensa")!.SayItAs);
        Assert.Equal(3, list.Entries.Count);
        // A replaced word keeps its place and id; the book's copy marker never comes along.
        Assert.Equal(Mac().Entries.Count, list.Entries.TakeWhile(e => e.Term != "Aoife").Count());
        Assert.All(list.Entries, e => Assert.Null(e.List));
    }

    [Fact]
    public void ABookShareSkipsDismissedAndEmptyEntries()
    {
        var p = Book(("Kerensa", " KER-en-sa "), ("Tom", ""), ("  ", "x"));
        p.Pronunciations.Add(new Pronunciation { Term = "Pip", SayItAs = "pip", Dismissed = true });
        var e = Assert.Single(PronunciationLists.BookEntries(p));
        Assert.Equal(("Kerensa", "KER-en-sa"), (e.Term, e.SayItAs));
        Assert.Equal(["kerensa"], PronunciationLists.BookKeys(p));
    }

    [Fact]
    public void SpeechUsesTheComputersListThroughSayings()
    {
        var before = PronunciationLists.Computer;
        try
        {
            PronunciationLists.Computer = Mac();
            Assert.Equal("keh-REN-sa", Say(SpeechScript.Sayings(Book()), "Kerensa"));
            PronunciationLists.Computer = null;
            Assert.Empty(SpeechScript.Sayings(Book()));
        }
        finally { PronunciationLists.Computer = before; }
    }

    // MARK: Files

    [Fact]
    public void CsvRoundTripsQuotesCommasAndCase()
    {
        ListedPronunciation[] entries =
        [
            new("Will", "Will", "The name, \"not\" the verb", matchCase: true),
            new("Llanfair, PG", "HLAN-vire"),
        ];
        var csv = PronunciationFile.Export(entries, PronunciationFile.Format.Csv);
        Assert.StartsWith("Word,Say It As,Match Case,Notes", Encoding.UTF8.GetString(csv));
        var back = PronunciationFile.Read(csv, "list.csv");
        Assert.Equal(["Llanfair, PG", "Will"], back.Select(e => e.Term));
        Assert.True(back.First(e => e.Term == "Will").MatchCase);
        Assert.Equal("The name, \"not\" the verb", back.First(e => e.Term == "Will").Notes);
    }

    [Fact]
    public void JsonRoundTripsAndPlainTextReads()
    {
        var json = PronunciationFile.Export(Mac().Entries, PronunciationFile.Format.Json);
        Assert.Contains("InnerVoice Pronunciations", Encoding.UTF8.GetString(json));
        Assert.Equal(2, PronunciationFile.Read(json).Count);
        var text = "# names\nKerensa = keh-REN-sa\nSiobhan → shi-VAWN\nAoife\tEE-fa\nnothing here\n";
        Assert.Equal(["Kerensa", "Siobhan", "Aoife"], PronunciationFile.Read(Encoding.UTF8.GetBytes(text), "names.txt").Select(e => e.Term));
        var empty = Assert.Throws<PronunciationFileException>(() => PronunciationFile.Read(Encoding.UTF8.GetBytes("  \n")));
        Assert.Equal(PronunciationFileException.Problem.Empty, empty.Kind);
        Assert.Equal("The file has no pronunciations in it.", empty.Message);
        var bad = Assert.Throws<PronunciationFileException>(() => PronunciationFile.Read(Encoding.UTF8.GetBytes("{\"a\":1}")));
        Assert.Equal(PronunciationFileException.Problem.Unreadable, bad.Kind);
        Assert.Equal("InnerVoice can’t read this file: it isn’t a pronunciations file.", bad.Message);
    }

    [Fact]
    public void CsvWithoutAHeaderAndWithOtherColumnNames()
    {
        var plain = PronunciationFile.Read(Encoding.UTF8.GetBytes("Word,Say It As\nKerensa,keh-REN-sa\n"), "a.csv");
        Assert.Equal(["keh-REN-sa"], plain.Select(e => e.SayItAs));
        var other = PronunciationFile.Read(Encoding.UTF8.GetBytes("Notes,Pronunciation,Term\nnone,shi-VAWN,Siobhan\n"), "b.csv");
        Assert.Equal(("Siobhan", "shi-VAWN", "none"), (other[0].Term, other[0].SayItAs, other[0].Notes));
        var bare = PronunciationFile.Read(Encoding.UTF8.GetBytes("Kerensa,keh-REN-sa,yes,Cornish\r\nTom,tom\r\n"), "c.csv");
        Assert.Equal(("Kerensa", "keh-REN-sa", true, "Cornish"), (bare[0].Term, bare[0].SayItAs, bare[0].MatchCase, bare[0].Notes));
        Assert.Equal(2, bare.Count);
    }

    [Fact]
    public void TsvBomArrowsColonsAndOtherJsonShapes()
    {
        var tsv = PronunciationFile.Read(Encoding.UTF8.GetBytes("﻿Word\tSay It As\tMatch Case\nWill\tVill\tx\n"), "a.tsv");
        Assert.Equal(("Will", "Vill", true), (tsv[0].Term, tsv[0].SayItAs, tsv[0].MatchCase));
        var text = PronunciationFile.Read(Encoding.UTF8.GetBytes("Aoife -> EE-fa\r\nPip: pip\r\n# skip = me\r\n"), "names.txt");
        Assert.Equal(["EE-fa", "pip"], text.Select(e => e.SayItAs));
        var array = PronunciationFile.Read(Encoding.UTF8.GetBytes("[{\"term\":\"Pip\",\"sayItAs\":\"pip\",\"list\":\"X\"},{\"term\":\"No say\"}]"));
        Assert.Null(Assert.Single(array).List);
        var list = PronunciationFile.Read(Encoding.UTF8.GetBytes("{\"listID\":\"L\",\"entries\":[{\"id\":\"00000000-0000-4000-8000-000000000001\",\"term\":\"Pip\",\"sayItAs\":\"pip\"}]}"));
        Assert.Equal(Guid.Parse("00000000-0000-4000-8000-000000000001"), Assert.Single(list).Id);
    }

    // MARK: Between the Mac and Windows (Fixtures/pronunciations, made by tools/MacFixtures/make-pronunciation-fixtures.sh)

    static Guid Id(int n) => Guid.Parse($"00000000-0000-4000-8000-{n:D12}");

    /// The entries the Mac's fixture tool exports (Pronunciations/main.swift).
    static ListedPronunciation[] FixtureEntries() =>
    [
        new("Kerensa", "keh-REN-sa", "Cornish", id: Id(1)),
        new("Will", "Will", "The name, \"not\" the verb", matchCase: true, id: Id(2)),
        new("Llanfair, PG", "HLAN-vire", id: Id(3)),
        new("Siobhan", "shi-VAWN", "Irish\nTwo lines", id: Id(4)),
        new("Zoë", "ZO-ee", id: Id(5)),
        new("aoife", "EE-fa", list: "OTHER", id: Id(6)),
    ];

    static List<string> Plain(IEnumerable<ListedPronunciation> e) =>
        e.Select(x => $"{x.Term}|{x.SayItAs}|{x.Notes}|{x.MatchCase}|{x.List ?? "-"}").Order(StringComparer.Ordinal).ToList();

    static byte[] Bytes(string name) => File.ReadAllBytes(Fixtures.PathOf("pronunciations/" + name));

    [Fact]
    public void FilesTheMacExportedImportHere()
    {
        var expected = Plain(FixtureEntries().Select(e => { var o = e.Copy(); o.List = null; return o; }));
        Assert.Equal(expected, Plain(PronunciationFile.Read(Bytes("mac-export.csv"), "mac-export.csv")));
        var json = PronunciationFile.Read(Bytes("mac-export.json"), "mac-export.json");
        Assert.Equal(expected, Plain(json));
        Assert.Equal(Id(4), json.First(e => e.Term == "Siobhan").Id);
    }

    [Fact]
    public void WindowsExportsAreTheMacsFiles()
    {
        // CSV: the same bytes. JSON: the same tree (Swift writes "key" : value).
        var csv = PronunciationFile.Export(FixtureEntries(), PronunciationFile.Format.Csv);
        Assert.Equal(Encoding.UTF8.GetString(Bytes("mac-export.csv")), Encoding.UTF8.GetString(csv));
        var json = PronunciationFile.Export(FixtureEntries(), PronunciationFile.Format.Json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(Bytes("mac-export.json")), JsonNode.Parse(json)));
        Assert.DoesNotContain("\r", Encoding.UTF8.GetString(json));
        // The files the Mac's fixture tool reads back (it says "the Mac reads it" for each).
        if (Fixtures.ShouldWrite)
        {
            File.WriteAllBytes(Fixtures.PathOf("pronunciations/windows-export.csv"), csv);
            File.WriteAllBytes(Fixtures.PathOf("pronunciations/windows-export.json"), json);
        }
        Assert.Equal(csv, Bytes("windows-export.csv"));
        Assert.Equal(json, Bytes("windows-export.json"));
    }

    [Fact]
    public void TheMacsListAndBookReadAndWriteBackTheSame()
    {
        var listJson = JsonNode.Parse(Bytes("mac-list.json"));
        var list = JsonSerializer.Deserialize<PronunciationList>(Bytes("mac-list.json"), ProjectJson.Options)!;
        Assert.Equal("MAC-LIST", list.ListID);
        Assert.Equal(3, list.Entries.Count);
        Assert.True(list.Entry("will")!.MatchCase);
        Assert.True(JsonNode.DeepEquals(listJson, ProjectJson.SortKeys(JsonSerializer.SerializeToNode(list, ProjectJson.Options))));

        var macBook = JsonNode.Parse(Bytes("mac-project.json"))!;
        var book = ProjectJson.Decode(Bytes("mac-project.json"));
        Assert.False(book.Settings.UsesComputerPronunciations);
        Assert.Equal(["MAC-LIST", "MAC-LIST", "MAC-LIST"], book.ComputerPronunciations!.Select(e => e.List));
        // Turned off: nothing from either list speaks, and the copy is saved as it came.
        Assert.Equal(["Kerensa"], PronunciationLists.Sayings(book, list).Select(s => s.Term));
        Assert.True(JsonNode.DeepEquals(macBook, JsonNode.Parse(ProjectJson.Encode(book))));
        // The same book made here, the way the Mac's fixture tool makes it.
        var made = new Project { Title = "Pronunciation Fixture", ImportedAt = new AppleDate(800_000_000) };
        made.Pronunciations.Add(new Pronunciation { Id = Id(10), Term = "Kerensa", SayItAs = "KER-en-sa", AddedByUser = true });
        made.ComputerPronunciations = PronunciationLists.CopyForSaving(made, list);
        made.Settings.UsesComputerPronunciations = false;
        Assert.True(JsonNode.DeepEquals(macBook, JsonNode.Parse(ProjectJson.Encode(made))));
    }
}
