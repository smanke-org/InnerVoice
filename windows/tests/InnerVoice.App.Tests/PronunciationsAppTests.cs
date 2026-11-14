using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using Xunit;
using Source = InnerVoice.App.Pages.PronunciationRow.Source;

namespace InnerVoice.App.Tests;

/// This Book's and This PC's pronunciations in the app: the Pronunciations page's combined list
/// in every book type, the conflict question, the book's saved copy, and Settings › Pronunciations.
public class PronunciationsAppTests
{
    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    /// This PC's list for one test (the app's list is shared by every test in the run).
    static void WithComputerList(IEnumerable<ListedPronunciation> entries, Action test)
    {
        var computer = ComputerPronunciations.Shared;
        var kept = computer.List.Entries.Select(e => e.Copy()).ToList();
        computer.Change(l => l.Entries = entries.ToList());
        try { test(); }
        finally
        {
            computer.Change(l => l.Entries = kept);
            ComputerPronunciations.AnswerConflicts = null;
        }
    }

    static List<string> Headers(PronunciationsPage page) =>
        page.MenuItems().OfType<MenuItem>().Select(m => (string)m.Header!).ToList();

    [AvaloniaFact]
    public void TheTwoListsOnTheNarrationToolsPage() => WithComputerList(
        [new("Kerensa", "keh-REN-sa"), new("Siobhan", "shi-VAWN"), new("Pip", "pip")], () =>
    {
        var computer = ComputerPronunciations.Shared;
        // The sample is a Narration Tools book: it has the page now.
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        Assert.True(doc.Project.ShowsPronunciations);
        doc.Change("Add Term", p => p.Pronunciations.Add(new Pronunciation { Term = "Kerensa", SayItAs = "KER-en-sa", AddedByUser = true }));
        // The book's copy: a word from another computer, and one deleted from This PC's list.
        doc.ApplyQuietly(p => p.ComputerPronunciations =
            [new ListedPronunciation("Aoife", "EE-fa", list: "OTHER"), new ListedPronunciation("Morwenna", "mor-WEN-a", list: computer.List.ListID)]);
        var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
        w.Show();
        w.Workspace.Go(PageKind.Pronunciations);
        Pump();
        Assert.Equal(PageKind.Pronunciations, w.Workspace.Page);
        var page = Assert.IsType<PronunciationsPage>(w.FindPage(PageKind.Pronunciations));

        Source? Kind(string term) => page.Rows.FirstOrDefault(r => r.Term == term)?.Kind;
        // All: the book's, then This PC's it uses, then the copy's; This PC's Kerensa loses to the book's.
        Assert.Equal(Source.Book, Kind("Kerensa"));
        Assert.Equal(Source.Computer, Kind("Siobhan"));
        Assert.Equal(Source.BookCopy, Kind("Aoife"));
        Assert.Null(Kind("Morwenna"));
        Assert.Single(page.Rows, r => r.Term == "Kerensa");
        page.Show(PronunciationFilter.Computer);
        Assert.Equal([("Kerensa", Source.Replaced), ("Pip", Source.Computer), ("Siobhan", Source.Computer), ("Aoife", Source.BookCopy)],
            page.Rows.Select(r => (r.Term, r.Kind)));
        Assert.Equal(("This PC · Not Used", "This PC", "Another Computer"), (page.Rows[0].Tag.Text, page.Rows[1].Tag.Text, page.Rows[3].Tag.Text));
        page.Show(PronunciationFilter.Book);
        Assert.All(page.Rows, r => Assert.Equal(Source.Book, r.Kind));

        // Speech: the book's word, This PC's, the copy's.
        var say = SpeechScript.Sayings(doc.Project);
        Assert.Equal("KER-en-sa", say.First(s => s.Term == "Kerensa").Say);
        Assert.Equal("shi-VAWN", say.First(s => s.Term == "Siobhan").Say);
        Assert.Equal("EE-fa", say.First(s => s.Term == "Aoife").Say);
        Assert.DoesNotContain(say, s => s.Term == "Morwenna");

        // Turned off for this book: one undo step; This PC's rows say Off, nothing of theirs speaks.
        PronunciationsPage.SetUsesComputer(doc, false);
        Pump();
        Assert.Equal("Don’t Use This PC’s Pronunciations", doc.UndoName);
        page.Show(PronunciationFilter.Computer);
        Assert.Equal([Source.NotUsed, Source.NotUsed, Source.NotUsed], page.Rows.Select(r => r.Kind));
        Assert.Equal("This PC · Off", page.Rows[0].Tag.Text);
        Assert.DoesNotContain(SpeechScript.Sayings(doc.Project), s => s.Term == "Siobhan");
        doc.Undo();
        Assert.True(doc.Project.Settings.UsesComputerPronunciations);

        // The right-click menu for each kind of row.
        page.Show(PronunciationFilter.All);
        var kerensa = doc.Project.Pronunciations.First(p => p.Term == "Kerensa").Id;
        page.SelectRows([kerensa]);
        Assert.Equal(["Add to This PC’s List", "Dismiss", "Delete"], Headers(page));
        page.SelectRows([computer.List.Entry("Siobhan")!.Id]);
        Assert.Equal(["Change for This Book Only", "Remove from This PC’s List…"], Headers(page));
        var aoife = page.Rows.First(r => r.Term == "Aoife");
        page.SelectRows([aoife.Id]);
        Assert.Equal(["Change for This Book Only", "Add to This PC’s List"], Headers(page));

        // Change for This Book Only: a copy in the book's list, which then wins.
        page.OverrideInBook(page.Rows.First(r => r.Term == "Siobhan"));
        Assert.Equal("Change Pronunciation for This Book", doc.UndoName);
        Assert.Equal(PronunciationFilter.Book, page.Filter);
        var own = doc.Project.Pronunciations.Single(p => p.Term == "Siobhan");
        Assert.Equal([own.Id], page.Selected);
        doc.Change("Edit Pronunciation", p => p.Pronunciations.First(x => x.Id == own.Id).SayItAs = "sha-VAHN");
        Assert.Equal("sha-VAHN", SpeechScript.Sayings(doc.Project).First(s => s.Term == "Siobhan").Say);
        Assert.Equal("shi-VAWN", computer.List.Entry("Siobhan")!.SayItAs);
        w.Close();
    });

    [AvaloniaFact]
    public async Task AddingToThisPCAsksAboutConflictsOnly()
    {
        var computer = ComputerPronunciations.Shared;
        var kept = computer.List.Entries.Select(e => e.Copy()).ToList();
        computer.Change(l => l.Entries = [new("Kerensa", "keh-REN-sa"), new("Siobhan", "shi-VAWN")]);
        try
        {
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Add Terms", p =>
            {
                p.Pronunciations.Add(new Pronunciation { Term = "Kerensa", SayItAs = "KER-en-sa", AddedByUser = true });
                p.Pronunciations.Add(new Pronunciation { Term = "Siobhan", SayItAs = "shi-VAWN", Notes = "Irish", AddedByUser = true });
                p.Pronunciations.Add(new Pronunciation { Term = "Aoife", SayItAs = "EE-fa", AddedByUser = true });
            });
            var w = new ProjectWindow(doc);
            w.Show();
            var entries = PronunciationLists.BookEntries(doc.Project);
            int asked = 0;
            ComputerPronunciations.AnswerConflicts = c => { asked = c.Count; return null; };
            Assert.Null(await computer.AddAsking(w, entries, "This Book’s"));
            Assert.Equal(1, asked);
            Assert.Equal(2, computer.List.Entries.Count);
            ComputerPronunciations.AnswerConflicts = _ => PronunciationConflict.KeepExisting;
            var r = await computer.AddAsking(w, entries, "This Book’s");
            Assert.Equal(new PronunciationLists.MergeResult(1, 0, 1, 1), r);
            Assert.Equal("Added 1, kept 1 of This PC’s, 1 already there.", ComputerPronunciations.Describe(r!));
            Assert.Equal("keh-REN-sa", computer.List.Entry("Kerensa")!.SayItAs);
            Assert.Equal("Irish", computer.List.Entry("Siobhan")!.Notes);
            ComputerPronunciations.AnswerConflicts = _ => PronunciationConflict.Replace;
            r = await computer.AddAsking(w, entries, "This Book’s");
            Assert.Equal(1, r!.Replaced);
            Assert.Equal("KER-en-sa", computer.List.Entry("Kerensa")!.SayItAs);
            // Nothing in conflict: added without a question.
            ComputerPronunciations.AnswerConflicts = _ => throw new InvalidOperationException("asked");
            Assert.Equal(1, (await computer.AddAsking(w, [new ListedPronunciation("Pip", "pip")], "the Imported Ones"))!.Added);
            w.Close();
        }
        finally
        {
            ComputerPronunciations.AnswerConflicts = null;
            computer.Change(l => l.Entries = kept);
        }
    }

    [AvaloniaFact]
    public void TheBookSavesACopyOfThisPCsWords() => WithComputerList([new("Siobhan", "shi-VAWN", matchCase: true)], () =>
    {
        var computer = ComputerPronunciations.Shared;
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        Assert.Null(doc.Project.ComputerPronunciations);
        var saved = ProjectJson.Decode(doc.EncodeForSaving());
        var copy = Assert.Single(saved.ComputerPronunciations!);
        Assert.Equal(("Siobhan", computer.List.ListID, true), (copy.Term, copy.List, copy.MatchCase));
        // Only the file gets the fresh copy; the open book keeps what it opened with.
        Assert.Null(doc.Project.ComputerPronunciations);
        // Turned off: the copy stays as it was (none here) and the setting is saved.
        PronunciationsPage.SetUsesComputer(doc, false);
        saved = ProjectJson.Decode(doc.EncodeForSaving());
        Assert.Null(saved.ComputerPronunciations);
        Assert.True(saved.Settings.ComputerPronunciationsOff);
    });

    [AvaloniaFact]
    public void SettingsEditsThisPCsList() => WithComputerList([new("Siobhan", "shi-VAWN")], () =>
    {
        var computer = ComputerPronunciations.Shared;
        var settings = SettingsWindow.Show(null, "Pronunciations");
        Pump();
        var pane = settings.Pronunciations!;
        Assert.Equal(["Siobhan"], pane.Listed.Select(e => e.Term));
        pane.Add();
        Pump();
        Assert.Equal(2, computer.List.Entries.Count);
        Assert.Contains(pane.Listed, e => e.Term == "New Word");
        computer.Update(computer.List.Entry("New Word")!.Id, e => { e.Term = "Aoife"; e.SayItAs = "EE-fa"; });
        Pump();
        Assert.Equal(["Aoife", "Siobhan"], pane.Listed.Select(e => e.Term));
        // Speech reads the list as it is now.
        Assert.Equal("EE-fa", PronunciationLists.Computer!.Entry("aoife")!.SayItAs);
        settings.Close();
    });
}
