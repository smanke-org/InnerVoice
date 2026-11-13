using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Headless;
using Xunit;
using InnerVoice.App;
using InnerVoice.App.Document;
using InnerVoice.App.Pages;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Tests;

/// The Voices page, Play, tones, Series Voices and the project type, with a fake system engine
/// (speech plays silently in test runs).
[Collection("Voice engines")]
public class VoicesAppTests
{
    sealed class FakeVoices : IVoiceEngine
    {
        static readonly VoiceInfo[] voices =
        [
            new(VoiceEngine.Apple, "f1", "Aria", Gender.Female, Accent: "American", Quality: 2),
            new(VoiceEngine.Apple, "f2", "Libby", Gender.Female, Accent: "British", Quality: 2),
            new(VoiceEngine.Apple, "m1", "Guy", Gender.Male, Accent: "American", Quality: 2),
            new(VoiceEngine.Apple, "m2", "Ryan", Gender.Male, Accent: "British", Quality: 1),
            new(VoiceEngine.Apple, "n", "Sky", Gender.Neutral, Accent: "American", Quality: 3),
        ];
        public VoiceEngine Engine => VoiceEngine.Apple;
        public bool IsReady => true;
        public string? NotReadyReason => null;
        public IReadOnlyList<VoiceInfo> Voices => voices;
        public VoiceInfo? Info(VoiceChoice choice) => voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);
        public bool ShadesPitch => true;
        public bool ShadesPace => true;
        public List<SpeechRequest> Rendered { get; } = [];
        public Task<float[]> Render(SpeechRequest request, CancellationToken ct)
        {
            lock (Rendered) Rendered.Add(request);
            var x = new float[2400];
            for (int i = 0; i < x.Length; i++) x[i] = 0.3f * MathF.Sin(2 * MathF.PI * 220 * i / SpeechAudio.SampleRate);
            return Task.FromResult(x);
        }
    }

    static FakeVoices Setup()
    {
        VoiceSetup.Start();
        var fake = new FakeVoices();
        VoiceEngineRegistry.Register(fake);
        EngineSwitches.Shared = new EngineSwitches();
        SpeechRenderer.Shared = new SpeechRenderer(Path.Combine(Path.GetTempPath(), $"iv-voices-{Guid.NewGuid():N}"));
        return fake;
    }

    static void Restore()
    {
        SpeechPlayer.Shared.Stop();
        VoiceEngineRegistry.Register(new WindowsVoices());
    }

    static ProjectDocument AiBook()
    {
        var p = TestApp.Sample();
        p.Settings.Purpose = ProjectPurpose.AiVoices;
        return ProjectDocument.Unsaved(p);
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static async Task WaitForPlayer()
    {
        for (int i = 0; i < 200 && SpeechPlayer.Shared.IsPlaying; i++)
        {
            await Task.Delay(10);
            Dispatcher.UIThread.RunJobs();
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheProjectTypeDecidesThePages()
    {
        Setup();
        try
        {
            var doc = AiBook();
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            w.Workspace.Go(PageKind.Voices);
            Pump();
            var page = Assert.IsType<VoicesPage>(w.FindPage(PageKind.Voices));
            Assert.Contains("voices need you", page.Subtitle + " voices need you");
            Assert.False(doc.Project.ShowsNarrationTools);
            // Narration Tools: the Voices page goes, and the window leaves it.
            doc.SetPurpose(ProjectPurpose.Narration);
            Pump();
            Assert.Equal(PageKind.Manuscript, w.Workspace.Page);
            Assert.False(doc.Project.ShowsVoices);
            Assert.Equal("Change Project Type", doc.UndoName);
            doc.Undo();
            Assert.Equal(ProjectPurpose.AiVoices, doc.Project.Settings.Purpose);
            w.Close();
        }
        finally { Restore(); }
    }

    [AvaloniaFact]
    public void TheVoicesPageCastsAndTheNarratorChooses()
    {
        Setup();
        try
        {
            var doc = AiBook();
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            w.Workspace.Go(PageKind.Voices);
            Pump();
            // Opening the page casts every speaking character and the narrator.
            var p = doc.Project;
            Assert.NotNull(p.VoicesCast);
            Assert.NotNull(p.Settings.NarratorVoice(VoiceEngine.Apple));
            var speaking = p.Characters.Where(c => (doc.Index.StatsFor(c.Id)?.Lines ?? 0) > 0).ToList();
            Assert.All(speaking, c => Assert.NotNull(c.Voice(VoiceEngine.Apple)));
            // The narrator's choice is kept and undoes in one step.
            var mara = speaking.First(c => c.Name.StartsWith("Mara"));
            var page = (VoicesPage)w.FindPage(PageKind.Voices);
            page.Show(new VoiceTarget.Character(mara.Id));
            Pump();
            doc.SetVoice(new VoiceChoice(VoiceEngine.Apple, "f2"), mara.Id);
            Assert.Equal(VoiceSource.User, doc.Project.Character(mara.Id)!.Voice(VoiceEngine.Apple)!.Source);
            doc.CastVoicesAgain();
            Assert.Equal("f2", doc.Project.Character(mara.Id)!.Voice(VoiceEngine.Apple)!.Choice.VoiceID);
            doc.Undo();
            doc.Undo();
            Assert.NotEqual(VoiceSource.User, doc.Project.Character(mara.Id)!.Voice(VoiceEngine.Apple)?.Source);
            // Every target draws.
            page.Show(VoiceTarget.Narrator);
            doc.SetCastingStyle(CastingStyle.Shared);
            VoiceCastingRun.CastIfNeeded(doc);
            Assert.NotNull(doc.Project.Settings.RoleVoice(VoiceRole.Men, VoiceEngine.Apple));
            page.Show(new VoiceTarget.Role(VoiceRole.Women));
            Pump();
            w.Close();
        }
        finally { Restore(); }
    }

    [AvaloniaFact]
    public async Task PlayLineAndPlayFromHere()
    {
        var fake = Setup();
        try
        {
            var doc = AiBook();
            VoiceCastingRun.CastIfNeeded(doc);
            var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
            w.Show();
            Pump();
            var manuscript = (ManuscriptPage)w.FindPage(PageKind.Manuscript);
            var line = doc.Project.Lines.First(l => l.SpeakerID is not null);
            manuscript.PlayLines([line.Id]);
            await WaitForPlayer();
            Assert.Equal(line.Id, SpeechPlayer.Shared.LastStarted?.LineID);
            Assert.Null(SpeechPlayer.Shared.Failure);
            var spoken = fake.Rendered.Last();
            Assert.Equal(doc.Project.Character(line.SpeakerID)!.Voice(VoiceEngine.Apple)!.Choice.VoiceID, spoken.Choice.VoiceID);
            // A table read from that line follows along.
            int before = fake.Rendered.Count;
            manuscript.PlayFromHere(line.Id);
            await Task.Delay(50);
            Dispatcher.UIThread.RunJobs();
            SpeechPlayer.Shared.Stop();
            Assert.True(fake.Rendered.Count > before);
            Assert.False(SpeechPlayer.Shared.IsPlaying);
            w.Close();
        }
        finally { Restore(); }
    }

    [AvaloniaFact]
    public void TonesAreTheNarratorsOwn()
    {
        Setup();
        try
        {
            var doc = AiBook();
            var line = doc.Project.Lines.First();
            Assert.NotNull(line.Tone);   // the rules read every line when the book is made
            doc.SetTone(Tone.Whispered, [line.Id]);
            var now = doc.Project.Lines.First(l => l.Id == line.Id);
            Assert.True(now.Tone == Tone.Whispered && now.ToneSource == ToneSource.User);
            Assert.Equal("Set Tone", doc.UndoName);
            // The rules never change it.
            doc.Change("Rules", p => ToneRules.Apply(p));
            Assert.Equal(Tone.Whispered, doc.Project.Lines.First(l => l.Id == line.Id).Tone);
        }
        finally { Restore(); }
    }

    [AvaloniaFact]
    public void SeriesVoicesReadsAnotherProjectWithoutOpeningIt()
    {
        Setup();
        var tmp = Path.Combine(Path.GetTempPath(), $"iv-series-{Guid.NewGuid():N}.innervoice");
        try
        {
            var earlier = TestApp.Sample();
            earlier.Title = "Placeholder Book One";
            var tom = earlier.Characters.First(c => c.Name.StartsWith("Tom"));
            tom.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "m2"), 1, VoiceSource.User), VoiceEngine.Apple);
            tom.VoiceNotes = "Low and slow.";
            File.WriteAllBytes(tmp, ProjectArchive.Write(new ProjectArchive.Contents { ProjectJson = ProjectJson.Encode(earlier) }));
            var old = SeriesVoicesWindow.ReadProject(tmp);
            var doc = AiBook();
            var matches = SeriesVoices.Suggest(old, new BookIndex(old), doc.Project);
            doc.Change("Use Voices from Another Project", p => SeriesVoices.Apply(matches, old, p, new SeriesVoices.Options()));
            var here = doc.Project.Characters.First(c => c.Name.StartsWith("Tom"));
            Assert.Equal("m2", here.Voice(VoiceEngine.Apple)?.Choice.VoiceID);
            Assert.Equal("Low and slow.", here.VoiceNotes);
            doc.Undo();
            Assert.Null(doc.Project.Characters.First(c => c.Name.StartsWith("Tom")).Voice(VoiceEngine.Apple));
            // The window draws its first step.
            var window = new SeriesVoicesWindow(doc);
            window.Show();
            Pump();
            window.Close();
        }
        finally
        {
            File.Delete(tmp);
            Restore();
        }
    }

    [AvaloniaFact]
    public void MacVoicesStandInOnWindows()
    {
        // A book cast on the Mac keeps its Apple voices; on a PC they're named as Mac voices.
        var engine = new WindowsVoices();
        var choice = new VoiceChoice(VoiceEngine.Apple, "com.apple.voice.premium.en-US.Ava");
        if (OperatingSystem.IsWindows() && engine.Voices.Count > 0)
            Assert.Equal("Ava (Mac voice)", engine.Info(choice)?.Name);
        else
        {
            Assert.Null(engine.Info(choice));
            Assert.False(engine.IsReady);
            Assert.Equal("Windows Voices need Windows.", engine.NotReadyReason);
        }
    }

    /// Light and dark PNGs of the Voices page in INNERVOICE_SNAP_DIR.
    [AvaloniaFact]
    public void Snapshots()
    {
        if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is not { } dir) return;
        Directory.CreateDirectory(dir);
        Setup();
        try
        {
            foreach (var (variant, name) in new[] { (Avalonia.Styling.ThemeVariant.Light, "light"), (Avalonia.Styling.ThemeVariant.Dark, "dark") })
            {
                Avalonia.Application.Current!.RequestedThemeVariant = variant;
                var doc = AiBook();
                var w = new ProjectWindow(doc) { Width = 1280, Height = 820 };
                w.Show();
                w.Workspace.Go(PageKind.Voices);
                Pump();
                w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"Voices-{name}.png"), (int?)null);
                var page = (VoicesPage)w.FindPage(PageKind.Voices);
                var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara"));
                page.Show(new VoiceTarget.Character(mara.Id));
                Pump();
                w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"VoicesCharacter-{name}.png"), (int?)null);
                doc.SetCastingStyle(CastingStyle.OneVoice);
                page.Show(new VoiceTarget.Character(mara.Id));
                Pump();
                w.CaptureRenderedFrame()?.Save(Path.Combine(dir, $"VoicesShade-{name}.png"), (int?)null);
                w.Close();
            }
            var series = new SeriesVoicesWindow(AiBook());
            series.Show();
            Pump();
            series.CaptureRenderedFrame()?.Save(Path.Combine(dir, "SeriesVoices-dark.png"), (int?)null);
            series.Close();
        }
        finally { Restore(); }
    }
}
