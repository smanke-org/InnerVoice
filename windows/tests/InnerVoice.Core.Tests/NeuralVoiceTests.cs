using System.Net;
using System.Security.Cryptography;
using System.Text;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

namespace InnerVoice.Core.Tests;

/// The local voices (Pocket TTS, Qwen3-TTS and Orpheus on ONNX Runtime): everything around the
/// models with fakes, and the real models when INNERVOICE_TEST_VOICE_MODELS names a Voice Models
/// folder (Pocket TTS 0.5 GB, Qwen3-TTS 8.8 GB, Orpheus 2.5 GB; VoiceBench --bench uses the same
/// layout).
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
    public void QwenTokenizerSplitsAndMapsBytes()
    {
        var dir = TempDir();
        // A tiny byte-level vocabulary: "Ġ" is the space byte.
        File.WriteAllText(Path.Combine(dir, "vocab.json"), """{"H":0,"e":1,"l":2,"o":3,"Ġ":4,"w":5,"r":6,"d":7,"He":8,"ll":9,"Hell":10,"Hello":11,"Ġw":12,"Ġwo":13,"!":14}""");
        File.WriteAllText(Path.Combine(dir, "merges.txt"), "#version: 0.2\nH e\nl l\nHe ll\nHell o\nĠ w\nĠw o\n");
        var qwen = BpeTokenizer.Qwen(Path.Combine(dir, "vocab.json"), Path.Combine(dir, "merges.txt"));
        Assert.Equal([151644, 11, 13, 6, 2, 7, 14, 151645], qwen.Encode("<|im_start|>Hello world!<|im_end|>"));
    }

    [Fact]
    public void OrpheusTokenizerSplitsAndMapsBytes()
    {
        var dir = TempDir();
        // Llama 3's tokenizer.json, with ignore_merges: a whole word in the vocabulary is one token.
        File.WriteAllText(Path.Combine(dir, "tokenizer.json"), """
            {"added_tokens":[{"id":100,"content":"<|begin_of_text|>","special":true}],
             "model":{"type":"BPE","ignore_merges":true,"vocab":{"H":0,"e":1,"l":2,"o":3,"Hello":4,"Ġ":5,"1":6,"2":7,"3":8,"4":9,"123":10},"merges":[["H","e"]]}}
            """);
        var llama = BpeTokenizer.FromTokenizerJson(Path.Combine(dir, "tokenizer.json"));
        Assert.Equal([100, 4, 5, 10, 9], llama.Encode("<|begin_of_text|>Hello 1234"));
    }

    /// Pocket TTS's tokenizer (Kyutai's tokenizer.model, CC BY 4.0, kept in Fixtures/pocket)
    /// gives the SentencePiece library's own answers (sentencepiece 0.2.3, 2026-11-13): the
    /// best split, digits one at a time, and characters it has no piece for as UTF-8 bytes.
    [Fact]
    public void PocketTokenizerMatchesSentencePiece()
    {
        var sp = SentencePiece.FromFile(Fixtures.PathOf("pocket/tokenizer.model"));
        Assert.Equal(4000, sp.Count);
        (string Text, int[] Ids)[] cases =
        [
            ("Hello world.", [2994, 578, 263]),
            ("        Hi there.", [260, 260, 260, 260, 260, 260, 260, 260, 1445, 310, 263]),
            ("Don’t go — “now!”", [1201, 230, 132, 157, 274, 350, 260, 3133, 260, 230, 132, 160, 306, 1080, 682, 230, 132, 161]),
            ("naïve café 123 ok", [913, 199, 179, 314, 331, 2250, 745, 260, 341, 365, 450, 676, 518]),
            ("日本", [260, 234, 155, 169, 234, 160, 176]),
            ("The wind came off the sea. Mara pulled the door shut behind her and leaned on it until the latch caught!",
             [364, 2206, 662, 475, 265, 1985, 263, 1329, 362, 1280, 278, 265, 1296, 2374, 260, 1234, 429, 269, 3371, 278, 288, 275, 1030, 265, 1041, 1994, 260, 2502, 682]),
            ("“You’re late,” said Tom Hale, without looking up from the lamp.",
             [260, 230, 132, 160, 2954, 1359, 230, 132, 157, 280, 1754, 262, 230, 132, 161, 425, 2368, 1033, 410, 262, 291, 629, 384, 273, 330, 340, 265, 697, 1255, 263]),
            (".!...?", [260, 263, 682, 799, 292]),
            ("Mr. Hale, 1,204 miles; e.g. «ça» 😀 end",
             [1519, 263, 1033, 410, 262, 260, 341, 262, 365, 316, 487, 984, 836, 1230, 402, 263, 453, 263, 260, 198, 175, 2467, 362, 198, 191, 260, 244, 163, 156, 132, 564]),
        ];
        foreach (var (text, ids) in cases)
        {
            Assert.Equal(ids, sp.Encode(text));
            Assert.Equal(text, sp.Decode(ids));
        }
    }

    /// Pocket TTS's text steps, as mlx-audio and Kyutai's reference do them: a capital and a
    /// full stop, groups of whole sentences of at most 50 tokens, more frames after the end for
    /// a short line.
    [Fact]
    public void PocketPreparesAndGroupsTextLikeTheReference()
    {
        Assert.Equal(("Hello there.", 5), PocketModel.Prepare("hello there"));
        Assert.Equal(("The rain had not stopped for three days.", 3), PocketModel.Prepare("  The rain had not stopped\nfor three days. "));
        Assert.Equal(("Why?", 5), PocketModel.Prepare("Why?"));
        var sp = SentencePiece.FromFile(Fixtures.PathOf("pocket/tokenizer.model"));
        var sentence = "The rain had not stopped for three days, and the river was rising fast.";
        var groups = PocketModel.Groups(sp, string.Join(" ", Enumerable.Repeat(sentence, 4)));
        Assert.True(groups.Count >= 2);
        Assert.All(groups, g => Assert.True(sp.Encode(g).Count <= 50, g));
        Assert.Equal(string.Join(" ", Enumerable.Repeat(sentence, 4)).Replace(" ", ""), string.Concat(groups).Replace(" ", ""));
        Assert.Equal(["Hello there."], PocketModel.Groups(sp, "hello there"));
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
        Assert.True(LocalClones.IsClone(new VoiceChoice(VoiceEngine.Pocket, c.VoiceID)));
        Assert.True(LocalClones.IsClone(new VoiceChoice(VoiceEngine.Qwen, c.VoiceID)));
        Assert.False(LocalClones.IsClone(new VoiceChoice(VoiceEngine.Orpheus, c.VoiceID)));
        Assert.Equal(VoiceEngine.Qwen, LocalClones.Info(c, VoiceEngine.Qwen).Engine);
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
        public readonly List<PocketLine> Said = [];
        public readonly List<(string Text, string Description, ulong Seed)> Designed = [];
        public readonly List<(string Text, VoiceSample Sample, ulong Seed)> Cloned = [];
        public readonly List<(string Text, string Voice)> Spoken = [];
        public readonly PocketEngine Pocket = new() { AssumeReady = true };
        public readonly QwenEngine Qwen = new() { AssumeReady = true };
        public readonly OrpheusEngine Orpheus = new() { AssumeReady = true };
        readonly Func<string, string, ulong, CancellationToken, float[]> designer = QwenSamples.Designer;
        readonly LocalClones clones = LocalClones.Shared;
        /// What Qwen3-TTS's VoiceDesign model makes for a description.
        public Func<string, float[]> Voice = d => Tone(d.Contains("woman") ? 220 : 115, 4);

        public Fakes()
        {
            VoicePacks.Folder = TempDir();
            PocketClips.Forget();
            QwenSamples.Forget();
            LocalClones.Shared = new LocalClones(TempDir());
            QwenSamples.Designer = (text, description, seed, ct) => { lock (Designed) Designed.Add((text, description, seed)); return Voice(description); };
            Qwen.Cloner = (text, sample, seed, ct) => { lock (Cloned) Cloned.Add((text, sample, seed)); return Tone(150, text.Length / 20.0, 0.05f); };
            // A quiet tone at the clip's own pitch, a twentieth of a second a character.
            Pocket.Speaker = (line, ct) => { lock (Said) Said.Add(line); return Tone(Math.Max(60, Pitch.Analyze(line.Clip).Hz), line.Text.Length / 20.0, 0.05f); };
            Orpheus.Speaker = (text, voice, seed, ct) => { lock (Spoken) Spoken.Add((text, voice)); return Tone(180, 1, 0.05f); };
        }

        public void Dispose()
        {
            PocketClips.Forget();
            QwenSamples.Designer = designer;
            QwenSamples.Forget();
            LocalClones.Shared = clones;
            VoicePacks.Folder = Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)");
        }
    }

    /// The nine stock voices ship with the app, and Chatterbox's own: the Mac's clips, 24 kHz
    /// mono, 7.5 to 11 seconds (Pocket TTS's 8 to 11).
    [Fact]
    public void TheStockVoicesShipWithTheApp()
    {
        Assert.Equal(["alba", "chatterbox", "dan", "jess", "leah", "leo", "mia", "tara", "zac", "zoe"], PocketClips.StockNames);
        Assert.Equal(PocketClips.StockNames.Where(n => n != "chatterbox"), VoiceInfo.Pocket.Select(v => PocketVoice.StockName(v.VoiceID)).Order(StringComparer.Ordinal));
        Assert.Equal(PocketClips.StockNames, VoiceInfo.Chatterbox.Select(v => PocketVoice.StockName(v.VoiceID)).Order(StringComparer.Ordinal));
        foreach (var name in PocketClips.StockNames)
        {
            var clip = PocketClips.StockClip(name)!;
            Assert.InRange(clip.Length / (double)SpeechAudio.SampleRate, name == "chatterbox" ? 7.5 : 8, 11);
            Assert.True(Pitch.Analyze(clip).Voiced > 0.3, name);
        }
        // The men's clips are lower than the women's.
        Assert.True(Pitch.Analyze(PocketClips.StockClip("dan")!).Hz < Pitch.Analyze(PocketClips.StockClip("zoe")!).Hz);
        Assert.Null(PocketClips.StockClip("nobody"));
        // The same audio as the Mac's Apple Lossless clips, when this checkout has them.
        var mac = Path.Combine(Fixtures.Folder, "..", "..", "Resources", "StockVoices");
        if (Directory.Exists(mac))
            Assert.Equal(Directory.GetFiles(mac, "*.m4a").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal), PocketClips.StockNames);
    }

    [Fact]
    public async Task PocketSpeaksEveryLineFromTheVoicesClipInItsTone()
    {
        using var f = new Fakes();
        Assert.True(f.Pocket.ShadesPitch && f.Pocket.ShadesPace); // it does all its own shading
        var tara = new VoiceChoice(VoiceEngine.Pocket, "stock:tara");
        var spoken = new SpeechRequest { Text = "Where were you?", Choice = tara, Kind = LineKind.Spoken, Tone = Core.Tone.Angry };
        var a = await f.Pocket.Render(spoken, default);
        // The tone sets the temperature (and the Mac's seed: the line and the voice).
        var line = Assert.Single(f.Said);
        Assert.Equal(0.95, line.Temperature);
        Assert.Equal(Sampler.Seed("Where were you?" + "stock:tara"), line.Seed);
        Assert.Equal(PocketClips.StockClip("tara")!.Length, line.Clip.Length);
        // Leveled to -20 dB from a quiet line, then sped up 4% for angry.
        Assert.InRange(20 * Math.Log10(Math.Sqrt(a.Average(x => (double)x * x))), -21, -19);
        double plain = "Where were you?".Length / 20.0;
        Assert.InRange(a.Length / (double)SpeechAudio.SampleRate, plain / 1.04 - 0.03, plain / 1.04 + 0.03);
        // Narration is always even: the model's own temperature, at its own pace.
        var narration = await f.Pocket.Render(new SpeechRequest { Text = "She left the room.", Choice = tara, Tone = Core.Tone.Angry }, default);
        Assert.Equal(PocketDelivery.DefaultTemperature, f.Said[^1].Temperature);
        Assert.InRange(narration.Length / (double)SpeechAudio.SampleRate, "She left the room.".Length / 20.0 - 0.03, "She left the room.".Length / 20.0 + 0.03);
        // Whispered is softer (0.7 after leveling); a thought is a little quieter too.
        var whispered = await f.Pocket.Render(spoken with { Tone = Core.Tone.Whispered }, default);
        Assert.InRange(Rms(whispered) / Rms(a), 0.68, 0.72);
        var thought = await f.Pocket.Render(spoken with { Kind = LineKind.Thought, Tone = Core.Tone.Neutral }, default);
        Assert.InRange(Rms(thought) / Rms(a), 0.83, 0.87);
        // A voice Pocket TTS doesn't have (a design is a Qwen3-TTS voice) says so.
        await Assert.ThrowsAsync<SpeechException>(() => f.Pocket.Render(spoken with { Choice = new VoiceChoice(VoiceEngine.Pocket, "design", "A man.") }, default));
    }

    static double Rms(float[] x) => Math.Sqrt(x.Average(v => (double)v * v));

    [Fact]
    public async Task PitchShiftsTheClipAndPaceStretchesTheLine()
    {
        using var f = new Fakes();
        var dan = new VoiceChoice(VoiceEngine.Pocket, "stock:dan");
        double own = Pitch.Analyze(PocketClips.StockClip("dan")!).Hz;
        // A shade 2 lower, an older character's pitch (0.89: 2 lower) and a sad tone (1 lower):
        // the clip goes 5 semitones down before cloning.
        var older = new VoiceChoice(VoiceEngine.Pocket, "stock:dan", rate: 0.92, pitch: 0.89);
        var r = new SpeechRequest { Text = "It was a long time ago.", Choice = older, Kind = LineKind.Spoken, Tone = Core.Tone.Sad, Shade = Shade.Make(-2, 1) };
        Assert.Equal(-5, PocketEngine.Semitones(r));
        var a = await f.Pocket.Render(r, default);
        Assert.InRange(Pitch.Analyze(f.Said[^1].Clip).Hz, own * Math.Pow(2, -5 / 12.0) * 0.93, own * Math.Pow(2, -5 / 12.0) * 1.07);
        Assert.Equal(PocketClips.Key(older, -5), f.Said[^1].ClipKey);
        // The pace: the voice's 0.92, a shade's step quicker (1.08) and sad's 0.92.
        double rate = 0.92 * 1.08 * 0.92;
        Assert.Equal(rate, PocketEngine.Rate(r), 6);
        double plain = r.Text.Length / 20.0;
        Assert.InRange(a.Length / (double)SpeechAudio.SampleRate, plain / rate - 0.04, plain / rate + 0.04);
        // The same clip and shift are made once.
        Assert.Same(PocketClips.Reference(older, -5), PocketClips.Reference(older, -5));
        // Long text goes a few sentences at a time.
        var text = string.Concat(Enumerable.Repeat("The wagon rolled slowly down the long hill toward the river. ", 6)).Trim();
        f.Said.Clear();
        await f.Pocket.Render(new SpeechRequest { Text = text, Choice = dan }, default);
        Assert.True(f.Said.Count > 1);
        Assert.Equal(text, string.Join(" ", f.Said.Select(s => s.Text)));
    }

    [Fact]
    public async Task AVoiceClonedOnThisPCSpeaksFromItsClip()
    {
        using var f = new Fakes();
        var c = LocalClones.Shared.Add("Ellen", Gender.Female, Tone(210, 6), "What the clip says.");
        var choice = new VoiceChoice(VoiceEngine.Pocket, c.VoiceID);
        Assert.Equal("Ellen", f.Pocket.Info(choice)!.Name);
        Assert.Equal(c.VoiceID, f.Pocket.Voices[0].VoiceID); // clones first
        await f.Pocket.Render(new SpeechRequest { Text = "Hello.", Choice = choice }, default);
        Assert.Equal(6 * SpeechAudio.SampleRate, f.Said.Single().Clip.Length);
        Assert.Equal(Sampler.Seed("Hello." + c.VoiceID), f.Said.Single().Seed);
        LocalClones.Shared.Delete(c.Id);
        await Assert.ThrowsAsync<SpeechException>(() => f.Pocket.Render(new SpeechRequest { Text = "Again.", Choice = choice }, default));
        Assert.Null(f.Pocket.Info(choice));
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
    public async Task AShadeShiftsTheQwenSampleNotTheLine()
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
    public async Task AVoiceClonedOnThisPCSpeaksWithQwenFromItsClip()
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


    /// A clone's clip can be up to 15 seconds (Pocket TTS); Qwen3-TTS's ONNX codec encoder takes
    /// 10, so it gets the clip cut at its quietest moment before 10 s, with the words cut to match.
    [Fact]
    public async Task ALongCloneClipIsFittedForQwen()
    {
        using var f = new Fakes();
        // 14 seconds: speech with a silent gap at 8.5 s.
        var audio = Tone(200, 14);
        for (int i = (int)(8.5 * SpeechAudio.SampleRate); i < (int)(8.8 * SpeechAudio.SampleRate); i++) audio[i] = 0;
        var text = string.Join(" ", Enumerable.Range(1, 28).Select(n => $"w{n}"));
        var c = LocalClones.Shared.Add("Long", Gender.Female, audio, text);
        await f.Qwen.Render(new SpeechRequest { Text = "Hello.", Choice = new VoiceChoice(VoiceEngine.Qwen, c.VoiceID) }, default);
        var sample = f.Cloned.Single().Sample;
        Assert.InRange(sample.Audio.Length / (double)SpeechAudio.SampleRate, 8.5, 8.8);
        Assert.StartsWith("w1 w2", sample.Text);
        Assert.True(sample.Text.Length < text.Length && !sample.Text.EndsWith(' '));
        Assert.Equal(sample.Text, string.Join(" ", text.Split(' ').Take(sample.Text.Split(' ').Length))); // whole words
        // A short clip is used as it is.
        var shortOne = new VoiceSample(Tone(200, 6), "Six seconds.");
        Assert.Same(shortOne, QwenSamples.Fitted(shortOne));
        // Pocket TTS still gets the whole clip.
        Assert.Equal(14 * SpeechAudio.SampleRate, LocalClones.Shared.Sample(c.VoiceID).Audio.Length);
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
            var pocket = new PocketEngine();
            Assert.False(pocket.IsReady);
            Assert.Contains("Settings › Voices", pocket.NotReadyReason);
            var e = await Assert.ThrowsAsync<SpeechException>(() => pocket.Render(new SpeechRequest { Text = "Hi.", Choice = VoiceInfo.Pocket[0].Choice }, default));
            Assert.Contains("isn’t downloaded yet", e.Message);
            var qwen = new QwenEngine();
            Assert.False(qwen.IsReady);
            Assert.Contains("Settings › Voices", qwen.NotReadyReason);
            e = await Assert.ThrowsAsync<SpeechException>(() => qwen.Render(new SpeechRequest { Text = "Hi.", Choice = VoiceInfo.QwenPresets[0].Choice }, default));
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
        // Pocket TTS: Kyutai's five graphs (fp32, as the Mac runs it), its tokenizer and license.
        Assert.InRange(VoicePacks.Pocket.Bytes, 4.5e8, 5e8);
        Assert.Equal("473 MB", VoicePacks.Pocket.SizeDescription);
        Assert.Equal(["flow_lm_flow.onnx", "flow_lm_main.onnx", "mimi_decoder.onnx", "mimi_encoder.onnx", "text_conditioner.onnx"],
            VoicePacks.Pocket.Files.Where(f => f.Local.EndsWith(".onnx", StringComparison.Ordinal)).Select(f => Path.GetFileName(f.Local)).Order(StringComparer.Ordinal));
        Assert.Contains(VoicePacks.Pocket.Files, f => f.Local == "pocket/tokenizer.model" && f.Sha256 == "d461765ae179566678c93091c5fa6f2984c31bbe990bf1aa62d92c64d91bc3f6");
        Assert.Contains(VoicePacks.Pocket.Files, f => f.Local == "pocket/LICENSE");
        Assert.All(VoicePacks.Pocket.Repos, r => Assert.Contains("CC BY 4.0", r.License));
        // Qwen3-TTS: WaveKat's ONNX exports of the Qwen team's models, Apache 2.0. The two models
        // share one tokenizer and one audio decoder, and every file comes from Hugging Face.
        Assert.InRange(VoicePacks.Qwen.Bytes, 8e9, 9.5e9);
        Assert.Equal("8.8 GB", VoicePacks.Qwen.SizeDescription);
        Assert.Single(VoicePacks.Qwen.Files, f => f.Local.EndsWith("vocoder.onnx.data", StringComparison.Ordinal));
        Assert.Contains(VoicePacks.Qwen.Files, f => f.Local.StartsWith("qwen/clone/speaker_encoder", StringComparison.Ordinal));
        Assert.All(VoicePacks.Qwen.Files, f => Assert.StartsWith("https://huggingface.co/wavekat/Qwen3-TTS-", f.Url));
        Assert.All(VoicePacks.Qwen.Repos, r => Assert.Equal("Apache-2.0", r.License));
        Assert.Equal([VoiceEngine.Pocket, VoiceEngine.Qwen, VoiceEngine.Chatterbox, VoiceEngine.Orpheus], VoicePacks.Packs.Select(p => p.Engine));
        // Chatterbox: the ONNX Community's export of Resemble AI's model (MIT), the 4-bit
        // language model and the four graphs, every one with its external weights.
        Assert.InRange(VoicePacks.Chatterbox.Bytes, 1.5e9, 1.6e9);
        Assert.All(VoicePacks.Chatterbox.Files, f => Assert.StartsWith("https://huggingface.co/onnx-community/chatterbox-ONNX/resolve/", f.Url));
        Assert.All(VoicePacks.Chatterbox.Repos, r => Assert.Equal("MIT", r.License));
        foreach (var graph in new[] { "speech_encoder", "embed_tokens", "language_model_q4", "conditional_decoder" })
        {
            Assert.Contains(VoicePacks.Chatterbox.Files, f => f.Local == $"chatterbox/onnx/{graph}.onnx");
            Assert.Contains(VoicePacks.Chatterbox.Files, f => f.Local == $"chatterbox/onnx/{graph}.onnx_data");
        }
        Assert.Contains(VoicePacks.Chatterbox.Files, f => f.Local == "chatterbox/onnx/" + ChatterboxModel.LanguageModelFile);
        Assert.Contains(VoicePacks.Chatterbox.Files, f => f.Local == "chatterbox/tokenizer.json");
        Assert.Equal(VoicePacks.Chatterbox, VoicePacks.For(VoiceEngine.Chatterbox));
        Assert.Null(VoicePacks.For(VoiceEngine.Gemini));
        Assert.All(VoicePacks.Packs.SelectMany(p => p.Files), f => Assert.StartsWith("https://huggingface.co/", f.Url));
        Assert.InRange(VoicePacks.Orpheus.Bytes, 2.3e9, 2.7e9);
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
        return (new VoicePack(VoiceEngine.Orpheus, "Test", "", list, 8, 4, 8), files);
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
        Assert.Equal(VoiceFit.Good, VoiceHardwareCheck.Fit(strong, VoiceEngine.Pocket));
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
        Assert.Contains("runs on the processor", VoiceHardwareCheck.Warning(integrated, VoiceEngine.Orpheus));
        var smallCard = strong with { GpuName = "Test Graphics", GpuMemoryGB = 4 };
        Assert.True(VoiceHardwareCheck.UsesGpu(smallCard));
        Assert.Contains("8 GB or more", VoiceHardwareCheck.Warning(smallCard, VoiceEngine.Orpheus));
        Assert.Contains("16 GB", VoiceHardwareCheck.Warning(strong with { MemoryGB = 12 }, VoiceEngine.Orpheus));
        Assert.Equal(VoiceFit.TooWeak, VoiceHardwareCheck.Fit(strong with { MemoryGB = 4 }, VoiceEngine.Orpheus));

        // Pocket TTS is small (about 1 GB while it speaks) and quick on a processor: no card
        // needed, 8 GB of memory is plenty, under 4 GB it may not load.
        Assert.Equal(VoiceFit.Good, VoiceHardwareCheck.Fit(noGpu with { MemoryGB = 8 }, VoiceEngine.Pocket));
        Assert.Null(VoiceHardwareCheck.Warning(integrated with { MemoryGB = 8 }, VoiceEngine.Pocket));
        Assert.Null(VoiceHardwareCheck.Warning(smallCard with { MemoryGB = 16 }, VoiceEngine.Pocket));
        Assert.Equal(VoiceFit.Slow, VoiceHardwareCheck.Fit(noGpu with { MemoryGB = 6 }, VoiceEngine.Pocket));
        Assert.Contains("works best with 8 GB or more", VoiceHardwareCheck.Warning(noGpu with { MemoryGB = 6 }, VoiceEngine.Pocket));
        Assert.Equal(VoiceFit.TooWeak, VoiceHardwareCheck.Fit(noGpu with { MemoryGB = 3 }, VoiceEngine.Pocket));
        Assert.Contains("may not load", VoiceHardwareCheck.Warning(noGpu with { MemoryGB = 3 }, VoiceEngine.Pocket));
        // Qwen3-TTS is large: a card with 8 GB and 16 GB of memory, as before.
        Assert.Equal(VoiceFit.Good, VoiceHardwareCheck.Fit(strong, VoiceEngine.Qwen));
        Assert.Equal(VoiceFit.Slow, VoiceHardwareCheck.Fit(noGpu, VoiceEngine.Qwen));
        Assert.Contains("no DirectX 12 graphics card", VoiceHardwareCheck.Warning(noGpu, VoiceEngine.Qwen));
        Assert.Contains("8 GB or more", VoiceHardwareCheck.Warning(smallCard, VoiceEngine.Qwen));
        Assert.Equal(VoiceFit.TooWeak, VoiceHardwareCheck.Fit(strong with { MemoryGB = 4 }, VoiceEngine.Qwen));
        Assert.Contains("RTX 3060", VoiceHardwareCheck.Recommended);
        Assert.Contains("Pocket TTS is small", VoiceHardwareCheck.Recommended);
        Assert.Contains("For Qwen3-TTS, Chatterbox and Orpheus", VoiceHardwareCheck.Recommended);
        Assert.Contains("32 GB to keep two of them loaded together", VoiceHardwareCheck.Recommended);
        // The graphics card check's answer, from the child process.
        Assert.Equal(new GpuInfo(1, "Test Graphics RTX", 12), VoiceHardwareCheck.ParseGpu("1\t12\tTest Graphics RTX\r\n"));
        Assert.Null(VoiceHardwareCheck.ParseGpu("none"));
        Assert.Null(VoiceHardwareCheck.ParseGpu(null));
        Assert.Null(VoiceHardwareCheck.ParseGpu("garbage"));
        Assert.Null(VoiceHardwareCheck.ParseGpu("-1\t4\tX"));
        Assert.Equal(new GpuInfo(0, "Fallback", 0), VoiceHardwareCheck.Detect(new GpuInfo(0, "Fallback", 0)) is var h ? new GpuInfo(h.GpuIndex, h.GpuName!, h.GpuMemoryGB) : null);
        // Detecting never throws, on any computer.
        var here = VoiceHardwareCheck.Detect();
        Assert.True(here.MemoryGB > 0);
        Assert.True(here.CpuThreads > 0);
    }

    // MARK: The real models (when downloaded)

    static string? Models => Environment.GetEnvironmentVariable("INNERVOICE_TEST_VOICE_MODELS") is { Length: > 0 } m && Directory.Exists(m) ? m : null;

    /// Runs only with the models: set INNERVOICE_TEST_VOICE_MODELS to a Voice Models folder
    /// (pocket/, qwen/ and orpheus/, any of them; VoiceBench --bench uses the same layout).
    [Fact]
    public async Task TheRealModelsSpeak()
    {
        if (Models is null) return;
        VoicePacks.Folder = Models!;
        try
        {
            PocketClips.Forget();
            QwenSamples.Forget();
            Assert.True(VoicePacks.Packs.Any(VoicePacks.IsInstalled));
            if (VoicePacks.IsInstalled(VoicePacks.Qwen))
            {
                // A ready-made voice: a sample designed once (kept in Voice Models\Samples), then cloned.
                var choice = VoiceInfo.QwenPresets.First(v => v.VoiceID == "preset:baritone").Choice;
                var q = await new QwenEngine().Render(new SpeechRequest { Text = "The rain had not stopped for three days.", Choice = choice }, default);
                var (qVoiced, qHz) = Pitch.Analyze(q);
                Assert.InRange(SpeechAudio.Seconds(q), 1.2, 8);
                Assert.True(qVoiced > 0.3, $"voiced {qVoiced}");
                Assert.InRange(qHz, 80, 180); // a man's voice, as described
                NeuralModels.Shared.Unload(VoiceEngine.Qwen);
            }
            if (VoicePacks.IsInstalled(VoicePacks.Pocket))
            {
                var pocket = new PocketEngine();
                var line = await pocket.Render(new SpeechRequest { Text = "The rain had not stopped for three days.", Choice = new VoiceChoice(VoiceEngine.Pocket, "stock:dan") }, default);
                var (voiced, hz) = Pitch.Analyze(line);
                Assert.InRange(SpeechAudio.Seconds(line), 1.2, 8);
                Assert.True(voiced > 0.3, $"voiced {voiced}");
                Assert.InRange(hz, 80, 190); // a man's voice, like the clip
                // Short lines stop when they're said (no run-on), at the even and the angry temperature.
                foreach (var tone in new[] { Core.Tone.Neutral, Core.Tone.Angry })
                {
                    var shortLine = await pocket.Render(new SpeechRequest { Text = "No.", Choice = new VoiceChoice(VoiceEngine.Pocket, "stock:tara"), Kind = LineKind.Spoken, Tone = tone }, default);
                    Assert.InRange(SpeechAudio.Seconds(shortLine), 0.2, 2.5);
                }
            }
            if (VoicePacks.IsInstalled(VoicePacks.Orpheus))
            {
                var o = await new OrpheusEngine().Render(new SpeechRequest { Text = "Good morning.", Choice = new VoiceChoice(VoiceEngine.Orpheus, "tara") }, default);
                Assert.InRange(SpeechAudio.Seconds(o), 0.5, 5);
                Assert.True(Pitch.Analyze(o).Voiced > 0.2);
            }
        }
        finally
        {
            NeuralModels.Shared.Unload(VoiceEngine.Pocket);
            NeuralModels.Shared.Unload(VoiceEngine.Qwen);
            NeuralModels.Shared.Unload(VoiceEngine.Orpheus);
            VoicePacks.Folder = Path.Combine(Path.GetTempPath(), "InnerVoice Voice Models (test)");
        }
    }
}

