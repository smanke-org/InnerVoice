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
    public static void Init()
    {
        Environment.SetEnvironmentVariable("INNERVOICE_TEST", "1");
        // An agent test copy of its own (AgentAppTests): its own pipe and folder, so nothing
        // here can reach the person's running InnerVoice or their settings.
        Environment.SetEnvironmentVariable("APP_AGENT_PIPE", null);
        Environment.SetEnvironmentVariable("APP_STORE", Path.Combine(Path.GetTempPath(), $"iv-apptests-{Guid.NewGuid():N}"));
    }

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
            foreach (var page in new[] { PageKind.Manuscript, PageKind.Review, PageKind.Cast, PageKind.Index, PageKind.Teleprompter })
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
        var settings = InnerVoice.App.Windows.SettingsWindow.Show(null, "AI");
        Pump();
        settings.CaptureRenderedFrame()?.Save(Path.Combine(dir, "Settings-AI.png"), (int?)null);
        settings.Close();
        // A revised draft: one paragraph edited, one added.
        {
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            var md = SampleStory.Markdown.Replace("“Then you'll be wanting tea.”", "“Then you'll be wanting tea, I suppose.”")
                .Replace("“Towels are in the chest. Same as always.”", "“Towels are in the chest. Same as always.”\n\n“And the lamp?” Mara asked.");
            var book = new ImportedBook("Sample Story", "", ChapterDetector.Chapters(PlainTextReader.Blocks(md, markdown: true), "Sample Story"));
            var merged = RevisionMerger.Merge(doc.Project, book, "Sample Story v2.md");
            doc.Change("Import Revised Manuscript", p => { p.Chapters = merged.Project.Chapters; p.Lines = merged.Project.Lines; p.Revisions = merged.Project.Revisions; }, textChanged: true);
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            w.Workspace.Go(PageKind.Revisions);
            Pump();
            w.CaptureRenderedFrame()?.Save(Path.Combine(dir, "Revisions-dark.png"), (int?)null);
            w.Close();
        }
        var start = new StartWindow();
        start.Show();
        Pump();
        start.CaptureRenderedFrame()?.Save(Path.Combine(dir, "Start-dark.png"), (int?)null);
    }
}

/// Microsoft Store screenshots (1600 × 900, over the Store's 1366 × 768 minimum) of the sample
/// book in INNERVOICE_STORE_DIR: the manuscript, Review, a character and the Teleprompter.
public class StoreShots
{
    static void Pump()
    {
        for (int i = 0; i < 8; i++) Dispatcher.UIThread.RunJobs();
    }

    /// The sample story as the Mac's snapshots show it (SeedData): tagged by the AI with one
    /// unsure guess and one line nobody was found for, four characters with summaries,
    /// voice notes and two narrators.
    static Project Tagged()
    {
        var blocks = PlainTextReader.Blocks(SampleStory.Markdown, markdown: true);
        var p = ProjectBuilder.Make(new ImportedBook("The Keeper of Penhallow Light", SampleStory.Author,
            ChapterDetector.Chapters(blocks, "The Keeper of Penhallow Light")), "Penhallow.docx");
        Guid Id(string name)
        {
            var first = name.Split(' ')[0];
            var c = p.Characters.FirstOrDefault(c => c.Name == name || c.AllNames.Contains(name) || c.Name == first);
            if (c is not null) { c.Name = name; return c.Id; }
            c = new BookCharacter(name, CharacterPalette.ForNew(name, p.Characters.Select(x => x.ColorHex)));
            p.Characters.Add(c);
            return c.Id;
        }
        var ids = new Dictionary<string, Guid> { ["tom"] = Id("Tom Hale"), ["mara"] = Id("Mara Quinn"), ["edda"] = Id("Edda Voss"), ["pip"] = Id("Pip Aelwyn") };
        (string, string)[] table = [
            ("You're late", "tom"), ("The path was", "mara"), ("Then you'll be", "tom"), ("I'll be wanting", "mara"),
            ("Towels are", "tom"), ("Same as always", "mara"), ("He hasn't asked", "mara"), ("Maybe he doesn't", "mara"),
            ("Doctor Voss came", "tom"), ("What did you", "mara"), ("That you'd be", "tom"), ("Miss Quinn!", "pip"),
            ("Slow down", "mara"), ("Da says if", "pip"), ("You have been avoiding", "edda"), ("I've been busy", "mara"),
            ("Busy.", "edda"), ("Your father wrote", "edda"), ("Who is Kerensa", "mara"),
            ("Read the rest", "edda"), ("I can't", "mara"), ("You can.", "edda"), ("That's what frightens", "mara"),
            ("Is it treasure", "pip"), ("Out", "edda"), ("Your father and I", "tom"), ("She was his sister", "tom"),
            ("Is she alive", "mara"), ("Aye.", "tom"), ("And you didn't", "mara"), ("I'm telling you", "tom"),
        ];
        var index = new BookIndex(p);
        foreach (var line in p.Lines.Where(l => l.Source is AssignmentSource.None or AssignmentSource.Exchange))
        {
            var text = p.TextOf(line, index).Trim('\u2014', '\u2013', ' ');
            var who = table.FirstOrDefault(t => text.StartsWith(t.Item1)).Item2;
            line.SpeakerID = who is null ? null : ids[who];
            line.Source = AssignmentSource.Ai;
            line.Confidence = text.StartsWith("Is it treasure") ? 0.4 : 0.86;
            if (text.StartsWith("Who is Kerensa")) { line.SpeakerID = null; line.Confidence = 0; }
        }
        p.LastTagged = AppleDate.Now;
        p.SummariesBuilt = AppleDate.Now;
        Guid? Para(string s) => p.Chapters.SelectMany(c => c.Paragraphs).FirstOrDefault(x => x.Text.Contains(s))?.Id;
        void Fact(string who, FactKind kind, string text, string source) =>
            p.Characters[p.CharacterIndex(ids[who])].Facts.Add(new Fact { Kind = kind, Text = text, SourceParagraphID = Para(source), Quote = source, FromAI = true });
        Fact("tom", FactKind.Age, "About sixty", "sixty if he was a day");
        Fact("tom", FactKind.Voice, "Gravelly from shouting over gales", "the gravel of a man");
        Fact("tom", FactKind.Accent, "Soft Highland Scottish burr (from Inverness)", "soft burr of the Highlands");
        Fact("tom", FactKind.History, "Kept the light with Mara's father for thirty years", "kept this light together for thirty years");
        Fact("tom", FactKind.Appearance, "Broad man gone narrow with age", "broad man gone narrow");
        Fact("edda", FactKind.Accent, "German, from Hamburg; clipped, careful vowels", "clipped, careful vowels");
        Fact("edda", FactKind.History, "Came from Hamburg twenty years ago", "come from Hamburg twenty years ago");
        Fact("edda", FactKind.Personality, "Firm but kind", "not unkindly");
        Fact("mara", FactKind.Relationships, "Daughter of the late keeper, old Quinn", "You are your father's daughter");
        Fact("pip", FactKind.Age, "A small boy", "one small fist");
        Fact("pip", FactKind.Voice, "Breathless, tumbling", "Miss Quinn! Miss Quinn");
        var tom = p.Characters[p.CharacterIndex(ids["tom"])];
        tom.VoiceNotes = "Low and slow, chest voice, Highland lilt on the vowels";
        tom.Aliases = ["Tom"];
        p.Characters[p.CharacterIndex(ids["mara"])].Aliases = ["Mara", "Miss Quinn"];
        p.Characters[p.CharacterIndex(ids["edda"])].Aliases = ["Doctor Voss", "the doctor"];
        p.Characters[p.CharacterIndex(ids["pip"])].Aliases = ["Pip"];
        var jane = new Narrator { Name = "Jane Doe" };
        var sam = new Narrator { Name = "Sam Roe" };
        p.Narrators = [jane, sam];
        foreach (var c in p.Characters) c.NarratorID = c.Id == ids["mara"] || c.Id == ids["edda"] ? jane.Id : sam.Id;
        p.Settings.Purpose = ProjectPurpose.Narration;
        CharacterPalette.RecolorAutomatic(p, new BookIndex(p));
        return p;
    }

    [AvaloniaFact]
    public void Listing()
    {
        if (Environment.GetEnvironmentVariable("INNERVOICE_STORE_DIR") is not { } dir) return;
        Directory.CreateDirectory(dir);
        foreach (var (variant, name) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
        {
            Application.Current!.RequestedThemeVariant = variant;
            var doc = ProjectDocument.Unsaved(Tagged());
            var w = new ProjectWindow(doc) { Width = 1600, Height = 900 };
            w.Show();
            Pump();
            foreach (var page in new[] { PageKind.Manuscript, PageKind.Review, PageKind.Teleprompter })
            {
                w.Workspace.Go(page);
                Pump();
                w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"store-{page}-{name}.png"), (int?)null);
            }
            w.Workspace.Go(PageKind.Cast);
            var main = doc.Project.Characters.OrderByDescending(c => c.Facts.Count).First();
            (w.FindPage(PageKind.Cast) as CastPage)?.ShowCharacter(main.Id);
            Pump();
            w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"store-Character-{name}.png"), (int?)null);
            w.Close();
        }
    }
}

public class Books
{
    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    internal static string Fixture(string name)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "Fixtures"))) dir = dir.Parent;
        return Path.Combine(dir!.FullName, "Fixtures", name);
    }

    /// A project the Mac saved opens, shows every page, and saves with nothing lost: recorded
    /// marks, retakes and pronunciation samples aren't shown any more, but stay in the file.
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
            foreach (var page in new[] { PageKind.Manuscript, PageKind.Cast, PageKind.Index, PageKind.Teleprompter })
            {
                w.Workspace.Go(page);
                Pump();
            }
            int pronunciations = doc.Project.Pronunciations.Count;
            bool initials = doc.Project.Settings.ShowInitials;
            doc.UpdateSettings("Hide Initials", s => s.ShowInitials = !initials);
            Assert.True(doc.TrySave(out var error), error);
            var again = ProjectArchive.Read(File.ReadAllBytes(tmp));
            var p = ProjectJson.Decode(again.ProjectJson);
            Assert.Equal(!initials, p.Settings.ShowInitials);
            Assert.Equal(2, p.Recorded.Count);
            Assert.Single(p.Retakes);
            Assert.Equal(pronunciations, p.Pronunciations.Count);
            Assert.True(p.Settings.Extra!.ContainsKey("voiceEngine"));
            Assert.True(again.Audio.ContainsKey("Kerensa.m4a"));
            doc.Undo();
            Assert.Equal(initials, doc.Project.Settings.ShowInitials);
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

/// Set INNERVOICE_TEST_MODEL to a GGUF file to tag the sample book and build summaries for real.
[Collection("Model choice")]
public class RealModel
{
    [AvaloniaFact]
    public async Task TagsAndSummarizesTheSample()
    {
        var path = Environment.GetEnvironmentVariable("INNERVOICE_TEST_MODEL");
        if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
        Environment.SetEnvironmentVariable("INNERVOICE_MODEL", "file:" + path);
        var doc = ProjectDocument.Unsaved(TestApp.Sample().Also(p => p.LastTagged = null));
        int before = doc.Project.LinesNeedingYou;
        var runner = new InnerVoice.App.AI.AIRunner(doc);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runner.TagSpeakers();
        Assert.Null(runner.Failure);
        int after = doc.Project.LinesNeedingYou;
        var tagged = doc.Project.Lines.Count(l => l.Source == AssignmentSource.Ai);
        Console.WriteLine($"Tag Speakers: {sw.ElapsedMilliseconds} ms; needing you {before} → {after}; AI lines {tagged}; {runner.Finished}");
        foreach (var l in doc.Project.Lines.Take(12))
            Console.WriteLine($"  {doc.Project.Character(l.SpeakerID)?.Name ?? "?",-12} {l.Confidence:P0} {l.Source,-8} {doc.Project.TextOf(l, doc.Index)}");
        Assert.True(after < before, $"{after} still need you");
        Assert.True(doc.Project.Lines.Where(l => l.Source == AssignmentSource.Ai).All(l => l.AiModel is not null));
        Assert.Equal("Tag Speakers", doc.UndoName);

        var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara"));
        sw.Restart();
        await runner.BuildSummaries([mara.Id]);
        Assert.Null(runner.Failure);
        var m = doc.Project.Character(mara.Id)!;
        Console.WriteLine($"Build Summaries ({sw.ElapsedMilliseconds} ms): {string.Join("; ", m.Facts.Select(f => $"{f.Kind}: {f.Text}"))}");
        Console.WriteLine("Voice notes:\n" + m.VoiceNotes);
        Assert.NotEmpty(m.Facts);
        doc.Undo();
        Assert.Empty(doc.Project.Character(mara.Id)!.Facts);
        doc.Undo();
        Assert.Equal(before, doc.Project.LinesNeedingYou);
        InnerVoice.App.AI.ModelManager.Shared.UnloadNow();
    }
}

static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action) { action(value); return value; }
}
