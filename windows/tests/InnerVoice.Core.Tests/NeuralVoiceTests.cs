using System.Net;
using System.Security.Cryptography;
using System.Text;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

namespace InnerVoice.Core.Tests;

/// The local voices (Qwen3-TTS and Orpheus on ONNX Runtime): everything around the models with
/// fakes, and the real models when INNERVOICE_TEST_VOICE_MODELS names a Voice Models folder
/// (11 GB; VoiceBench --bench uses the same layout).
[Collection("Engines")]
public class NeuralVoiceTests
{
    static float[] Tone(double hz, double seconds, float amp = 0.3f)
    {
        var x = new float[(int)(seconds * SpeechAudio.SampleRate)];
        for (int i = 0; i < x.Length; i++) x[i] = amp * MathF.Sin(2 * MathF.PI * (float)hz * i / SpeechAudio.SampleRate);
        return x;
    }

    static string TempDir() => Directory.CreateTempSubdirectory("iv-neural-").FullName;

    // MARK: Text

    [Fact]
    public void LongTextGoesAFewSentencesAtATime()
    {
        Assert.Equal(["Short and sweet."], SpeechChunks.Chunks("Short and sweet."));
        var sentence = "The rain had not stopped for three days, and the river was rising fast. ";
        var text = string.Concat(Enumerable.Repeat(sentence, 8)).Trim();
        var chunks = SpeechChunks.Chunks(text);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 200, c));
        Assert.All(chunks, c => Assert.EndsWith("fast.", c));
        Assert.Equal(text, string.Join(" ", chunks));
        // A sentence too long on its own breaks after a comma past the middle, or at a word.
        var longOne = string.Join(", ", Enumerable.Repeat("and then the wagon rolled on down the hill", 8)) + ".";
        var parts = SpeechChunks.Chunks(longOne);
        Assert.All(parts, c => Assert.True(c.Length <= 200));
        Assert.EndsWith(",", parts[0]);
        Assert.Equal(["“Run!” she said.", "He ran."], SpeechChunks.Sentences("“Run!” she said. He ran."));
        Assert.Equal(["Mr. Hale left.", "Dr. Moss stayed!", "Why?"], SpeechChunks.Sentences("Mr. Hale left. Dr. Moss stayed! Why?"));
    }

    [Fact]
    public void TheTokenizersSplitAndMapBytes()
    {
        var dir = TempDir();
        // A tiny byte-level vocabulary: "Ġ" is the space byte.
        File.WriteAllText(Path.Combine(dir, "vocab.json"), """{"H":0,"e":1,"l":2,"o":3,"Ġ":4,"w":5,"r":6,"d":7,"He":8,"ll":9,"Hell":10,"Hello":11,"Ġw":12,"Ġwo":13,"!":14}""");
        File.WriteAllText(Path.Combine(dir, "merges.txt"), "#version: 0.2\nH e\nl l\nHe ll\nHell o\nĠ w\nĠw o\n");
        var qwen = BpeTokenizer.Qwen(Path.Combine(dir, "vocab.json"), Path.Combine(dir, "merges.txt"));
        Assert.Equal([151644, 11, 13, 6, 2, 7, 14, 151645], qwen.Encode("<|im_start|>Hello world!<|im_end|>"));
        // Llama 3's tokenizer.json, with ignore_merges: a whole word in the vocabulary is one token.
        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), """
            {"added_tokens":[{"id":100,"content":"<|begin_of_text|>","special":true}],
             "model":{"type":"BPE","ignore_merges":true,"vocab":{"H":0,"e":1,"l":2,"o":3,"Hello":4,"Ġ":5,"1":6,"2":7,"3":8,"4":9,"123":10},"merges":[["H","e"]]}}
            """);
        var llama = BpeTokenizer.FromTokenizerJson(Path.Combine(dir, "tokenizer.json"));
        Assert.Equal([100, 4, 5, 10, 9], llama.Encode("<|begin_of_text|>Hello 1234"));
    }

    // MARK: Sampling

    [Fact]
    public void SamplingIsSeededLikeTheMac()
    {
        // The Mac's seed: the first 8 bytes of the SHA-256, big-endian.
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes("A line.tara"));
        ulong expected = hash.Take(8).Aggregate(0UL, (a, b) => a << 8 | b);
        Assert.Equal(expected, Sampler.Seed("A line.tara"));

        float[] logits = [0, 1, 5, 2, 4];
        Assert.Equal(2, new Sampler(1).Sample(logits, 0));
        var s1 = new Sampler(42); var s2 = new Sampler(42);
        Assert.Equal(Enumerable.Range(0, 20).Select(_ => s1.Sample(logits, 1, 3)), Enumerable.Range(0, 20).Select(_ => s2.Sample(logits, 1, 3)));
        // Top-k 2 only ever picks the two best; top-p 0.5 with one dominant token picks it.
        var s = new Sampler(7);
        Assert.All(Enumerable.Range(0, 200).Select(_ => s.Sample(logits, 1, 2)), t => Assert.Contains(t, new[] { 2, 4 }));
        Assert.All(Enumerable.Range(0, 50).Select(_ => s.Sample([0, 10, 0], 1, 0, 0.5f)), t => Assert.Equal(1, t));
        float[] p = [2, -2, 1];
        Sampler.Penalize(p, [0, 1, 1], 2);
        Assert.Equal([1, -4, 1], p);
    }

    // MARK: Audio

    [Fact]
    public void TheSpeakerEncodersMelMatchesTheReference()
    {
        // The FFT finds a sine in its bin.
        var re = new double[1024]; var im = new double[1024];
        for (int i = 0; i < 1024; i++) re[i] = Math.Sin(2 * Math.PI * 32 * i / 1024);
        Mel.Fft(re, im);
        int peak = Enumerable.Range(0, 512).MaxBy(k => re[k] * re[k] + im[k] * im[k]);
        Assert.Equal(32, peak);
        // Slaney filters: 128 triangles over 513 bins, each with weight, none negative.
        var f = Mel.Filters(24000, 1024, 128, 0, 12000);
        Assert.Equal(128, f.Length);
        Assert.All(f, row => { Assert.Equal(513, row.Length); Assert.True(row.Sum() > 0); Assert.True(row.Min() >= 0); });
        // Frames: reflect-padded by 384 on each side, hop 256.
        var mel = Mel.LogMel(Tone(440, 1), out int frames);
        Assert.Equal(1 + (24000 + 768 - 1024) / 256, frames);
        Assert.Equal(frames * 128, mel.Length);
        // A 440 Hz tone is loudest in the band that holds 440 Hz.
        int loudest = Enumerable.Range(0, 128).MaxBy(b => mel[frames / 2 * 128 + b]);
        int band = Enumerable.Range(0, 128).MaxBy(b => f[b][(int)Math.Round(440 * 1024 / 24000.0)]);
        Assert.InRange(loudest, band - 1, band + 1);
    }

    [Fact]
    public void PitchFindsTheVoiceAndCatchesAContradiction()
    {
        var (voiced, hz) = Pitch.Analyze(Tone(120, 1));
        Assert.True(voiced > 0.9);
        Assert.InRange(hz, 115, 125);
        Assert.InRange(Pitch.Analyze(Tone(240, 1)).Hz, 230, 250);
        Assert.True(Pitch.Contradicts("A man in his forties with a warm, easygoing baritone voice.", Tone(240, 1)));
        Assert.False(Pitch.Contradicts("A man in his forties with a warm, easygoing baritone voice.", Tone(115, 1)));
        Assert.True(Pitch.Contradicts("A young woman with a bright, clear, lively voice.", Tone(110, 1)));
        // Children and descriptions with no gender are never second-guessed.
        Assert.False(Pitch.Contradicts("A young boy with a high, cheerful voice.", Tone(300, 1)));
        Assert.False(Pitch.Contradicts("A calm, clear audiobook narrator.", Tone(300, 1)));
    }

    [Fact]
    public void NumPyArraysAreReadByRow()
    {
        var path = Path.Combine(TempDir(), "m.npy");
        var header = "{'descr': '<f4', 'fortran_order': False, 'shape': (3, 4), }\n";
        using (var f = File.Create(path))
        {
            f.Write([0x93, .. "NUMPY"u8, 1, 0]);
            f.Write(BitConverter.GetBytes((ushort)header.Length));
            f.Write(Encoding.ASCII.GetBytes(header));
            for (int i = 0; i < 12; i++) f.Write(BitConverter.GetBytes((float)i));
        }
        foreach (var inMemory in new[] { false, true })
        {
            using var m = NpyMatrix.Open(path, inMemory);
            Assert.Equal((3, 4), (m.Rows, m.Columns));
            Assert.Equal([4f, 5, 6, 7], m.Row(1));
            var sum = new float[4];
            m.AddRow(2, sum);
            m.AddRow(0, sum);
            Assert.Equal([8f, 10, 12, 14], sum);
        }
    }

    [Fact]
    public void OrpheusCodesBecomeSnacsThreeLevels()
    {
        // Tokens after start-of-speech; each slot k carries code + k × 4096 + 128266.
        var frame = new[] { 1, 2, 3, 4, 5, 6, 7 };
        var tokens = new List<int> { 999, 128257 };
        for (int f = 0; f < 2; f++) tokens.AddRange(frame.Select((c, k) => c + f * 10 + k * 4096 + 128266));
        tokens.Add(128266); // half a frame is dropped
        var codes = OrpheusModel.Codes(tokens);
        Assert.Equal(14, codes.Count);
        var (l1, l2, l3) = OrpheusModel.Levels(codes);
        Assert.Equal([1L, 11], l1);
        Assert.Equal([2L, 5, 12, 15], l2);
        Assert.Equal([3L, 4, 6, 7, 13, 14, 16, 17], l3);
        // A frame with a code out of its slot's range is dropped, not decoded as noise.
        codes[0] = 5000;
        Assert.Single(OrpheusModel.Levels(codes).L1);
        Assert.Equal("<sigh> Well.", OrpheusEngine.Text("Well.", VocalSound.Sigh));
        Assert.Equal("Well.", OrpheusEngine.Text("Well.", null));
    }

    // MARK: Cloning

    [Fact]
    public void CloneClipCutsAtAPause()
    {
        // The Mac's test: words every 0.5 s, with a long pause after the word ending at 9.3 s.
        var words = new List<CloneClip.Word>();
        double t = 0.4;
        int text = 0;
        for (int i = 0; i < 60; i++)
        {
            double end = t + 0.4;
            text += 5;
            words.Add(new CloneClip.Word(t, end, text));
            t = end + (Math.Abs(end - 9.3) < 0.01 ? 0.9 : 0.1);
        }
        var span = CloneClip.Cut(words)!.Value;
        Assert.True(span.Seconds <= CloneClip.Longest);
        Assert.Equal(0.25, span.Start, 3);
        Assert.Equal(9.3, words.First(w => w.TextEnd == span.TextEnd).End, 2);
        var shortOne = words.Take(10).ToList();
        Assert.Equal(shortOne[^1].TextEnd, CloneClip.Cut(shortOne)!.Value.TextEnd);
        Assert.Null(CloneClip.Cut([]));
        Assert.Equal("Hello there.", CloneClip.Text("Hello there. More words", 12));
    }

    [Fact]
    public void ClonesAreKeptOnThisPC()
    {
        var dir = TempDir();
        var store = new LocalClones(dir);
        var c = store.Add("  Ellen  ", Gender.Female, Tone(200, 6), " Every word of it. ");
        Assert.Equal("Ellen", c.Name);
        Assert.Equal("Every word of it.", c.Text);
        Assert.Equal(6, c.Seconds, 2);
        Assert.True(LocalClones.IsClone(new VoiceChoice(VoiceEngine.Qwen, c.VoiceID)));
        Assert.False(LocalClones.IsClone(new VoiceChoice(VoiceEngine.Orpheus, c.VoiceID)));
        var info = LocalClones.Info(c);
        Assert.True(info.Cloned);
        Assert.Empty(VoiceInfo.Castable([info]));
        store.Rename(c.Id, "Ellen Hale");
        // A new store reads the same list and clip back.
        var again = new LocalClones(dir);
        Assert.Equal("Ellen Hale", again.All.Single().Name);
        Assert.Equal(6 * SpeechAudio.SampleRate, again.Sample(c.VoiceID).Audio.Length);
        Assert.Equal("Every word of it.", again.Sample(c.VoiceID).Text);
        again.Delete(c.Id);
        Assert.Empty(new LocalClones(dir).All);
        Assert.Throws<SpeechException>(() => again.Sample(c.VoiceID));
        // Quiet ends are cut, leaving a tenth of a second.
        var padded = new float[24000].Concat(Tone(200, 1)).Concat(new float[24000]).ToArray();
        Assert.InRange(LocalClones.Trim(padded).Length, 24000 + 4000, 24000 + 6000);
    }

    // MARK: The engines, with fake models

    sealed class Fakes : IDisposable
    {
        public readonly List<(string Text, string Description, ulong Seed)> Designed = [];
        public readonly List<(string Text, VoiceSample Sample, ulong Seed)> Cloned = [];
        public readonly List<(string Text, string Voice)> Spoken = [];
        public readonly QwenEngine Qwen = new() { AssumeReady = true };
        public readonly OrpheusEngine Orpheus = new() { AssumeReady = true };
        readonly Func<string, string, ulong, CancellationToken, float[]> designer = QwenSamples.Designer;
        readonly LocalClones clones = LocalClones.Shared;
        public Func<string, float[]> Voice = d => Tone(d.Contains("woman") ? 220 : 115, 4);

        public Fakes()
        {
            VoicePacks.Folder = TempDir();
            QwenSamples.Forget();
            LocalClones.Shared = new LocalClones(TempDir());
            QwenSamples.Designer = (text, description, seed, ct) => { lock (Designed) Designed.Add((text, description, seed)); return Voice(description); };
            Qwen.Cloner = (text, sample, seed, ct) => { lock (Cloned) Cloned.Add((text, sample, seed)); return Tone(150, text.Length / 20.0, 0.05f); };
            Orpheus.Speaker = (text, voice, seed, ct) => { lock (Spoken) Spoken.Add((text, voice)); return Tone(180, 1, 0.05f); };
        }

        public void Dispose()
        {
            QwenSamples.Designer = designer;
            QwenSamples.Forget();
            LocalClones.Shared = clones;
            VoicePacks.Folder = Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)");
        }
    }

    [Fact]
    public async Task QwenSpeaksEveryLineFromOneSamplePerDescriptionAndTone()
    {
        using var f = new Fakes();
        var design = "A woman in her forties with a warm, low, unhurried voice.";
        var choice = new VoiceChoice(VoiceEngine.Qwen, "design", design);
        var spoken = new SpeechRequest { Text = "Where were you?", Choice = choice, Kind = LineKind.Spoken, Tone = Core.Tone.Angry };
        var a = await f.Qwen.Render(spoken, default);
        await f.Qwen.Render(spoken with { Text = "Answer me." }, default);
        // One sample for the description and tone, said in the tone, with the Mac's sample text and seed.
        var d = Assert.Single(f.Designed);
        Assert.Equal(QwenSamples.SampleText, d.Text);
        Assert.Equal(design + " Speaking angry and sharp.", d.Description);
        Assert.Equal(Sampler.Seed(design), d.Seed);
        Assert.True(File.Exists(Path.Combine(VoicePacks.SamplesFolder, QwenSamples.Key(design, Core.Tone.Angry) + ".wav")));
        // Every line is cloned from it, seeded by the line and the description.
        Assert.Equal(2, f.Cloned.Count);
        Assert.Equal(QwenSamples.SampleText, f.Cloned[0].Sample.Text);
        Assert.Equal(Sampler.Seed("Where were you?" + design), f.Cloned[0].Seed);
        // Leveled to -20 dB from a quiet clone.
        Assert.InRange(20 * Math.Log10(Math.Sqrt(a.Average(x => (double)x * x))), -21, -19);
        // Narration is always even: a neutral sample, no tone words.
        await f.Qwen.Render(new SpeechRequest { Text = "She left.", Choice = choice, Tone = Core.Tone.Angry }, default);
        Assert.Equal(design, f.Designed[^1].Description);
        // A thought is a little quieter.
        var thought = await f.Qwen.Render(spoken with { Kind = LineKind.Thought }, default);
        Assert.Equal(0.85, thought.Max() / a.Max(), 2);
    }

    [Fact]
    public async Task QwenRedesignsASampleThatContradictsItsDescription()
    {
        using var f = new Fakes();
        int calls = 0;
        f.Voice = _ => Tone(++calls == 1 ? 240 : 115, 4);
        var design = "A man in his forties with a warm, easygoing baritone voice.";
        await f.Qwen.Render(new SpeechRequest { Text = "Evening.", Choice = new VoiceChoice(VoiceEngine.Qwen, "design", design) }, default);
        Assert.Equal(2, f.Designed.Count);
        Assert.Equal(Sampler.Seed(design + 1), f.Designed[1].Seed);
        Assert.InRange(Pitch.Analyze(f.Cloned[0].Sample.Audio).Hz, 110, 120);
    }

    [Fact]
    public async Task AShadeShiftsTheSampleNotTheLine()
    {
        using var f = new Fakes();
        Assert.True(f.Qwen.ShadesPitch);
        Assert.False(f.Qwen.ShadesPace);
        var choice = VoiceInfo.QwenPresets.First(v => v.VoiceID == "preset:baritone").Choice;
        await f.Qwen.Render(new SpeechRequest { Text = "Evening.", Choice = choice, Shade = Shade.Make(-2) }, default);
        double hz = Pitch.Analyze(f.Cloned[0].Sample.Audio).Hz;
        Assert.InRange(hz, 115 * Math.Pow(2, -2 / 12.0) - 4, 115 * Math.Pow(2, -2 / 12.0) + 4);
        Assert.True(File.Exists(Path.Combine(VoicePacks.SamplesFolder, QwenSamples.Key(choice.Design!, Core.Tone.Neutral) + "-shade-2.wav")));
        // Long text goes a few sentences at a time, 0.18 s apart.
        var text = string.Concat(Enumerable.Repeat("The wagon rolled slowly down the long hill toward the river. ", 6)).Trim();
        f.Cloned.Clear();
        await f.Qwen.Render(new SpeechRequest { Text = text, Choice = choice }, default);
        Assert.True(f.Cloned.Count > 1);
        Assert.Equal(text, string.Join(" ", f.Cloned.Select(c => c.Text)));
    }

    [Fact]
    public async Task AVoiceClonedOnThisPCSpeaksFromItsClip()
    {
        using var f = new Fakes();
        var c = LocalClones.Shared.Add("Ellen", Gender.Female, Tone(210, 6), "What the clip says.");
        var choice = new VoiceChoice(VoiceEngine.Qwen, c.VoiceID);
        Assert.Equal("Ellen", f.Qwen.Info(choice)!.Name);
        Assert.Equal(c.VoiceID, f.Qwen.Voices[0].VoiceID); // clones first
        await f.Qwen.Render(new SpeechRequest { Text = "Hello.", Choice = choice }, default);
        Assert.Empty(f.Designed);
        Assert.Equal("What the clip says.", f.Cloned.Single().Sample.Text);
        Assert.Equal(Sampler.Seed("Hello." + c.VoiceID), f.Cloned.Single().Seed);
        LocalClones.Shared.Delete(c.Id);
        await Assert.ThrowsAsync<SpeechException>(() => f.Qwen.Render(new SpeechRequest { Text = "Again.", Choice = choice }, default));
        Assert.Null(f.Qwen.Info(choice));
    }

    [Fact]
    public async Task OrpheusPerformsTheSoundOnce()
    {
        using var f = new Fakes();
        Assert.False(f.Orpheus.ShadesPitch); // the renderer shifts Orpheus's finished audio
        var text = string.Concat(Enumerable.Repeat("I suppose we should go home before the storm comes over the hills. ", 5)).Trim();
        await f.Orpheus.Render(new SpeechRequest { Text = text, Choice = new VoiceChoice(VoiceEngine.Orpheus, "leo"), Sound = VocalSound.Sigh }, default);
        Assert.True(f.Spoken.Count > 1);
        Assert.StartsWith("<sigh> ", f.Spoken[0].Text);
        Assert.All(f.Spoken.Skip(1), s => Assert.DoesNotContain("<sigh>", s.Text));
        Assert.All(f.Spoken, s => Assert.Equal("leo", s.Voice));
        await Assert.ThrowsAsync<SpeechException>(() => f.Orpheus.Render(new SpeechRequest { Text = "Hi.", Choice = new VoiceChoice(VoiceEngine.Orpheus, "nobody") }, default));
    }

    [Fact]
    public async Task WithoutTheDownloadTheEnginesSaySo()
    {
        VoicePacks.Folder = TempDir();
        try
        {
            var qwen = new QwenEngine();
            Assert.False(qwen.IsReady);
            Assert.Contains("Settings › Voices", qwen.NotReadyReason);
            var e = await Assert.ThrowsAsync<SpeechException>(() => qwen.Render(new SpeechRequest { Text = "Hi.", Choice = VoiceInfo.QwenPresets[0].Choice }, default));
            Assert.Contains("isn’t downloaded yet", e.Message);
            Assert.False(new OrpheusEngine().IsReady);
        }
        finally { VoicePacks.Folder = Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)"); }
    }

    // MARK: Packs and downloads

    [Fact]
    public void ThePacksArePinnedAndChecked()
    {
        foreach (var pack in VoicePacks.Packs)
        {
            Assert.NotEmpty(pack.Files);
            Assert.All(pack.Files, f =>
            {
                Assert.Matches("^[0-9a-f]{40}$", f.Repo.Revision);
                Assert.Matches("^[0-9a-f]{64}$", f.Sha256);
                Assert.Contains($"/resolve/{f.Repo.Revision}/", f.Url);
            });
            Assert.Equal(pack.Files.Count, pack.Files.Select(f => f.Local).Distinct().Count());
        }
        Assert.InRange(VoicePacks.Qwen.Bytes, 8e9, 9.5e9);
        Assert.InRange(VoicePacks.Orpheus.Bytes, 2.3e9, 2.7e9);
        // The two Qwen3-TTS models share one tokenizer and one audio decoder.
        Assert.Single(VoicePacks.Qwen.Files, f => f.Local.EndsWith("vocoder.onnx.data", StringComparison.Ordinal));
        Assert.Contains(VoicePacks.Qwen.Files, f => f.Local.StartsWith("qwen/clone/speaker_encoder", StringComparison.Ordinal));
        Assert.Contains(VoicePacks.Orpheus.Files, f => f.Local == "orpheus/snac/decoder_model.onnx");
        Assert.All(VoicePacks.Packs.SelectMany(p => p.Repos), r => Assert.False(string.IsNullOrEmpty(r.License)));
    }

    /// Serves files by URL, with byte ranges.
    sealed class FakeHub(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        public readonly List<(string Url, long? From)> Requests = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            long? from = request.Headers.Range?.Ranges.First().From;
            lock (Requests) Requests.Add((url, from));
            if (!files.TryGetValue(url, out var data)) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            var body = from is { } f ? data[(int)f..] : data;
            return Task.FromResult(new HttpResponseMessage(from is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent) { Content = new ByteArrayContent(body) });
        }
    }

    static (VoicePack Pack, Dictionary<string, byte[]> Files) SmallPack(bool damaged = false)
    {
        var repo = new VoiceRepo("test/pack", new string('a', 40), "MIT", "Test");
        var files = new Dictionary<string, byte[]>();
        var list = new List<VoicePackFile>();
        foreach (var (path, size) in new[] { ("a.onnx", 3000), ("b/c.npy", 5000) })
        {
            var bytes = Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray();
            var f = new VoicePackFile(repo, path, "test/" + path, size, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            files[f.Url] = damaged && path == "a.onnx" ? bytes.Select(b => (byte)(b ^ 1)).ToArray() : bytes;
            list.Add(f);
        }
        return (new VoicePack(VoiceEngine.Orpheus, "Test", "", list, 8, 4), files);
    }

    [Fact]
    public async Task DownloadsResumeAndAreChecked()
    {
        VoicePacks.Folder = TempDir();
        try
        {
            var (pack, files) = SmallPack();
            Assert.False(VoicePacks.IsInstalled(pack));
            // Half of the first file is there from a dropped connection.
            var first = pack.Files[0];
            Directory.CreateDirectory(Path.GetDirectoryName(VoicePacks.PathOf(first))!);
            File.WriteAllBytes(VoicePacks.PathOf(first) + ".part", files[first.Url][..1500]);
            Assert.Equal(1500, VoicePacks.BytesPresent(pack));
            var hub = new FakeHub(files);
            var reports = new List<VoicePackProgress>();
            await VoicePackDownloader.Download(pack, new HttpClient(hub), new SyncProgress(reports), default);
            Assert.Equal((first.Url, (long?)1500), hub.Requests[0]);
            Assert.Null(hub.Requests[1].From);
            Assert.True(VoicePacks.IsInstalled(pack));
            Assert.Equal(1.0, reports[^1].Fraction);
            Assert.False(File.Exists(VoicePacks.PathOf(first) + ".part"));
            // Nothing is fetched again once it's whole.
            await VoicePackDownloader.Download(pack, new HttpClient(hub), null, default);
            Assert.Equal(2, hub.Requests.Count);
            VoicePacks.Remove(pack);
            Assert.False(VoicePacks.IsInstalled(pack));
            Assert.False(File.Exists(VoicePacks.PathOf(first)));

            // A damaged download is thrown away, never moved into place.
            var (bad, badFiles) = SmallPack(damaged: true);
            var e = await Assert.ThrowsAsync<IOException>(() => VoicePackDownloader.Download(bad, new HttpClient(new FakeHub(badFiles)), null, default));
            Assert.Contains("damaged", e.Message);
            Assert.False(File.Exists(VoicePacks.PathOf(bad.Files[0])));
            Assert.False(File.Exists(VoicePacks.PathOf(bad.Files[0]) + ".part"));
        }
        finally { VoicePacks.Folder = Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)"); }
    }

    sealed class SyncProgress(List<VoicePackProgress> into) : IProgress<VoicePackProgress>
    {
        public void Report(VoicePackProgress value) { lock (into) into.Add(value); }
    }

    // MARK: Hardware

    [Fact]
    public void TheHardwareCheckWarnsPlainly()
    {
        var strong = new VoiceHardware(32, "NVIDIA GeForce RTX 3060", 12, 0, "Test CPU", 16, "x64");
        Assert.Equal(VoiceFit.Good, VoiceHardwareCheck.Fit(strong, VoiceEngine.Qwen));
        Assert.Null(VoiceHardwareCheck.Warning(strong, VoiceEngine.Orpheus));

        var noGpu = strong with { GpuName = null, GpuMemoryGB = 0 };
        Assert.Equal(VoiceFit.Slow, VoiceHardwareCheck.Fit(noGpu, VoiceEngine.Orpheus));
        var w = VoiceHardwareCheck.Warning(noGpu, VoiceEngine.Orpheus)!;
        Assert.Contains("no DirectX 12 graphics card", w);
        Assert.Contains("times longer", w);
        Assert.Contains("Windows Voices", w);
        Assert.Contains("ElevenLabs", w);

        var integrated = strong with { GpuName = "Intel Iris Xe", GpuMemoryGB = 0.5 };
        Assert.False(VoiceHardwareCheck.UsesGpu(integrated));
        Assert.Contains("runs on the processor", VoiceHardwareCheck.Warning(integrated, VoiceEngine.Qwen));
        var smallCard = strong with { GpuName = "Test Graphics", GpuMemoryGB = 4 };
        Assert.True(VoiceHardwareCheck.UsesGpu(smallCard));
        Assert.Contains("8 GB or more", VoiceHardwareCheck.Warning(smallCard, VoiceEngine.Qwen));
        // The graphics card check's answer, from the child process.
        Assert.Equal(new GpuInfo(1, "Test Graphics RTX", 12), VoiceHardwareCheck.ParseGpu("1\t12\tTest Graphics RTX\r\n"));
        Assert.Null(VoiceHardwareCheck.ParseGpu("none"));
        Assert.Null(VoiceHardwareCheck.ParseGpu(null));
        Assert.Null(VoiceHardwareCheck.ParseGpu("garbage"));
        Assert.Null(VoiceHardwareCheck.ParseGpu("-1\t4\tX"));
        Assert.Equal(new GpuInfo(0, "Fallback", 0), VoiceHardwareCheck.Detect(new GpuInfo(0, "Fallback", 0)) is var h ? new GpuInfo(h.GpuIndex, h.GpuName!, h.GpuMemoryGB) : null);
        Assert.Contains("16 GB", VoiceHardwareCheck.Warning(strong with { MemoryGB = 12 }, VoiceEngine.Qwen));
        Assert.Equal(VoiceFit.TooWeak, VoiceHardwareCheck.Fit(strong with { MemoryGB = 4 }, VoiceEngine.Qwen));
        Assert.Contains("may not load", VoiceHardwareCheck.Warning(strong with { MemoryGB = 4 }, VoiceEngine.Qwen));
        Assert.Contains("RTX 3060", VoiceHardwareCheck.Recommended);
        Assert.Contains("32 GB", VoiceHardwareCheck.Recommended);
        // Detecting never throws, on any computer.
        var here = VoiceHardwareCheck.Detect();
        Assert.True(here.MemoryGB > 0);
        Assert.True(here.CpuThreads > 0);
    }

    // MARK: The real models (when downloaded)

    static string? Models => Environment.GetEnvironmentVariable("INNERVOICE_TEST_VOICE_MODELS") is { Length: > 0 } m && Directory.Exists(m) ? m : null;

    [Fact]
    public async Task TheRealModelsSpeak()
    {
        // Runs only with the models: set INNERVOICE_TEST_VOICE_MODELS to a Voice Models folder.
        if (Models is null) return;
        VoicePacks.Folder = Models!;
        var samples = TempDir();
        try
        {
            QwenSamples.Forget();
            Assert.True(VoicePacks.IsInstalled(VoicePacks.Qwen));
            Assert.True(VoicePacks.IsInstalled(VoicePacks.Orpheus));
            var qwen = new QwenEngine();
            var choice = VoiceInfo.QwenPresets.First(v => v.VoiceID == "preset:baritone").Choice;
            var line = await qwen.Render(new SpeechRequest { Text = "The rain had not stopped for three days.", Choice = choice }, default);
            var (voiced, hz) = Pitch.Analyze(line);
            Assert.InRange(SpeechAudio.Seconds(line), 1.2, 8);
            Assert.True(voiced > 0.3, $"voiced {voiced}");
            Assert.InRange(hz, 80, 180); // a man's voice, as described
            var orpheus = new OrpheusEngine();
            var o = await orpheus.Render(new SpeechRequest { Text = "Good morning.", Choice = new VoiceChoice(VoiceEngine.Orpheus, "tara") }, default);
            Assert.InRange(SpeechAudio.Seconds(o), 0.5, 5);
            Assert.True(Pitch.Analyze(o).Voiced > 0.2);
        }
        finally
        {
            NeuralModels.Shared.Unload(VoiceEngine.Qwen);
            NeuralModels.Shared.Unload(VoiceEngine.Orpheus);
            if (Directory.Exists(VoicePacks.SamplesFolder)) Directory.Delete(VoicePacks.SamplesFolder, true);
            VoicePacks.Folder = Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)");
        }
    }
}
