using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;
using InnerVoice.App;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.Import;

[assembly: AvaloniaTestApplication(typeof(InnerVoice.App.Tests.TestApp))]

namespace InnerVoice.App.Tests;

public static class TestApp
{
    [ModuleInitializer]
    public static void Init() => Environment.SetEnvironmentVariable("INNERVOICE_TEST", "1");

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public static Project Sample()
    {
        var blocks = PlainTextReader.Blocks(SampleStory.Markdown, markdown: true);
        var chapters = ChapterDetector.Chapters(blocks, SampleStory.Title);
        var p = ProjectBuilder.Make(new ImportedBook(SampleStory.Title, SampleStory.Author, chapters), "Sample Story.md");
        p.LastTagged = AppleDate.Now;
        return p;
    }
}

/// Light and dark PNGs of every page in INNERVOICE_SNAP_DIR (the Mac's tools/snap.sh).
public class Snapshots
{
    static string? Dir => Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR");

    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void EveryPage()
    {
        if (Dir is not { } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            Pump();
            foreach (var page in new[] { PageKind.Manuscript, PageKind.Review, PageKind.Cast, PageKind.Record, PageKind.Retakes })
            {
                w.Workspace.Go(page);
                Pump();
                w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"{page}-{name}.png"), (int?)null);
            }
            // The Assign panel on the first line that needs the narrator.
            w.Workspace.Go(PageKind.Manuscript);
            Pump();
            var panelHost = new Avalonia.Controls.Window { Width = 340, Height = 520, Content = new InnerVoice.App.Controls.AssignPanel(doc, [doc.Project.Lines.First(doc.Project.NeedsYou).Id], (_, _) => { }, () => { }) };
            panelHost.Show();
            Pump();
            panelHost.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"Assign-{name}.png"), (int?)null);
            panelHost.Close();
            w.Workspace.Go(PageKind.Cast);
            (w.FindPage(PageKind.Cast) as CastPage)?.ShowCharacter(doc.Project.Characters[0].Id);
            Pump();
            w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"Character-{name}.png"), (int?)null);
            w.Close();
        }
        var start = new StartWindow();
        start.Show();
        Pump();
        start.CaptureRenderedFrame()?.Save(Path.Combine(dir, "Start-dark.png"), (int?)null);
    }
}

public class Books
{
    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Fixtures", name);
    }

    /// A project the Mac saved opens, shows every page, and saves with nothing lost.
    [AvaloniaFact]
    public void OpensAMacProject()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"iv-{Guid.NewGuid():N}.innervoice");
        File.Copy(Fixture("sample-mac.innervoice"), tmp);
        try
        {
            var doc = ProjectDocument.Open(tmp);
            Assert.False(doc.IsReadOnly);
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            foreach (var page in new[] { PageKind.Manuscript, PageKind.Cast, PageKind.Record, PageKind.Retakes })
            {
                w.Workspace.Go(page);
                Pump();
            }
            doc.ToggleRecorded(doc.Project.Chapters[0].Paragraphs[5].Id);
            Assert.True(doc.TrySave(out var error), error);
            var again = ProjectArchive.Read(File.ReadAllBytes(tmp));
            var p = ProjectJson.Decode(again.ProjectJson);
            Assert.Equal(3, p.Recorded.Count);
            Assert.True(p.Settings.Extra!.ContainsKey("voiceEngine"));
            Assert.True(again.Audio.ContainsKey("Kerensa.m4a"));
            doc.Undo();
            Assert.Equal(2, doc.Project.Recorded.Count);
            w.Close();
        }
        finally { File.Delete(tmp); }
    }

    /// Every manuscript format opens as a project; the narrator's edits undo cleanly.
    [AvaloniaFact]
    public void ImportsAndEdits()
    {
        foreach (var f in new[] { "docx", "odt", "epub", "html", "rtf", "pdf" })
        {
            var book = ManuscriptImporter.Read(Fixture($"import/sample.{f}"));
            var p = ProjectBuilder.Make(book, $"sample.{f}");
            Assert.True(p.Lines.Count > 20, $"{f}: {p.Lines.Count} lines");
            Assert.Equal(ProjectPurpose.Narration, p.Settings.Purpose);
        }
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var project = doc.Project;
        var tom = project.Characters.First(c => c.Name.StartsWith("Tom")).Id;
        var mara = project.Characters.First(c => c.Name.StartsWith("Mara")).Id;
        // Fix one line of the back-and-forth; the exchange proposes the rest.
        var towel = doc.Project.Lines.First(l => doc.Project.TextOf(l, doc.Index).StartsWith("I'll be wanting a towel"));
        var before = towel.SpeakerID;
        doc.Assign([towel.Id], tom);
        Assert.Equal(AssignmentSource.User, doc.Project.Lines.First(l => l.Id == towel.Id).Source);
        var proposals = Exchange.Proposals(doc.Project, towel.Id, before);
        if (proposals.Count > 0) doc.ApplyExchange(proposals);
        // Make a line, mark one as narration, delete a paragraph.
        var para = doc.Project.Chapters[0].Paragraphs[0];
        var made = doc.MakeLine(para.Id, new TextSpan(4, 4), mara);
        Assert.Contains(doc.Project.Lines, l => l.Id == made);
        doc.RemoveLines([made]);
        int paragraphs = doc.Project.Chapters[0].Paragraphs.Count;
        doc.DeleteText(new() { [para.Id] = [new TextSpan(0, para.Text.Length)] });
        Assert.Equal(paragraphs - 1, doc.Project.Chapters[0].Paragraphs.Count);
        doc.Undo();
        Assert.Equal(paragraphs, doc.Project.Chapters[0].Paragraphs.Count);
        Assert.Equal("Mark as Narration", doc.UndoName);
    }

    /// A novel-length book (about 150,000 words) lays out quickly enough to open.
    [AvaloniaFact]
    public void NovelLaysOutQuickly()
    {
        var md = string.Concat(Enumerable.Range(1, 300).Select(i => SampleStory.Markdown.Replace("# Chapter", $"# Part {i} Chapter")));
        var blocks = PlainTextReader.Blocks(md, markdown: true);
        var chapters = ChapterDetector.Chapters(blocks, "Long");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var project = ProjectBuilder.Make(new ImportedBook("Long", "", chapters), "Long.md");
        var build = sw.ElapsedMilliseconds;
        Assert.True(project.WordCount > 140_000, $"{project.WordCount} words");
        var doc = ProjectDocument.Unsaved(project);
        var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
        sw.Restart();
        w.Show();
        Pump();
        var layout = sw.ElapsedMilliseconds;
        sw.Restart();
        doc.Assign([project.Lines[10].Id], project.Characters[0].Id);
        Pump();
        var change = sw.ElapsedMilliseconds;
        Console.WriteLine($"Novel: {project.WordCount:N0} words, {project.Lines.Count:N0} lines; rules {build} ms, open {layout} ms, assign {change} ms");
        Assert.True(layout < 8000, $"open took {layout} ms");
        Assert.True(change < 1500, $"a change took {change} ms");
        w.Close();
    }
}
