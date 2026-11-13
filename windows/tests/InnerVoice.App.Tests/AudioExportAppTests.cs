using System.IO.Compression;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using InnerVoice.App.Document;
using InnerVoice.App.Export;
using InnerVoice.App.Speech;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Tests;

/// File › Export Audio… and File › Export EPUB with Audio…, with a fake voice engine (a tone
/// per word) standing in for Windows' voices, and a fake M4A encoder.
[Collection("Voice engines")]
public class AudioExportAppTests
{
    sealed class ToneEngine : IVoiceEngine
    {
        static readonly VoiceInfo[] voices =
        [
            new(VoiceEngine.Apple, "f", "Aria", Gender.Female, Accent: "American", Quality: 2),
            new(VoiceEngine.Apple, "m", "Guy", Gender.Male, Accent: "American", Quality: 2),
            new(VoiceEngine.Apple, "n", "Sky", Gender.Neutral, Accent: "American", Quality: 3),
        ];
        public VoiceEngine Engine => VoiceEngine.Apple;
        public bool IsReady => true;
        public string? NotReadyReason => null;
        public IReadOnlyList<VoiceInfo> Voices => voices;
        public VoiceInfo? Info(VoiceChoice choice) => voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);
        public bool ShadesPitch => false;
        public bool ShadesPace => false;
        public Task<float[]> Render(SpeechRequest request, CancellationToken ct)
        {
            int words = Math.Max(1, request.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            var x = new float[(int)(0.05 * words * SpeechAudio.SampleRate)];
            for (int i = 0; i < x.Length; i++) x[i] = 0.3f * MathF.Sin(2 * MathF.PI * 220 * i / SpeechAudio.SampleRate);
            return Task.FromResult(x);
        }
    }

    sealed class CopyEncoder : IAudioEncoder
    {
        public Task EncodeM4a(string wavPath, string m4aPath, CancellationToken ct)
        {
            File.Copy(wavPath, m4aPath, overwrite: true);
            return Task.CompletedTask;
        }
    }

    static (ProjectDocument Doc, string Folder, IAudioEncoder? Encoder) Setup()
    {
        VoiceSetup.Start();
        VoiceEngineRegistry.Register(new ToneEngine());
        EngineSwitches.Shared = new EngineSwitches();
        var p = TestApp.Sample();
        p.Settings.Purpose = ProjectPurpose.AiVoices;
        p.Chapters[0].Recap = "Placeholder recap. It is short.";
        var doc = ProjectDocument.Unsaved(p);
        var folder = Path.Combine(Path.GetTempPath(), $"iv-audio-{Guid.NewGuid():N}");
        return (doc, folder, AudioEncoders.M4a);
    }

    static void Restore(string folder, IAudioEncoder? encoder)
    {
        VoiceEngineRegistry.Register(new WindowsVoices());
        AudioEncoders.M4a = encoder;
        try { Directory.Delete(folder, true); } catch { }
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task ExportsWavPerChapterAndSeparate()
    {
        var (doc, folder, encoder) = Setup();
        AudioEncoders.M4a = null;
        try
        {
            AppSettings.Update(s => { s.ExportAudioSeparateVoices = false; s.ExportAudioRecaps = true; s.ReadNarration = true; });
            var w = new AudioExportWindow(doc);
            w.Show();
            Pump();
            // No AAC encoder: WAV only, and the window says why.
            Assert.Equal(AudioFormat.Wav, w.Format);
            var files = await w.Export(folder);
            Assert.NotNull(files);
            Assert.Equal(doc.Project.Chapters.Count, files!.Count);
            Assert.All(files, f => Assert.EndsWith(".wav", f.Path));
            Assert.StartsWith("01 ", Path.GetFileName(files[0].Path));
            var audio = WavFile.Read(files[0].Path);
            Assert.True(SpeechAudio.Seconds(audio) > 1);
            Assert.Empty(Directory.GetFiles(folder, "*.m4a"));
            // The casting the export needed was done first.
            Assert.NotNull(doc.Project.Settings.NarratorVoice(VoiceEngine.Apple));
            w.Close();

            // Separate: a file per voice, all the same length.
            AppSettings.Update(s => s.ExportAudioSeparateVoices = true);
            var separate = Path.Combine(folder, "separate");
            var w2 = new AudioExportWindow(doc);
            w2.Show();
            Pump();
            var tracks = await w2.Export(separate);
            Assert.NotNull(tracks);
            // The chapter with the most voices (an opening chapter may be all narration).
            var first = tracks!.GroupBy(t => t.Part).OrderByDescending(g => g.Count()).First().ToList();
            Assert.True(first.Count > 1);
            Assert.Contains(first, t => t.Voice == "Narrator");
            var lengths = first.Select(t => WavFile.Read(t.Path).Length).Distinct().ToList();
            Assert.Single(lengths);
            w2.Close();
        }
        finally
        {
            AppSettings.Update(s => { s.ExportAudioSeparateVoices = false; s.ExportAudioRecaps = false; });
            Restore(folder, encoder);
        }
    }

    [AvaloniaFact]
    public async Task ExportsAnEpubWithAudio()
    {
        var (doc, folder, encoder) = Setup();
        try
        {
            AudioEncoders.M4a = null;
            var none = new EpubExportWindow(doc);
            none.Show();
            Pump();
            Assert.False(await none.Export(Path.Combine(folder, "none.epub")));
            Assert.False(File.Exists(Path.Combine(folder, "none.epub")));
            none.Close();

            AudioEncoders.M4a = new CopyEncoder();
            var w = new EpubExportWindow(doc);
            w.Show();
            Pump();
            Assert.EndsWith(".epub", w.SuggestedName);
            var path = Path.Combine(folder, w.SuggestedName);
            Assert.True(await w.Export(path));
            using var zip = ZipFile.OpenRead(path);
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            var smil = zip.Entries.Where(e => e.FullName.StartsWith("OEBPS/smil/")).ToList();
            Assert.Equal(doc.Project.Chapters.Count(c => c.Paragraphs.Count > 0), smil.Count);
            using var r = new StreamReader(smil[0].Open());
            var text = r.ReadToEnd();
            Assert.Contains("clipBegin=", text);
            Assert.Contains("clipEnd=", text);
            Assert.NotNull(zip.GetEntry("OEBPS/audio/c001.m4a"));
            w.Close();
        }
        finally { Restore(folder, encoder); }
    }
}
