using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;
using Xunit;

namespace InnerVoice.App.Tests;

/// Pocket TTS, Qwen3-TTS and Orpheus in the app: Settings › Voices (downloads, the hardware check,
/// the recommended specs, where Qwen3-TTS comes from) and Clone a Voice › On This PC, with fake engines and a fake
/// recognizer. The models themselves are tested in the Core tests.
[Collection("Voice engines")]
public class LocalVoicesAppTests
{
    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static List<string> Texts(Window w) =>
        w.GetLogicalDescendants().Select(c => c switch
        {
            TextBlock t => t.Text,
            Button { Content: string s } => s,
            ContentControl { Content: TextBlock t } => t.Text,
            _ => null,
        }).OfType<string>().ToList();

    static readonly VoiceHardware Weak = new(8, null, 0, 0, "Test Processor", 8, "x64");
    static readonly VoiceHardware Strong = new(32, "Test Graphics RTX", 12, 0, "Test Processor", 16, "x64");

    [AvaloniaFact]
    public void SettingsShowTheModelsAndWhatThisPCHas()
    {
        VoiceSetup.Start();
        var models = VoiceModels.Shared;
        try
        {
            models.Use(Weak);
            Assert.False(Onnx.PreferGpu);
            var settings = SettingsWindow.Show(null, "Voices");
            Pump();
            var all = string.Join("\n", Texts(settings));
            Assert.Contains("Pocket TTS", all);
            Assert.Contains("Qwen3-TTS", all);
            Assert.Contains("Orpheus", all);
            Assert.Contains($"Download ({VoicePacks.Pocket.SizeDescription})", all);
            Assert.Contains($"Download ({VoicePacks.Qwen.SizeDescription})", all);
            Assert.Equal("8.8 GB", VoicePacks.Qwen.SizeDescription);
            // Qwen3-TTS says where it comes from and that it stays on this PC (the user's wording).
            Assert.Contains("Developed in China · Runs on this PC", all);
            Assert.Contains("Qwen3-TTS comes from Alibaba’s Qwen team in China. It runs entirely on this PC from open model files (Apache 2.0): your book’s text and the audio never leave the PC, and it never connects to Alibaba or any server in China. The only network use is the one-time model download from Hugging Face.", all);
            Assert.DoesNotContain("Old Voice Files", all);
            Assert.Contains($"Download ({VoicePacks.Orpheus.SizeDescription})", all);
            Assert.DoesNotContain("Coming later", all);
            // What this PC has, the plain warning, and what makes it better.
            Assert.Contains("This PC", all);
            Assert.Contains("Memory: 8 GB", all);
            Assert.Contains("no DirectX 12 graphics card", all);
            Assert.Contains("Windows Voices are fast on any PC", all);
            Assert.Contains("RTX 3060", all);

            // A download in progress, then one that failed.
            models.States[VoiceEngine.Pocket] = new VoiceModels.State.Downloading(0.5, 4_000_000_000, false);
            models.Use(Weak);
            Pump();
            all = string.Join("\n", Texts(settings));
            Assert.Contains($"50% of {VoicePacks.Pocket.SizeDescription}", all);
            Assert.Contains("Cancel", all);
            models.States[VoiceEngine.Pocket] = new VoiceModels.State.Failed("Unable to download: check the internet connection, then try again.");
            models.Use(Weak);
            Pump();
            Assert.Contains("Unable to download", string.Join("\n", Texts(settings)));
            models.States.Remove(VoiceEngine.Pocket);

            // A strong PC: no warning, and the graphics card is used.
            models.Use(Strong);
            Pump();
            all = string.Join("\n", Texts(settings));
            Assert.Contains("Test Graphics RTX, 12 GB of video memory", all);
            Assert.DoesNotContain("Windows Voices are fast on any PC", all);
            Assert.True(Onnx.PreferGpu);
            settings.Close();
        }
        finally
        {
            models.States.Clear();
            models.Use(Weak);
            Onnx.PreferGpu = false;
        }
    }

    /// Qwen3-TTS's files are live again: Settings never offers them for removal (Remove Old
    /// Voice Files is gone), even with the models and samples on disk.
    [AvaloniaFact]
    public void QwenFilesAreNotOfferedForRemoval()
    {
        VoiceSetup.Start();
        var folder = VoicePacks.Folder;
        Assert.Contains("(test)", folder);
        var qwen = Path.Combine(folder, "qwen", "design");
        Directory.CreateDirectory(qwen);
        File.WriteAllBytes(Path.Combine(qwen, "talker.onnx"), new byte[1_000]);
        Directory.CreateDirectory(VoicePacks.SamplesFolder);
        File.WriteAllBytes(Path.Combine(VoicePacks.SamplesFolder, "a.wav"), new byte[1_000]);
        try
        {
            var settings = SettingsWindow.Show(null, "Voices");
            Pump();
            var all = string.Join("\n", Texts(settings));
            Assert.DoesNotContain("Old Voice Files", all);
            Assert.DoesNotContain("Recycle Bin", all);
            settings.Close();
            Assert.True(Directory.Exists(qwen));
        }
        finally
        {
            if (Directory.Exists(Path.Combine(folder, "qwen"))) Directory.Delete(Path.Combine(folder, "qwen"), true);
            if (Directory.Exists(VoicePacks.SamplesFolder)) Directory.Delete(VoicePacks.SamplesFolder, true);
        }
    }

    /// A book saved with Qwen3-TTS opens as Qwen3-TTS, unchanged and not marked edited.
    [AvaloniaFact]
    public void OpeningAQwenBookKeepsQwen()
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"iv-qwen-{Guid.NewGuid():N}.innervoice");
        File.Copy(Books.Fixture("sample-windows.innervoice"), tmp);
        try
        {
            var doc = ProjectDocument.Open(tmp);
            Assert.False(doc.IsDirty);
            Assert.Equal(VoiceEngine.Qwen, doc.Project.Settings.VoiceEngine);
            Assert.Contains(doc.Project.Characters, c => c.Voice(VoiceEngine.Qwen)?.Choice.VoiceID == "design");
            Assert.All(doc.Project.Characters, c => Assert.Null(c.Voice(VoiceEngine.Pocket)));
            // A Pocket TTS book opens as it is too.
            var mac = Path.Combine(Path.GetTempPath(), $"iv-pocket-{Guid.NewGuid():N}.innervoice");
            File.Copy(Books.Fixture("sample-mac.innervoice"), mac);
            Assert.False(ProjectDocument.Open(mac).IsDirty);
            File.Delete(mac);
        }
        finally { File.Delete(tmp); }
    }

    /// Qwen3-TTS, downloaded.
    sealed class FakeQwen : IVoiceEngine
    {
        public VoiceEngine Engine => VoiceEngine.Qwen;
        public bool IsReady => true;
        public string? NotReadyReason => null;
        public IReadOnlyList<VoiceInfo> Voices => LocalClones.Shared.All.Select(c => LocalClones.Info(c, VoiceEngine.Qwen)).Concat(VoiceInfo.QwenPresets).ToList();
        public VoiceInfo? Info(VoiceChoice choice) => Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);
        public bool ShadesPitch => true;
        public bool ShadesPace => false;
        public Task<float[]> Render(SpeechRequest request, CancellationToken ct) => Task.FromResult(Tone(150, 0.5));
    }

    /// In a book that speaks with Qwen3-TTS, a voice cloned on this PC is a Qwen3-TTS voice
    /// (the same clip Pocket TTS would speak from).
    [AvaloniaFact]
    public async Task CloningInAQwenBookGivesAQwenVoice()
    {
        VoiceSetup.Start();
        var clones = LocalClones.Shared;
        LocalClones.Shared = new LocalClones(Directory.CreateTempSubdirectory("iv-clones-").FullName);
        VoiceEngineRegistry.Register(new FakeQwen());
        EngineSwitches.Shared = new EngineSwitches();
        var wav = Path.Combine(Path.GetTempPath(), $"iv-recording-{Guid.NewGuid():N}.wav");
        WavFile.Write(wav, Tone(180, 12));
        var words = new List<CloneClip.Word>();
        var transcript = "";
        for (double t = 0.3; t + 0.4 < 12; t += 0.5)
        {
            transcript += (transcript.Length > 0 ? " " : "") + "word";
            words.Add(new CloneClip.Word(t, t + 0.4, transcript.Length));
        }
        LocalCloning.TestTranscriber = _ => (transcript, words);
        try
        {
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Purpose", p => { p.Settings.Purpose = ProjectPurpose.AiVoices; p.Settings.VoiceEngine = VoiceEngine.Qwen; });
            Assert.Equal(VoiceEngine.Qwen, doc.Project.EngineInUse());
            var who = doc.Project.Characters.First(c => (doc.Index.StatsFor(c.Id)?.Lines ?? 0) > 0);
            var window = new CloneVoiceWindow(doc);
            window.Show();
            Pump();
            Assert.Contains("Qwen3-TTS learns the voice", string.Join("\n", Texts(window)));
            window.AddRecordings([wav]);
            await window.Preparing!;
            Pump();
            window.Prepare("Placeholder Voice", rights: true, useFor: who.Id);
            await window.Clone();
            Pump();
            var c = Assert.Single(LocalClones.Shared.All);
            var voice = doc.Project.Character(who.Id)!.Voice(VoiceEngine.Qwen)?.Choice;
            Assert.Equal(new VoiceChoice(VoiceEngine.Qwen, c.VoiceID), voice);
            Assert.True(LocalClones.IsClone(voice!));
            window.Close();
        }
        finally
        {
            LocalCloning.TestTranscriber = null;
            LocalClones.Shared = clones;
            VoiceEngineRegistry.Register(new QwenEngine());
            File.Delete(wav);
        }
    }

    /// Pocket TTS, downloaded, speaking a tone.
    sealed class FakePocket : IVoiceEngine
    {
        public VoiceEngine Engine => VoiceEngine.Pocket;
        public bool IsReady => true;
        public string? NotReadyReason => null;
        public IReadOnlyList<VoiceInfo> Voices => LocalClones.Shared.All.Select(LocalClones.Info).Concat(VoiceInfo.Pocket).ToList();
        public VoiceInfo? Info(VoiceChoice choice) => Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);
        public bool ShadesPitch => true;
        public bool ShadesPace => true;
        public Task<float[]> Render(SpeechRequest request, CancellationToken ct) => Task.FromResult(Tone(150, 0.5));
    }

    static float[] Tone(double hz, double seconds)
    {
        var x = new float[(int)(seconds * SpeechAudio.SampleRate)];
        for (int i = 0; i < x.Length; i++) x[i] = 0.3f * MathF.Sin(2 * MathF.PI * (float)hz * i / SpeechAudio.SampleRate);
        return x;
    }

    [AvaloniaFact]
    public async Task CloningOnThisPCFindsTheWordsAndKeepsTheVoiceHere()
    {
        VoiceSetup.Start();
        var clones = LocalClones.Shared;
        LocalClones.Shared = new LocalClones(Directory.CreateTempSubdirectory("iv-clones-").FullName);
        VoiceEngineRegistry.Register(new FakePocket());
        EngineSwitches.Shared = new EngineSwitches();
        var wav = Path.Combine(Path.GetTempPath(), $"iv-recording-{Guid.NewGuid():N}.wav");
        // 20 seconds of a voice: the clip is cut at the long pause after 9 seconds.
        WavFile.Write(wav, Tone(180, 20));
        var words = new List<CloneClip.Word>();
        var transcript = "";
        for (double t = 0.3; t + 0.4 < 20; t += 0.5)
        {
            transcript += (transcript.Length > 0 ? " " : "") + "word";
            words.Add(new CloneClip.Word(t, t + 0.4, transcript.Length));
            if (Math.Abs(t - 8.8) < 0.01) t += 0.6; // a pause
        }
        var heard = new List<int>();
        LocalCloning.TestTranscriber = audio => { heard.Add(audio.Length); return (transcript, words); };
        try
        {
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Purpose", p => { p.Settings.Purpose = ProjectPurpose.AiVoices; p.Settings.VoiceEngine = VoiceEngine.Pocket; });
            Assert.Equal(VoiceEngine.Pocket, doc.Project.EngineInUse());
            var who = doc.Project.Characters.First(c => (doc.Index.StatsFor(c.Id)?.Lines ?? 0) > 0);

            var window = new CloneVoiceWindow(doc);
            window.Show();
            Pump();
            var all = string.Join("\n", Texts(window));
            Assert.Contains("The recordings never leave this PC. The words in them are found on this PC too.", all);
            Assert.DoesNotContain("Remove background noise", all);

            window.AddRecordings([wav]);
            await window.Preparing!;
            Pump();
            var clip = window.PreparedClip!;
            Assert.True(clip.Recognized);
            Assert.InRange(clip.Seconds, CloneClip.Shortest, CloneClip.Longest);
            Assert.InRange(clip.Seconds, 8.5, 9.6);
            Assert.StartsWith("word word", window.ClipText);
            Assert.True(window.ClipText.Length < transcript.Length);
            Assert.True(heard.Single() <= 30 * SpeechAudio.SampleRate);
            Assert.Contains("Play Clip", string.Join("\n", Texts(window)));

            // Nothing is saved without the rights box.
            window.Prepare("Placeholder Voice", rights: false, useFor: who.Id);
            await window.Clone();
            Assert.Empty(LocalClones.Shared.All);
            window.Prepare("Placeholder Voice", rights: true, useFor: who.Id);
            await window.Clone();
            Pump();
            var c = Assert.Single(LocalClones.Shared.All);
            Assert.Equal("Placeholder Voice", c.Name);
            Assert.Equal(window.ClipText, c.Text);
            Assert.Equal(c.VoiceID, doc.Project.Character(who.Id)!.Voice(VoiceEngine.Pocket)?.Choice.VoiceID);
            Assert.Contains("“Placeholder Voice” is ready", string.Join("\n", Texts(window)));
            window.Close();

            // Without a recognizer the narrator types the words.
            LocalCloning.TestTranscriber = _ => null;
            var again = new CloneVoiceWindow(doc);
            again.Show();
            again.AddRecordings([wav]);
            await again.Preparing!;
            Pump();
            Assert.False(again.PreparedClip!.Recognized);
            Assert.Equal(CloneClip.Longest, again.PreparedClip.Seconds, 1);
            Assert.Contains("Type exactly what the clip says", string.Join("\n", Texts(again)));
            again.Prepare("Placeholder Two", rights: true);
            await again.Clone();
            Assert.Single(LocalClones.Shared.All); // no words yet
            again.ClipText = "The words, typed.";
            await again.Clone();
            Assert.Equal(2, LocalClones.Shared.All.Count);
            again.Close();
        }
        finally
        {
            LocalCloning.TestTranscriber = null;
            LocalClones.Shared = clones;
            VoiceEngineRegistry.Register(new PocketEngine());
            File.Delete(wav);
        }
    }
}
