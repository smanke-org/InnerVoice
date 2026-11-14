using InnerVoice.Core;
using InnerVoice.Core.Import;
using InnerVoice.Core.Speech;

namespace InnerVoice.Core.Tests;

/// A voice engine for tests: a tone whose pitch is the voice's, a tenth of a second per word.
public sealed class FakeEngine(VoiceEngine engine, params VoiceInfo[] voices) : IVoiceEngine
{
    public VoiceEngine Engine => engine;
    public bool IsReady { get; set; } = true;
    public string? NotReadyReason => IsReady ? null : "Not ready.";
    public IReadOnlyList<VoiceInfo> Voices => voices;
    public VoiceInfo? Info(VoiceChoice choice) => voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);
    public bool ShadesPitch { get; set; }
    public bool ShadesPace { get; set; }
    public List<SpeechRequest> Rendered { get; } = [];
    /// How long each line takes (none: at once), for tests of lines made in parallel.
    public Func<SpeechRequest, TimeSpan>? Delay { get; set; }
    /// Fails a line with this before making it (none: never).
    public Func<SpeechRequest, Exception?>? Fail { get; set; }
    /// The most lines being made at the same moment.
    public int MostAtOnce => Volatile.Read(ref most);
    int running, most;

    public async Task<float[]> Render(SpeechRequest request, CancellationToken ct)
    {
        lock (Rendered) Rendered.Add(request);
        if (Fail?.Invoke(request) is { } failure) throw failure;
        int now = Interlocked.Increment(ref running);
        try
        {
            int seen;
            while (now > (seen = Volatile.Read(ref most)) && Interlocked.CompareExchange(ref most, now, seen) != seen) { }
            if (Delay?.Invoke(request) is { } wait) await Task.Delay(wait, ct);
        }
        finally { Interlocked.Decrement(ref running); }
        return Tone(request);
    }

    static float[] Tone(SpeechRequest request)
    {
        int words = Math.Max(1, request.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
        double hz = 200 * (request.Choice.Pitch ?? 1);
        var x = new float[(int)(0.1 * words * SpeechAudio.SampleRate)];
        for (int i = 0; i < x.Length; i++) x[i] = 0.3f * (float)Math.Sin(2 * Math.PI * hz * i / SpeechAudio.SampleRate);
        return x;
    }
}

[Collection("Engines")]
public class SpeechTests : IDisposable
{
    readonly string cache = Path.Combine(Path.GetTempPath(), "iv-speech-" + Guid.NewGuid().ToString("N"));
    readonly FakeEngine system = new(VoiceEngine.Apple,
        new VoiceInfo(VoiceEngine.Apple, "f", "Aria", Gender.Female, Accent: "American", Quality: 2),
        new VoiceInfo(VoiceEngine.Apple, "m", "Guy", Gender.Male, Accent: "American", Quality: 2));

    public SpeechTests()
    {
        VoiceEngineRegistry.Register(system);
        VoiceEngineRegistry.Unregister(VoiceEngine.Elevenlabs);
        EngineSwitches.Shared = new EngineSwitches();
    }

    public void Dispose()
    {
        VoiceEngineRegistry.Unregister(VoiceEngine.Apple);
        try { Directory.Delete(cache, true); } catch { }
    }

    static Project Make(params string[] paragraphs) => ProjectBuilder.Make(
        new ImportedBook("Test", "", [new Chapter("Chapter 1", paragraphs.Select(t => new Paragraph(t, ParagraphStyle.Body, null)).ToList())]), "t.txt");

    static double PitchOf(float[] x)
    {
        int crossings = 0;
        for (int i = 1; i < x.Length; i++) if (x[i - 1] < 0 && x[i] >= 0) crossings++;
        return crossings / SpeechAudio.Seconds(x);
    }

    static float[] Sine(double hz, double seconds) =>
        Enumerable.Range(0, (int)(seconds * SpeechAudio.SampleRate)).Select(i => 0.5f * (float)Math.Sin(2 * Math.PI * hz * i / SpeechAudio.SampleRate)).ToArray();

    [Fact]
    public void WavRoundTripsAndConverts()
    {
        var x = Sine(220, 0.5);
        Assert.Equal(x, WavFile.Read(WavFile.Bytes(x, float32: true)));
        var pcm = WavFile.Read(WavFile.Bytes(x));
        Assert.Equal(x.Length, pcm.Length);
        Assert.True(x.Zip(pcm).All(p => Math.Abs(p.First - p.Second) < 1e-4));
        // 16 kHz stereo comes in as 24 kHz mono at the same pitch.
        var m = new MemoryStream();
        var w = new BinaryWriter(m);
        int rate = 16000, n = rate / 2;
        w.Write("RIFF"u8); w.Write(36 + n * 4); w.Write("WAVE"u8); w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2);
        w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16); w.Write("data"u8); w.Write(n * 4);
        for (int i = 0; i < n; i++) { var s = (short)(16000 * Math.Sin(2 * Math.PI * 300 * i / rate)); w.Write(s); w.Write(s); }
        var converted = WavFile.Read(m.ToArray());
        Assert.InRange(converted.Length, 11990, 12010);
        Assert.InRange(PitchOf(converted), 290, 310);
    }

    [Fact]
    public void TrimLevelAndStretch()
    {
        var x = SpeechAudio.Join([SpeechAudio.Silence(0.5), Sine(200, 1), SpeechAudio.Silence(0.5)]);
        var t = SpeechAudio.Trimmed(x);
        Assert.InRange(SpeechAudio.Seconds(t), 1.0, 1.05);
        var quiet = SpeechAudio.Scaled(Sine(200, 1), 0.01f);
        var level = SpeechAudio.Leveled(quiet);
        Assert.InRange(level.Max(), 0.13f, 0.15f);   // -20 dB RMS of a sine: peak ≈ 0.141
        // Twice as fast: half as long, same pitch.
        var fast = SpeechAudio.TimeStretched(Sine(200, 2), 2);
        Assert.InRange(SpeechAudio.Seconds(fast), 0.98, 1.02);
        Assert.InRange(PitchOf(fast), 190, 210);
        var slow = SpeechAudio.TimeStretched(Sine(200, 1), 0.5);
        Assert.InRange(SpeechAudio.Seconds(slow), 1.98, 2.02);
        Assert.InRange(PitchOf(slow), 190, 210);
    }

    [Fact]
    public void ShadeMovesPitchAndPace()
    {
        var up = ShadeAudio.Apply(Sine(200, 1), 12, 0);
        Assert.InRange(PitchOf(up), 390, 410);
        Assert.InRange(SpeechAudio.Seconds(up), 0.97, 1.03);
        var slower = ShadeAudio.Apply(Sine(200, 1), 0, -2);
        Assert.InRange(SpeechAudio.Seconds(slower), 1.16, 1.22);
        Assert.InRange(PitchOf(slower), 190, 210);
        Assert.Equal(1.16, ShadeAudio.PaceRate(2), 6);
    }

    [Fact]
    public async Task RendererCachesAndShadesWhatTheEngineDoesNot()
    {
        var r = new SpeechRenderer(cache);
        var req = new SpeechRequest { Text = "Hello there", Choice = new VoiceChoice(VoiceEngine.Apple, "f") };
        var a = await r.Render(req);
        var b = await r.Render(req);
        Assert.Equal(a, b);
        Assert.Single(system.Rendered);
        Assert.True(r.IsCached(req));
        // A shaded request is its own audio, pitched by the renderer when the engine can't.
        var shaded = req with { Shade = Shade.Make(pitch: 4) };   // the most a shade moves: 4 semitones
        Assert.NotEqual(req.CacheKey, shaded.CacheKey);
        Assert.Equal(req.CacheKey, (req with { Shade = Shade.Make() }).CacheKey);
        var s = await r.Render(shaded);
        Assert.InRange(PitchOf(s), 240, 265);
        // An engine that isn't there says so in words.
        var missing = await Assert.ThrowsAsync<SpeechException>(() => r.Render(req with { Choice = new VoiceChoice(VoiceEngine.Orpheus, "tara") }));
        Assert.Contains("Orpheus isn’t downloaded yet", missing.Message);
        Assert.True(r.CacheSize > 0);
        r.ClearCache();
        Assert.False(r.IsCached(req));
    }

    [Fact]
    public void CacheKeysMatchTheMac()
    {
        // The Mac's SHA-256 over "v1␟apple␟f␟␟1.0␟1.0␟neutral␟narration␟Hello".
        var req = new SpeechRequest { Text = "Hello", Choice = new VoiceChoice(VoiceEngine.Apple, "f") };
        var parts = string.Join('\u001F', "v1", "apple", "f", "", "1.0", "1.0", "neutral", "narration", "Hello");
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(parts))), req.CacheKey);
        Assert.NotEqual(req.CacheKey, (req with { Choice = new VoiceChoice(VoiceEngine.Apple, "f", rate: 0.92) }).CacheKey);
    }

    [Fact]
    public void ResolverGivesEachLineItsVoice()
    {
        var p = Make("“Hello,” said Mara. The rain fell.", "“Hush,” said Tom.");
        var index = new BookIndex(p);
        VoiceCasting.Cast(p, index, VoiceEngine.Apple, system.Voices);
        var mara = p.Characters.First(c => c.Name == "Mara");
        mara.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "f"), 1, VoiceSource.User), VoiceEngine.Apple);
        var resolver = new VoiceResolver(p, index: index);
        Assert.Equal(VoiceEngine.Apple, resolver.Engine);
        var segs = SpeechScript.Segments(0, p, index, includeNarration: true);
        var hello = segs.First(s => s.Text == "Hello");
        Assert.Equal("f", resolver.ChoiceFor(hello)?.VoiceID);
        var rain = segs.First(s => s.Text == "The rain fell.");
        Assert.Equal(resolver.Narrator, resolver.ChoiceFor(rain));
        Assert.Equal(Tone.Neutral, resolver.RequestFor(rain)!.Tone);
        // Delivery hints never reach an engine but ElevenLabs.
        Assert.Null(resolver.RequestFor(hello with { Delivery = "softly" })!.Delivery);
        // "Same as Narrator".
        mara.SetVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, VoiceChoice.NarratorID), 1, VoiceSource.User), VoiceEngine.Apple);
        Assert.Equal(new VoiceResolver(p, index: index).Narrator, new VoiceResolver(p, index: index).ChoiceFor(hello));
        Assert.Equal("Same as Narrator", VoiceNames.Name(mara.Voice(VoiceEngine.Apple)!.Choice));
        Assert.Equal("Aria", VoiceNames.Name(new VoiceChoice(VoiceEngine.Apple, "f")));
    }

    [Fact]
    public void OneVoiceShadesTheNarratorForCharacters()
    {
        var p = Make("“Hello,” said Mrs. Hale.", "“Hush,” said Mr. Penrose.");
        p.Settings.CastingStyle = CastingStyle.OneVoice;
        p.Settings.SetNarratorVoice(new CastVoice(new VoiceChoice(VoiceEngine.Apple, "m"), 1, VoiceSource.User), VoiceEngine.Apple);
        var index = new BookIndex(p);
        var resolver = new VoiceResolver(p, index: index);
        var segs = SpeechScript.Segments(0, p, index, includeNarration: false);
        var woman = resolver.RequestFor(segs.First(s => s.Text == "Hello"))!;
        Assert.Equal("m", woman.Choice.VoiceID);
        Assert.True(woman.Shade!.Pitch > 0);   // a man's voice goes higher for a woman
        Assert.Null(resolver.RequestFor(segs.First(s => s.Text == "Hush"))!.Shade?.Cue);
    }

    [Fact]
    public void SwitchesPickTheEngineInUse()
    {
        var p = Make("“Hello,” said Mara.");
        var cloud = new FakeEngine(VoiceEngine.Elevenlabs, new VoiceInfo(VoiceEngine.Elevenlabs, "e1", "Rachel", Gender.Female)) { IsReady = false };
        VoiceEngineRegistry.Register(cloud);
        try
        {
            var sw = new EngineSwitches();
            Assert.Equal([VoiceEngine.Apple], sw.Choices);
            p.Settings.VoiceEngine = VoiceEngine.Elevenlabs;
            Assert.Equal(VoiceEngine.Apple, sw.InUse(p));   // not set up
            cloud.IsReady = true;
            Assert.Equal(VoiceEngine.Apple, sw.InUse(p));   // the book hasn't allowed it
            p.Settings.CloudVoicesAllowed = AppleDate.Now;
            Assert.Equal(VoiceEngine.Elevenlabs, sw.InUse(p));
            Assert.Equal([VoiceEngine.Apple, VoiceEngine.Elevenlabs, VoiceEngine.Mixed], sw.Choices);
            Assert.False(sw.CanTurnOff(VoiceEngine.Apple) && sw.Enabled.Count < 2);
            sw.Set(VoiceEngine.Elevenlabs, false);
            Assert.Equal(VoiceEngine.Apple, sw.InUse(p));
            sw.Set(VoiceEngine.Apple, false);   // the last engine stays on
            Assert.True(sw.IsOn(VoiceEngine.Apple));
            p.Settings.VoiceEngine = VoiceEngine.Mixed;
            sw.Set(VoiceEngine.Elevenlabs, true);
            Assert.Equal(3, sw.VoicesFor(VoiceEngine.Mixed, p).Count);
        }
        finally { VoiceEngineRegistry.Unregister(VoiceEngine.Elevenlabs); }
    }
}
