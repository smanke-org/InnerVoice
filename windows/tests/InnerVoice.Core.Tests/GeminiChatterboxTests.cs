using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InnerVoice.Core.Speech;
using InnerVoice.Core.Speech.Neural;

namespace InnerVoice.Core.Tests;

/// The Mac's GeminiChatterboxTests (Tests/InnerVoiceKitTests/GeminiChatterboxTests.swift), plus
/// the file format both computers share and the parts only Windows has (Chatterbox's ONNX
/// tokenizer and engine).
public class GeminiTTSTests
{
    [Fact]
    public void ThirtyVoicesWithGenders()
    {
        Assert.Equal(30, GeminiTTS.Voices.Count);
        Assert.Equal(30, GeminiTTS.Voices.Select(v => v.VoiceID).Distinct().Count());
        Assert.All(GeminiTTS.Voices, v => Assert.True(v.Engine == VoiceEngine.Gemini && v.Gender != Gender.Neutral));
        Assert.Equal(14, GeminiTTS.Voices.Count(v => v.Gender == Gender.Female));
        Assert.Equal(VoiceAge.Older, GeminiTTS.Voices.First(v => v.Name == "Gacrux").Age);
    }

    [Fact]
    public void RequestBodyMatchesTheInteractionsAPI()
    {
        var root = JsonNode.Parse(GeminiTTS.RequestBody("Not here.", "Kore", "in a quiet whisper", GeminiModel.Flash))!;
        Assert.Equal("gemini-3.8-flash-tts", (string?)root["model"]);
        Assert.Equal("audio", (string?)root["response_format"]!["type"]);
        Assert.False((bool)root["store"]!);
        Assert.Equal("Kore", (string?)root["generation_config"]!["speech_config"]![0]!["voice"]);
        var input = root["input"]![0]!;
        Assert.Equal("user_input", (string?)input["type"]);
        var content = input["content"]![0]!;
        Assert.Equal("Not here.", (string?)content["text"]);
        var note = content["annotations"]![0]!;
        Assert.Equal("speech_metadata", (string?)note["type"]);
        Assert.Equal("in a quiet whisper", (string?)note["style"]);
        // No style: no annotations at all.
        var plain = JsonNode.Parse(GeminiTTS.RequestBody("x", "Puck", null, GeminiModel.FlashLite))!;
        Assert.Null(plain["input"]![0]!["content"]![0]!["annotations"]);
        Assert.Equal("gemini-3.8-flash-lite-tts", (string?)plain["model"]);
        // Keys sorted, as the Mac's JSONSerialization writes them.
        var text = Encoding.UTF8.GetString(GeminiTTS.RequestBody("x", "Puck", null, GeminiModel.Flash));
        Assert.StartsWith("""{"generation_config":""", text);
    }

    [Fact]
    public void StyleFromToneThoughtAndHint()
    {
        Assert.Null(GeminiTTS.Style(Tone.Neutral, LineKind.Spoken, null));
        Assert.Equal("in a quiet whisper", GeminiTTS.Style(Tone.Whispered, LineKind.Spoken, null));
        Assert.Equal("quietly, as a private thought", GeminiTTS.Style(Tone.Neutral, LineKind.Thought, null));
        Assert.Equal("sad and subdued, close to tears, barely audible", GeminiTTS.Style(Tone.Sad, LineKind.Spoken, "barely audible"));
        Assert.Equal("<sigh>", GeminiTTS.Tag(VocalSound.Sigh));
        Assert.Equal("<sob>", GeminiTTS.Tag(VocalSound.Sniffle));
    }

    static byte[] Wav(short[] samples)
    {
        var d = new List<byte>();
        void U32(int v) => d.AddRange(BitConverter.GetBytes((uint)v));
        void U16(int v) => d.AddRange(BitConverter.GetBytes((ushort)v));
        d.AddRange("RIFF"u8.ToArray()); U32(36 + samples.Length * 2);
        d.AddRange("WAVEfmt "u8.ToArray()); U32(16); U16(1); U16(1); U32(24_000); U32(48_000); U16(2); U16(16);
        // An odd-sized extra chunk before the data, padded, as some encoders write.
        d.AddRange("LIST"u8.ToArray()); U32(3); d.AddRange([1, 2, 3, 0]);
        d.AddRange("data"u8.ToArray()); U32(samples.Length * 2);
        foreach (var s in samples) d.AddRange(BitConverter.GetBytes(s));
        return d.ToArray();
    }

    [Fact]
    public void AudioFromTheReplyAndWavSamples()
    {
        var w = Wav([1, -2, 3]);
        var reply = new JsonObject
        {
            ["steps"] = new JsonArray(
                new JsonObject { ["type"] = "user_input", ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = "Not here." }) },
                new JsonObject { ["type"] = "model_output", ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(w) }) }),
        };
        var audio = GeminiTTS.AudioFromResponse(Encoding.UTF8.GetBytes(reply.ToJsonString()));
        Assert.Equal(w, audio);
        var pcm = GeminiTTS.Pcm16FromWavOrRaw(audio);
        Assert.Equal(6, pcm.Length);
        Assert.Equal(-2, BitConverter.ToInt16(pcm, 2));
        // Raw PCM passes through.
        Assert.Equal(new byte[] { 1, 0, 2, 0 }, GeminiTTS.Pcm16FromWavOrRaw([1, 0, 2, 0]));
    }

    [Fact]
    public void AReplyWithoutAudioSaysWhy()
    {
        var reply = """{"steps":[{"type":"model_output","content":[{"type":"text","text":"I can’t read that."}]}]}""";
        var e = Assert.Throws<GeminiTTS.NoAudioException>(() => GeminiTTS.AudioFromResponse(Encoding.UTF8.GetBytes(reply)));
        Assert.Equal("I can’t read that.", e.Said);
        var err = Encoding.UTF8.GetBytes("""{"error":{"code":400,"message":"API key not valid.","status":"INVALID_ARGUMENT"}}""");
        Assert.Equal("API key not valid.", GeminiTTS.ErrorMessage(err)?.Message);
        Assert.Equal("INVALID_ARGUMENT", GeminiTTS.ErrorMessage(err)?.Status);
        Assert.Null(GeminiTTS.ErrorMessage("not json"u8.ToArray()));
    }

    [Fact]
    public void CostsPerSecondAndThePriceRise()
    {
        var late2026 = new DateTime(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
        var early2027 = new DateTime(2027, 1, 2, 0, 0, 0, DateTimeKind.Utc);
        Assert.Equal(0.000225, GeminiModel.Flash.DollarsPerSecond(late2026), 9);
        Assert.Equal(0.00045, GeminiModel.Flash.DollarsPerSecond(early2027), 9);
        Assert.Equal(0.00015, GeminiModel.FlashLite.DollarsPerSecond(late2026), 9);
        // A 10-hour book with Flash in 2026: about $8.
        Assert.Equal(8.1, 36_000 * GeminiModel.Flash.DollarsPerSecond(late2026), 2);
        Assert.Equal(GeminiModel.FlashLite, GeminiTTS.ModelFrom("gemini-3.8-flash-lite-tts"));
        Assert.Null(GeminiTTS.ModelFrom("gemini-unknown"));
    }

    [Fact]
    public void UsageRecordsCostAndOldRecordsStillRead()
    {
        var r = new CloudUsageRecord("2026-11-14", "b", CloudService.Google, "gemini-3.8-flash-tts") { AudioSeconds = 100 };
        Assert.NotNull(r.Cost);
        var t = r with { };
        t.Add(r);
        Assert.Equal(200, t.AudioSeconds);
        var old = """{"day":"2026-11-01","book":"b","service":"elevenlabs","model":"eleven_v4","inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"characters":12,"requests":1}""";
        var decoded = JsonSerializer.Deserialize<CloudUsageRecord>(old, ProjectJson.Options)!;
        Assert.Null(decoded.AudioSeconds);
        Assert.Equal(12, decoded.Characters);
        // The Mac's keys: service "google", audioSeconds only when there are some.
        var json = JsonSerializer.Serialize(CloudUsageRecord.Google("b", "gemini-3.8-flash-tts", 9, 0.6), ProjectJson.Options);
        Assert.Contains("\"service\":\"google\"", json);
        Assert.Contains("\"audioSeconds\":0.6", json);
        Assert.DoesNotContain("audioSeconds", JsonSerializer.Serialize(decoded, ProjectJson.Options));
        var mac = """{"day":"2026-11-14","book":"b","service":"google","model":"gemini-3.8-flash-tts","inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"characters":9,"audioSeconds":0.6,"requests":1}""";
        var fromMac = JsonSerializer.Deserialize<CloudUsageRecord>(mac, ProjectJson.Options)!;
        Assert.Equal(CloudService.Google, fromMac.Service);
        Assert.Equal(0.6, fromMac.AudioSeconds);
    }

    [Fact]
    public void EachCompanyHasItsOwnYes()
    {
        var s = new ProjectSettings();
        Assert.True(!s.AllowsCloud(VoiceEngine.Elevenlabs) && !s.AllowsCloud(VoiceEngine.Gemini) && s.AllowsCloud(VoiceEngine.Chatterbox));
        s.CloudVoicesAllowed = AppleDate.Now;
        Assert.True(s.AllowsCloud(VoiceEngine.Elevenlabs) && !s.AllowsCloud(VoiceEngine.Gemini));
        s.GoogleVoicesAllowed = AppleDate.Now;
        Assert.True(s.AllowsCloud(VoiceEngine.Gemini) && s.AllowsAnyCloudVoices);
        Assert.True(VoiceEngine.Gemini.IsCloud() && VoiceEngine.Gemini.CloudCompany() == "Google");
        Assert.Equal("ElevenLabs", VoiceEngine.Elevenlabs.CloudCompany());
        Assert.True(!VoiceEngine.Chatterbox.IsCloud() && VoiceEngine.Chatterbox.HasChineseOrigin() && VoiceEngine.Qwen.HasChineseOrigin());
        Assert.False(VoiceEngine.Pocket.HasChineseOrigin());
        Assert.Equal(["apple", "pocket", "qwen", "chatterbox", "orpheus", "elevenlabs", "gemini"], VoiceEngines.Engines.Select(e => e.Raw()));
    }

    /// Projects move between the Mac and a PC: Google's permission is saved as the Mac's
    /// `googleVoicesAllowed`, the new engines by the Mac's names, and both read back.
    [Fact]
    public void TheFileFormatMatchesTheMac()
    {
        var p = new Project { Title = "Placeholder" };
        p.Settings.GoogleVoicesAllowed = AppleDate.Now;
        p.Settings.VoiceEngine = VoiceEngine.Gemini;
        p.Settings.SetNarratorVoice(new CastVoice(new VoiceChoice(VoiceEngine.Chatterbox, ChatterboxVoice.BuiltInID), 1, VoiceSource.User), VoiceEngine.Chatterbox);
        var json = Encoding.UTF8.GetString(ProjectJson.Encode(p));
        Assert.Contains("\"googleVoicesAllowed\":", json);
        Assert.Contains("\"voiceEngine\":\"gemini\"", json);
        Assert.Contains("\"chatterbox\":{", json);
        Assert.Contains("\"engine\":\"chatterbox\"", json);
        var back = ProjectJson.Decode(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(back.Settings.GoogleVoicesAllowed);
        Assert.Null(back.Settings.CloudVoicesAllowed);
        Assert.Equal(VoiceEngine.Gemini, back.Settings.VoiceEngine);
        Assert.Equal(ChatterboxVoice.BuiltInID, back.Settings.NarratorVoice(VoiceEngine.Chatterbox)!.Choice.VoiceID);
        Assert.NotNull(back.Settings.Copy().GoogleVoicesAllowed);
    }

    /// The resolver gives each request its own company's yes: a book that allows ElevenLabs
    /// never lets a Gemini voice speak, and the reverse.
    [Fact]
    public void TheResolverAsksEachCompanySeparately()
    {
        var p = new Project { Title = "Placeholder" };
        p.Settings.CloudVoicesAllowed = AppleDate.Now;
        var switches = new EngineSwitches();
        var r = new VoiceResolver(p, VoiceEngine.Mixed, switches: switches);
        Assert.Contains(VoiceEngine.Elevenlabs, r.CloudAllowed);
        Assert.DoesNotContain(VoiceEngine.Gemini, r.CloudAllowed);
        Assert.DoesNotContain(VoiceEngine.Gemini, r.Usable);
        r.Narrator = new VoiceChoice(VoiceEngine.Gemini, "Kore");
        var seg = new SpeechSegment { Text = "Hello.", LineID = Guid.NewGuid(), Kind = LineKind.Spoken, Delivery = "warmly" };
        var req = r.RequestFor(seg)!;
        Assert.False(req.CloudAllowed);
        Assert.Equal("warmly", req.Delivery); // Gemini takes delivery hints, as ElevenLabs does
        r.Narrator = new VoiceChoice(VoiceEngine.Elevenlabs, "prem1");
        Assert.True(r.RequestFor(seg)!.CloudAllowed);
        r.Narrator = new VoiceChoice(VoiceEngine.Chatterbox, ChatterboxVoice.BuiltInID);
        Assert.Null(r.RequestFor(seg)!.Delivery);
        // The cache key covers Gemini's sound and delivery.
        var a = new SpeechRequest { Text = "Hi.", Choice = new VoiceChoice(VoiceEngine.Gemini, "Kore"), Delivery = "softly" };
        Assert.NotEqual(a.CacheKey, (a with { Delivery = null }).CacheKey);
        Assert.NotEqual(a.CacheKey, (a with { Sound = VocalSound.Sigh }).CacheKey);
    }
}

public class ChatterboxTests
{
    [Fact]
    public void TenVoicesSharingPocketClips()
    {
        Assert.Equal(VoiceInfo.Pocket.Count + 1, VoiceInfo.Chatterbox.Count);
        Assert.Equal(ChatterboxVoice.BuiltInID, VoiceInfo.Chatterbox[0].VoiceID);
        Assert.All(VoiceInfo.Chatterbox, v => Assert.True(v.Engine == VoiceEngine.Chatterbox && v.VoiceID.StartsWith(PocketVoice.StockPrefix)));
        Assert.NotNull(PocketClips.StockClip("chatterbox"));
    }

    [Fact]
    public void TonesStayInTheSteadyRange()
    {
        foreach (var tone in Tones.All)
        {
            var d = ChatterboxDelivery.For(tone);
            Assert.InRange(d.Exaggeration, 0.3, 0.8);
            Assert.InRange(d.Rate, 0.95, 1.0);
        }
        Assert.Equal(ChatterboxDelivery.Even, ChatterboxDelivery.For(Tone.Angry, narration: true));
        Assert.Equal(0.7, ChatterboxDelivery.For(Tone.Whispered).Volume);
        Assert.Equal(1, ChatterboxDelivery.Even.Rate);
    }

    [Fact]
    public void RunOnAfterAShortLineIsCut()
    {
        const double rate = 24_000;
        var tone = Enumerable.Range(0, (int)(rate * 1.8)).Select(i => (float)Math.Sin(i * 0.05) * 0.5f).ToArray();
        var gap = new float[(int)(rate * 0.6)];
        var more = Enumerable.Range(0, (int)(rate * 1.0)).Select(i => (float)Math.Sin(i * 0.07) * 0.5f).ToArray();
        var output = ChatterboxRunOn.Trim([.. tone, .. gap, .. more], rate, "The path was underwater.");
        Assert.True(output.Length < tone.Length + gap.Length);
        Assert.True(output.Length >= tone.Length);
        // A pause before the line has had time to be said (a comma) is kept.
        var longLine = "He walked to the window, and looked out over the empty street for a long time.";
        Assert.Equal(tone.Length + gap.Length + more.Length, ChatterboxRunOn.Trim([.. tone, .. gap, .. more], rate, longLine).Length);
    }

    [Fact]
    public void AgeNudgesApplyToStockVoicesNotClones()
    {
        var stock = VoiceInfo.Chatterbox[1];
        Assert.Equal(1.19, VoiceCasting.AgeAdjusted(stock, VoiceAge.Child).Pitch);
        var clone = new VoiceInfo(VoiceEngine.Chatterbox, "clone:abc", "Me", Gender.Female, Accent: "");
        Assert.Null(VoiceCasting.AgeAdjusted(clone, VoiceAge.Child).Pitch);
    }

    /// Chatterbox's tokenizer: characters merged by rank, a word at a time, spaces as their own
    /// token, sound tags whole, unknown characters as [UNK], and the template around the text.
    [Fact]
    public void TheTokenizerMergesCharactersAndWrapsTheTemplate()
    {
        var vocab = new Dictionary<string, int> { ["[STOP]"] = 0, ["[UNK]"] = 1, [" "] = 2, ["."] = 9, ["H"] = 20, ["e"] = 18, ["l"] = 25, ["o"] = 28, ["he"] = 40, ["ll"] = 41, ["llo"] = 42, ["[START]"] = 255 };
        var tok = new ChatterboxTokenizer(vocab, [("l", "l"), ("ll", "o"), ("h", "e")],
            new Dictionary<string, int> { ["[STOP]"] = 0, ["[UNK]"] = 1, ["[START]"] = 255, ["[sigh]"] = 611 });
        Assert.Equal([20, 18, 42, 2, 18, 41, 9], tok.Encode("Hello  ell."));
        Assert.Equal([611, 2, 1], tok.Encode("[sigh] z"));
        Assert.Equal([ChatterboxModel.ExaggerationToken, 255, 20, 18, 42, 0, ChatterboxModel.StartSpeech, ChatterboxModel.StartSpeech], tok.Prompt("Hello"));
    }

    [Fact]
    public void TextIsTidiedTheWayChatterboxExpects()
    {
        Assert.Equal("Where were you?", ChatterboxText.Normalize("where were you?"));
        Assert.Equal("Wait, then go.", ChatterboxText.Normalize("Wait… then go"));
        Assert.Equal("He said \"no\".", ChatterboxText.Normalize("He  said “no”"));
        Assert.Equal("Fine.", ChatterboxText.Normalize("Fine"));
        Assert.Equal("", ChatterboxText.Normalize("   "));
    }

    [Fact]
    public void TheTokenBudgetFollowsTheLine()
    {
        Assert.Equal(100, ChatterboxModel.TokenBudget("No."));
        Assert.InRange(ChatterboxModel.TokenBudget(new string('a', 168)), 700, 760);
        Assert.Equal(ChatterboxModel.MaxTokens, ChatterboxModel.TokenBudget(new string('a', 5000)));
    }

    static float[] Sine(double hz, double seconds, float amp = 0.05f) =>
        Enumerable.Range(0, (int)(seconds * SpeechAudio.SampleRate)).Select(i => amp * (float)Math.Sin(2 * Math.PI * hz * i / SpeechAudio.SampleRate)).ToArray();

    /// The engine: every voice from its clip (stock or cloned), the tone's exaggeration, the
    /// run-on cut, leveled, whispered quieter, the pace stretched; nothing downloaded.
    [Fact]
    public async Task ChatterboxSpeaksEveryLineFromTheVoicesClipInItsTone()
    {
        var said = new List<ChatterboxLine>();
        var engine = new ChatterboxEngine
        {
            AssumeReady = true,
            // The line, then a pause and a run-on, as the model sometimes does.
            Speaker = (line, ct) => { lock (said) said.Add(line); return [.. Sine(200, 1.6), .. new float[(int)(0.6 * SpeechAudio.SampleRate)], .. Sine(200, 1.0)]; },
        };
        Assert.True(engine.ShadesPitch && engine.ShadesPace);
        Assert.Equal(VoiceInfo.Chatterbox.Count + LocalClones.Shared.All.Count, engine.Voices.Count);
        var own = new VoiceChoice(VoiceEngine.Chatterbox, ChatterboxVoice.BuiltInID);
        var angry = new SpeechRequest { Text = "Where were you?", Choice = own, Kind = LineKind.Spoken, Tone = Tone.Angry };
        var a = await engine.Render(angry, default);
        var line = Assert.Single(said);
        Assert.Equal(0.8, line.Exaggeration);
        Assert.Equal(Sampler.Seed("Where were you?" + ChatterboxVoice.BuiltInID), line.Seed);
        Assert.Equal(PocketClips.StockClip("chatterbox")!.Length, line.Clip.Length);
        // The run-on after the pause is gone, and the rest stretched for the CFG weight.
        Assert.InRange(SpeechAudio.Seconds(a), 1.6 / ChatterboxDelivery.For(Tone.Angry).Rate, 1.6 / ChatterboxDelivery.For(Tone.Angry).Rate + 0.2);
        // Narration is even; whispered is softer.
        await engine.Render(angry with { Kind = null }, default);
        Assert.Equal(0.5, said[^1].Exaggeration);
        var whispered = await engine.Render(angry with { Tone = Tone.Whispered }, default);
        Assert.Equal(0.3, said[^1].Exaggeration);
        double Rms(float[] x) => Math.Sqrt(x.Average(v => (double)v * v));
        Assert.InRange(Rms(whispered) / Rms(a), 0.6, 0.8);
        // A Pocket TTS stock voice works too; a voice it doesn't have says so.
        await engine.Render(angry with { Choice = new VoiceChoice(VoiceEngine.Chatterbox, "stock:dan") }, default);
        Assert.Equal(PocketClips.StockClip("dan")!.Length, said[^1].Clip.Length);
        await Assert.ThrowsAsync<SpeechException>(() => engine.Render(angry with { Choice = new VoiceChoice(VoiceEngine.Chatterbox, "preset:narrator") }, default));
        // Not downloaded: says where to get it.
        var missing = await Assert.ThrowsAsync<SpeechException>(() => new ChatterboxEngine().Render(angry, default));
        Assert.Contains("Chatterbox", missing.Message);
    }

    /// With INNERVOICE_TEST_VOICE_MODELS pointing at a folder with chatterbox/ in it (the pack's
    /// files), a real line goes through the ONNX models.
    [Fact]
    public void TheRealModelSpeaks()
    {
        var folder = Environment.GetEnvironmentVariable("INNERVOICE_TEST_VOICE_MODELS");
        if (folder is null || !File.Exists(Path.Combine(folder, "chatterbox", "onnx", ChatterboxModel.LanguageModelFile))) return;
        using var model = new ChatterboxModel(Path.Combine(folder, "chatterbox"));
        var voice = model.Encode(PocketClips.StockClip("chatterbox")!);
        var audio = model.Speak("The rain had not stopped for three days.", voice, 0.5, 1, default);
        Assert.InRange(audio.Length / (double)SpeechAudio.SampleRate, 1.2, 6);
        Assert.True(Pitch.Analyze(audio).Voiced > 0.3);
    }
}
