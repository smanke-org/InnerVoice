using System.IO.Compression;
using System.Text.RegularExpressions;
using InnerVoice.Core;
using InnerVoice.Core.Import;
using InnerVoice.Core.Speech;

namespace InnerVoice.Core.Tests;

/// Stands in for Media Foundation: the "M4A" is the WAV's bytes.
public sealed class CopyEncoder : IAudioEncoder
{
    public int Calls;
    public Task EncodeM4a(string wavPath, string m4aPath, CancellationToken ct)
    {
        Interlocked.Increment(ref Calls);
        File.Copy(wavPath, m4aPath, overwrite: true);
        return Task.CompletedTask;
    }
}

[Collection("Engines")]
public class AudioExportTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "iv-export-" + Guid.NewGuid().ToString("N"));
    readonly SpeechRenderer renderer;
    readonly FakeEngine system = new(VoiceEngine.Apple,
        new VoiceInfo(VoiceEngine.Apple, "f", "Aria", Gender.Female, Accent: "American", Quality: 2),
        new VoiceInfo(VoiceEngine.Apple, "m", "Guy", Gender.Male, Accent: "American", Quality: 2));

    public AudioExportTests()
    {
        VoiceEngineRegistry.Register(system);
        VoiceEngineRegistry.Unregister(VoiceEngine.Elevenlabs);
        EngineSwitches.Shared = new EngineSwitches();
        renderer = new SpeechRenderer(Path.Combine(root, "cache"));
    }

    public void Dispose()
    {
        VoiceEngineRegistry.Unregister(VoiceEngine.Apple);
        try { Directory.Delete(root, true); } catch { }
    }

    static Project Book()
    {
        var chapters = new List<Chapter>
        {
            new("Chapter 1", [new Paragraph("Chapter 1", ParagraphStyle.Heading, null),
                              new Paragraph("“Hello there,” said Mara. The rain fell.", ParagraphStyle.Body, null),
                              new Paragraph("“Hush now,” said Tom.", ParagraphStyle.Body, null)]),
            new("Chapter 2", [new Paragraph("Chapter 2", ParagraphStyle.Heading, null),
                              new Paragraph("“Again,” said Mara.", ParagraphStyle.Body, null)]),
        };
        var p = ProjectBuilder.Make(new ImportedBook("Placeholder Book", "A. Writer", chapters), "t.txt");
        var index = new BookIndex(p);
        foreach (var c in p.Characters)
            c.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, c.Name == "Mara" ? "f" : "m"), 1, VoiceSource.User), VoiceEngine.Apple);
        p.Settings.SetNarratorVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "m", pitch: 0.8), 1, VoiceSource.User), VoiceEngine.Apple);
        return p;
    }

    [Fact]
    public async Task TimingsAddUpWithThePauses()
    {
        var p = Book();
        var index = new BookIndex(p);
        var resolver = new VoiceResolver(p, index: index);
        var pauses = PauseSettings.Standard;
        var segs = SpeechScript.Segments(0, p, index, includeNarration: true, pauses: pauses);
        var timings = new List<(SpeechSegment Seg, double Begin, double End)>();
        var progress = new List<int>();
        var folder = Path.Combine(root, "out");
        var files = await AudioExporter.Write([new AudioPart("Placeholder Book - Chapter 1", segs)], folder, AudioFormat.Wav, resolver, renderer,
            timing: (_, s, b, e) => timings.Add((s, b, e)), progress: (d, _, _) => progress.Add(d), pauses: pauses);
        var file = Assert.Single(files);
        Assert.Equal(Path.Combine(folder, "Placeholder Book - Chapter 1.wav"), file.Path);
        Assert.Equal(segs.Count, timings.Count);
        // Back to back, each followed by its own pause.
        for (int i = 1; i < timings.Count; i++)
            Assert.Equal(timings[i - 1].End + pauses.Seconds(timings[i - 1].Seg.Pause), timings[i].Begin, 3);
        Assert.Equal(timings[^1].End + pauses.Chapter, file.Seconds, 3);
        Assert.Equal(segs.Count, progress[^1]);
        var audio = WavFile.Read(file.Path);   // back at 24 kHz
        Assert.Equal(file.Seconds, SpeechAudio.Seconds(audio), 2);
        Assert.Empty(Directory.GetFiles(folder, "*.part"));
    }

    [Fact]
    public async Task SeparateTracksLineUp()
    {
        var p = Book();
        var index = new BookIndex(p);
        var resolver = new VoiceResolver(p, index: index);
        var segs = SpeechScript.Segments(0, p, index, includeNarration: true);
        var stem = AudioExporter.Stems(p, index, resolver);
        Assert.Equal("Narrator", stem(segs.First(s => s.Text == "The rain fell.")));
        Assert.Equal("Mara", stem(segs.First(s => s.Text == "Hello there")));
        var timing = new Dictionary<string, (double, double)>();
        var files = await AudioExporter.Write([new AudioPart("Ch", segs)], Path.Combine(root, "sep"), AudioFormat.Wav, resolver, renderer,
            stem: stem, timing: (_, s, b, e) => timing[s.Id] = (b, e));
        Assert.Equal(["Ch - Narrator.wav", "Ch - Mara.wav", "Ch - Tom.wav"], files.Select(f => Path.GetFileName(f.Path)));
        var tracks = files.Select(f => WavFile.Read(f.Path)).ToList();
        Assert.All(tracks, t => Assert.Equal(tracks[0].Length, t.Length));
        // Mara's track is silent while the narrator reads, and speaks during her line.
        var (b0, e0) = timing[segs.First(s => s.Text == "The rain fell.").Id];
        var (b1, e1) = timing[segs.First(s => s.Text == "Hello there").Id];
        float Loudness(float[] x, double from, double to) =>
            x.Skip((int)(from * SpeechAudio.SampleRate) + 50).Take((int)((to - from) * SpeechAudio.SampleRate) - 100).Select(Math.Abs).DefaultIfEmpty().Max();
        Assert.True(Loudness(tracks[1], b0, e0) < 0.001f);
        Assert.True(Loudness(tracks[1], b1, e1) > 0.1f);
        Assert.True(Loudness(tracks[0], b1, e1) < 0.001f);
    }

    [Fact]
    public async Task RecapsAreSpokenAfterTheTitle()
    {
        var p = Book();
        p.Chapters[1].Recap = "Mara said hello. Tom hushed her.";
        var index = new BookIndex(p);
        var resolver = new VoiceResolver(p, index: index);
        var parts = AudioExporter.Parts(p, index, [0, 1], narration: true, recaps: true, perChapter: true, resolver);
        Assert.Equal(["01 Chapter 1", "02 Chapter 2"], parts.Select(x => x.Name));
        Assert.Equal(["Chapter 2", "Previously…", "Mara said hello.", "Tom hushed her.", "Again", "said Mara."], parts[1].Segments.Select(s => s.Text));
        var spoken = new List<string>();
        await AudioExporter.Write(parts, Path.Combine(root, "recaps"), AudioFormat.Wav, resolver, renderer, timing: (_, s, _, _) => spoken.Add(s.Text));
        Assert.Contains("Previously…", spoken);
        // Read by the narrator.
        Assert.Contains(system.Rendered, r => r.Text == "Previously…" && r.Choice == resolver.Narrator);
        // Without recaps, or for one character, none.
        Assert.DoesNotContain(AudioExporter.Parts(p, index, [1], true, false, true, resolver)[0].Segments, s => s.Text == "Previously…");
        var mara = p.Characters.First(c => c.Name == "Mara").Id;
        var hers = AudioExporter.Parts(p, index, [0, 1], true, true, true, resolver, mara);
        Assert.Equal("Placeholder Book - Mara", Assert.Single(hers).Name);
        Assert.Equal(["Hello there", "Again"], hers[0].Segments.Select(s => s.Text));
        var narrator = AudioExporter.Parts(p, index, [1], true, true, true, resolver, AudioExporter.NarratorOnly);
        Assert.Equal("Placeholder Book - Narrator, Chapter 2", narrator[0].Name);
        // Every chapter in one file: the book's title alone.
        Assert.Equal("Placeholder Book", Assert.Single(AudioExporter.Parts(p, index, [1, 0], true, false, false, resolver)).Name);
    }

    [Fact]
    public async Task M4aGoesThroughTheEncoder()
    {
        var p = Book();
        var index = new BookIndex(p);
        var resolver = new VoiceResolver(p, index: index);
        var part = new AudioPart("Ch:1?", SpeechScript.Segments(1, p, index, includeNarration: true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AudioExporter.Write([part], Path.Combine(root, "m"), AudioFormat.M4a, resolver, renderer));
        var encoder = new CopyEncoder();
        var files = await AudioExporter.Write([part], Path.Combine(root, "m"), AudioFormat.M4a, resolver, renderer, encoder);
        Assert.Equal("Ch-1-.m4a", Path.GetFileName(Assert.Single(files).Path));
        Assert.Equal(1, encoder.Calls);
        // Stopped: nothing half-written is left.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AudioExporter.Write([part], Path.Combine(root, "stopped"), AudioFormat.Wav, resolver, renderer, ct: cts.Token));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "stopped")));
    }

    [Fact]
    public async Task EpubWithAudioHasTimedOverlays()
    {
        var p = Book();
        var index = new BookIndex(p);
        var resolver = new VoiceResolver(p, index: index);
        var path = Path.Combine(root, "Placeholder Book.epub");
        await EpubAudioExport.Write(p, index, [1, 0], path, resolver, new CopyEncoder(), renderer);
        using var zip = ZipFile.OpenRead(path);
        Assert.Equal("mimetype", zip.Entries[0].FullName);
        Assert.NotNull(zip.GetEntry("OEBPS/audio/c001.m4a"));
        Assert.NotNull(zip.GetEntry("OEBPS/audio/c002.m4a"));
        using var r = new StreamReader(zip.GetEntry("OEBPS/smil/c001.smil")!.Open());
        var smil = r.ReadToEnd();
        var begins = Regex.Matches(smil, "clipBegin=\"([0-9:.]+)\"").Select(m => TimeSpan.Parse(m.Groups[1].Value)).ToList();
        var ends = Regex.Matches(smil, "clipEnd=\"([0-9:.]+)\"").Select(m => TimeSpan.Parse(m.Groups[1].Value)).ToList();
        Assert.True(begins.Count >= 3);
        Assert.Equal(begins.Order(), begins);
        Assert.All(begins.Zip(ends), x => Assert.True(x.Second > x.First));
        using var page = new StreamReader(zip.GetEntry("OEBPS/text/c001.xhtml")!.Open());
        Assert.Contains("Hello there", page.ReadToEnd());
    }
}
