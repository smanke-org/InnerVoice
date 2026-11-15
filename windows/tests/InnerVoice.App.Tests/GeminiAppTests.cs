using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Xunit;
using InnerVoice.App.Document;
using InnerVoice.App.Export;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Tests;

/// Gemini TTS against a fake Google (the Mac's SelfTestGemini and FakeGemini): nothing goes to
/// Google without a key, the narrator's yes and the book's own Google permission (separate
/// from ElevenLabs'); lines go out in the Interactions API's shape with the tone as a
/// direction, the WAV that comes back plays, usage is counted in seconds with a cost, errors
/// say what to do, and Stop Sending This Book ends it.
[Collection("Model choice")]
public class GeminiAppTests
{
    sealed class FakeGemini : HttpMessageHandler
    {
        public List<JsonNode> Speech { get; } = [];
        public List<string> Keys { get; } = [];
        public int Checks;
        public int Status = 200;

        public static byte[] Wav(double seconds)
        {
            int n = (int)(24_000 * seconds);
            var d = new List<byte>();
            void U32(int v) => d.AddRange(BitConverter.GetBytes((uint)v));
            void U16(int v) => d.AddRange(BitConverter.GetBytes((ushort)v));
            d.AddRange("RIFF"u8.ToArray()); U32(36 + n * 2);
            d.AddRange("WAVEfmt "u8.ToArray()); U32(16); U16(1); U16(1); U32(24_000); U32(48_000); U16(2); U16(16);
            d.AddRange("data"u8.ToArray()); U32(n * 2);
            for (int i = 0; i < n; i++) d.AddRange(BitConverter.GetBytes((short)(Math.Sin(i * 0.06) * 8000)));
            return d.ToArray();
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("generativelanguage.googleapis.com", request.RequestUri!.Host);
            lock (Keys) Keys.Add(request.Headers.TryGetValues("x-goog-api-key", out var k) ? k.Single() : "");
            var path = request.RequestUri.AbsolutePath;
            int code = Status;
            string body;
            if (path.EndsWith("/voices"))
            {
                Checks++;
                code = 200;
                body = """{"voices":[{"id":"Kore","display_name":"Kore","gender":"female"}]}""";
            }
            else if (path.EndsWith("/interactions"))
            {
                if (code == 200)
                {
                    var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                    lock (Speech) Speech.Add(sent);
                    body = new JsonObject
                    {
                        ["steps"] = new JsonArray(new JsonObject
                        {
                            ["type"] = "model_output",
                            ["content"] = new JsonArray(new JsonObject { ["type"] = "audio", ["mime_type"] = "audio/wav", ["data"] = Convert.ToBase64String(Wav(0.6)) }),
                        }),
                    }.ToJsonString();
                }
                else if (code == 400) body = """{"error":{"code":400,"message":"API key not valid. Please pass a valid API key.","status":"INVALID_ARGUMENT"}}""";
                else body = """{"error":{"code":429,"message":"You exceeded your current quota.","status":"RESOURCE_EXHAUSTED"}}""";
            }
            else { code = 404; body = "{}"; }
            return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static SpeechRequest Request(bool allowed) => new()
    {
        Text = "Not here.", Choice = new VoiceChoice(VoiceEngine.Gemini, "Kore"), Tone = Tone.Whispered, Kind = LineKind.Spoken,
        Delivery = "barely audible", Sound = VocalSound.Sigh, CloudAllowed = allowed, UsageBook = "Placeholder Book|1",
    };

    [AvaloniaFact]
    public async Task NothingGoesToGoogleWithoutKeyConsentAndTheBooksOwnYes()
    {
        VoiceSetup.Start();
        var fake = new FakeGemini();
        GeminiVoices.TestHttp = new HttpClient(fake);
        var gemini = GeminiVoices.Shared;
        gemini.Disconnect();
        Pump();
        var renderer = SpeechRenderer.Shared;
        try
        {
            Assert.False(gemini.IsSetUp);
            Assert.False(EngineSwitches.Shared.IsOn(VoiceEngine.Gemini));
            Assert.DoesNotContain(VoiceEngine.Gemini, EngineSwitches.Shared.Choices);
            await Assert.ThrowsAsync<SpeechException>(() => gemini.Render(Request(true), default));
            gemini.Save("test-key", consent: false);
            await Assert.ThrowsAsync<SpeechException>(() => gemini.Render(Request(true), default));

            // Check Key asks Google for the voice list, and sends no book text.
            await GeminiVoices.Check("test-key");
            Assert.Equal(1, fake.Checks);
            Assert.Empty(fake.Speech);
            gemini.Save("test-key", consent: true);
            EngineSwitches.Shared.Set(VoiceEngine.Gemini, true);
            Pump();
            Assert.Contains(VoiceEngine.Gemini, EngineSwitches.Shared.Choices);
            Assert.Equal(30, EngineSwitches.Shared.VoicesFor(VoiceEngine.Gemini).Count);

            // Set up, and the book allows ElevenLabs, but not Google: nothing may go to Google.
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Purpose", p => { p.Settings.Purpose = ProjectPurpose.AiVoices; });
            doc.SetCloudVoicesAllowed(true);
            doc.SetVoiceEngine(VoiceEngine.Gemini);
            Assert.NotEqual(VoiceEngine.Gemini, doc.Project.EngineInUse());
            Assert.False(CloudInUse.Gemini(doc.Project));
            var segs = SpeechScript.Segments([0], doc.Project, doc.Index, includeNarration: true);
            var lines = segs.Where(s => s.LineID is not null).Take(2).ToList();
            renderer.ClearCache();
            var r0 = VoiceSetup.Resolver(doc);
            foreach (var seg in lines) try { if (r0.RequestFor(seg) is { } q) await renderer.Render(q); } catch (SpeechException) { }
            // A request that names a Gemini voice but carries no permission is refused too.
            var refused = await Assert.ThrowsAsync<SpeechException>(() => renderer.Render(Request(false) with { Delivery = null, Sound = null }));
            Assert.Contains("hasn’t been allowed", refused.Message);
            var mixed = VoiceSetup.Resolver(doc, VoiceEngine.Mixed);
            Assert.DoesNotContain(VoiceEngine.Gemini, mixed.Usable);
            Assert.DoesNotContain(VoiceEngine.Gemini, mixed.CloudAllowed);
            Assert.Empty(fake.Speech);
            doc.SetCloudVoicesAllowed(false);

            // The book allows Google (and only Google).
            doc.SetCloudAllowed(VoiceEngine.Gemini, true);
            doc.SetVoiceEngine(VoiceEngine.Gemini);
            Assert.Equal(VoiceEngine.Gemini, doc.Project.EngineInUse());
            Assert.Null(doc.Project.Settings.CloudVoicesAllowed);
            Assert.True(CloudInUse.Gemini(doc.Project));
            var speaking = doc.Project.Characters.Where(c => (doc.Index.StatsFor(c.Id)?.Lines ?? 0) > 0).ToList();
            Assert.NotEmpty(speaking);
            Assert.All(speaking, c => Assert.Equal(VoiceEngine.Gemini, c.Voice(VoiceEngine.Gemini)?.Choice.Engine));
            renderer.ClearCache();
            var r = VoiceSetup.Resolver(doc);
            var toned = segs.FirstOrDefault(s => s.LineID is not null && s.Tone != Tone.Neutral) ?? lines[0];
            var req = r.RequestFor(toned)!;
            Assert.True(req.CloudAllowed);
            var audio = await renderer.Render(req);
            Pump();
            var body = fake.Speech[^1];
            Assert.Equal("gemini-3.8-flash-tts", (string?)body["model"]);
            Assert.False((bool)body["store"]!);
            var content = body["input"]![0]!["content"]![0]!;
            Assert.Contains(toned.Text[..Math.Min(8, toned.Text.Length)], (string?)content["text"]);
            if (toned.Tone != Tone.Neutral) Assert.Contains(toned.Tone.Direction(), (string?)content["annotations"]![0]!["style"]);
            Assert.True(SpeechAudio.Seconds(audio) > 0.4);
            Assert.All(fake.Keys, k => Assert.Equal("test-key", k));
            Assert.Contains(CloudUsageStore.Shared.Records, u => u.Service == CloudService.Google && (u.AudioSeconds ?? 0) > 0.4 && u.Cost is not null);

            // Direct: the sound tag goes before the line, the tone and hint as the style.
            fake.Speech.Clear();
            await gemini.Render(Request(true), default);
            var direct = fake.Speech.Single()["input"]![0]!["content"]![0]!;
            Assert.Equal("<sigh> Not here.", (string?)direct["text"]);
            Assert.Equal("in a quiet whisper, barely audible", (string?)direct["annotations"]![0]!["style"]);

            // Errors say what to do.
            fake.Status = 400;
            renderer.ClearCache();
            var badKey = await Assert.ThrowsAsync<SpeechException>(() => renderer.Render(r.RequestFor(lines[^1])!));
            Assert.Contains("API key", badKey.Message);
            fake.Status = 429;
            var quota = await Assert.ThrowsAsync<SpeechException>(() => renderer.Render(r.RequestFor(lines[^1])!));
            Assert.Contains("quota", quota.Message);
            fake.Status = 200;

            // The export notes and the cost estimate.
            Assert.True(AudioExportWindow.GeminiNoteShown(doc.Project));
            // A 10-hour book (540,000 characters) with Flash: about $8.10 in 2026, twice that from 2027.
            Assert.Equal($"About {(DateTime.UtcNow.Year < 2027 ? "$8.10" : "$16.20")} of Gemini TTS for the lines not made yet, at Google’s paid-tier prices.",
                AudioExportWindow.GeminiCostText(36_000 * 15, GeminiModel.Flash));

            // Stop sending this book: nothing more goes to Google.
            doc.SetCloudAllowed(VoiceEngine.Gemini, false);
            int before = fake.Speech.Count;
            renderer.ClearCache();
            var r2 = VoiceSetup.Resolver(doc);
            foreach (var seg in lines) try { if (r2.RequestFor(seg) is { } q) await renderer.Render(q); } catch (SpeechException) { }
            Assert.Equal(before, fake.Speech.Count);
            Assert.NotEqual(VoiceEngine.Gemini, doc.Project.EngineInUse());
            Assert.False(AudioExportWindow.GeminiNoteShown(doc.Project));

            gemini.Disconnect();
            Pump();
            Assert.False(gemini.IsSetUp);
            Assert.DoesNotContain(VoiceEngine.Gemini, EngineSwitches.Shared.Choices);
        }
        finally
        {
            gemini.Disconnect();
            GeminiVoices.TestHttp = null;
            renderer.ClearCache();
            Pump();
        }
    }

    /// Settings › Voices shows Gemini TTS marked Cloud (Google), and the setup window has the
    /// Mac's words; Chatterbox carries its origin mark.
    static List<string> Texts(Avalonia.Controls.Window w) =>
        w.GetLogicalDescendants().Select(c => c switch
        {
            Avalonia.Controls.TextBlock t => t.Text,
            Avalonia.Controls.Button { Content: string s } => s,
            Avalonia.Controls.ContentControl { Content: Avalonia.Controls.TextBlock t } => t.Text,
            _ => null,
        }).OfType<string>().ToList();

    [AvaloniaFact]
    public void SettingsShowGeminiAndChatterboxMarked()
    {
        VoiceSetup.Start();
        var dialog = SettingsWindow.GeminiSetupDialog();
        dialog.Show();
        Pump();
        var texts = Texts(dialog);
        Assert.Contains("Set Up Gemini TTS", texts);
        Assert.Contains("Gemini TTS sends your book to Google", texts);
        Assert.Contains("Turn On Gemini TTS", texts);
        Assert.Contains(texts, t => t.StartsWith("Each book asks before its text is sent the first time, separately from ElevenLabs."));
        dialog.Close();

        var settings = SettingsWindow.Show(null, "Voices");
        Pump();
        var all = Texts(settings);
        Assert.Contains("Gemini TTS", all);
        Assert.Contains("Chatterbox", all);
        Assert.Contains("Uses parts developed in China · Runs on this PC", all);
        Assert.Contains("Developed in China · Runs on this PC", all);
        Assert.Contains(all, t => t.StartsWith("Windows Voices, Pocket TTS, Qwen3-TTS, Chatterbox and Orpheus run on this PC"));
        settings.Close();
        Assert.Contains("Alibaba’s CosyVoice", SettingsWindow.OriginDetail(VoiceEngine.Chatterbox));
    }

    /// A per-minute 429 or an overloaded model is busy (Export Audio waits as long as Google
    /// asks); a daily quota is out of quota.
    [Xunit.Fact]
    public void RateLimitsAreBusyDailyQuotaIsNot()
    {
        var perMinute = Encoding.UTF8.GetBytes("""{"error":{"code":429,"message":"You exceeded your current quota.","status":"RESOURCE_EXHAUSTED","details":[{"@type":"type.googleapis.com/google.rpc.RetryInfo","retryDelay":"27s"}]}}""");
        var e = GeminiVoices.Failure(429, perMinute);
        Assert.True(e.Busy);
        Assert.Equal(GeminiErrors.Busy, e.Message);
        Assert.Equal(TimeSpan.FromSeconds(27), e.RetryAfter);
        var overloaded = GeminiVoices.Failure(503, Encoding.UTF8.GetBytes("""{"error":{"code":503,"message":"The model is overloaded.","status":"UNAVAILABLE"}}"""), TimeSpan.FromSeconds(5));
        Assert.True(overloaded.Busy);
        Assert.Equal(TimeSpan.FromSeconds(5), overloaded.RetryAfter);
        var daily = GeminiVoices.Failure(429, Encoding.UTF8.GetBytes("""{"error":{"code":429,"message":"Quota exceeded.","details":[{"@type":"type.googleapis.com/google.rpc.QuotaFailure","violations":[{"quotaId":"GenerateRequestsPerDayPerProjectPerModel"}]}]}}"""));
        Assert.False(daily.Busy);
        Assert.Equal(GeminiErrors.OutOfQuota, daily.Message);
    }

    /// After Google says busy, requests wait out its delay together and then start one every
    /// 60 / limit seconds (the Mac's GeminiPace).
    [Xunit.Fact]
    public async Task PaceSpacesRequestsAfterALimit()
    {
        var pace = new GeminiPace();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await pace.WaitTurn(default);
        await pace.WaitTurn(default);
        Assert.True(clock.ElapsedMilliseconds < 100);   // no limit yet: at once
        pace.Limited(perMinute: 600, retryAfter: 0.2);   // 0.105 s apart after a 0.2 s wait
        Assert.InRange(pace.Interval, 0.1, 0.11);
        Assert.NotNull(pace.SecondsWaiting());
        clock.Restart();
        await Task.WhenAll(pace.WaitTurn(default), pace.WaitTurn(default), pace.WaitTurn(default));
        Assert.InRange(clock.ElapsedMilliseconds, 380, 3000);
        // An unknown limit grows a guess with each refusal.
        var guess = new GeminiPace();
        guess.Limited(null, 0);
        Assert.Equal(2, guess.Interval);
        guess.Limited(null, 0);
        Assert.Equal(3, guess.Interval);
    }
}
