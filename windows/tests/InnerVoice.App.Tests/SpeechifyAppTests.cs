using System.Net;
using System.Net.Http.Headers;
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

/// Speechify against a fake Speechify (the Mac's SelfTestSpeechify and FakeSpeechify): nothing
/// goes to Speechify without a key, the narrator's yes and the book's own Speechify permission
/// (separate from ElevenLabs' and Google's); lines go out as SSML with the tone as prosody, the
/// WAV that comes back plays, usage is counted in billed characters, a busy reply is tried
/// again, errors say what to do, and Stop Sending This Book ends it.
[Collection("Model choice")]
public class SpeechifyAppTests
{
    sealed class FakeSpeechify : HttpMessageHandler
    {
        public List<JsonNode> Speech { get; } = [];
        public List<string> Keys { get; } = [];
        public int Checks;
        public int Status = 200;
        public bool BusyOnce;
        public bool BusyServed;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.speechify.ai", request.RequestUri!.Host);
            lock (Keys) Keys.Add(request.Headers.Authorization?.ToString() ?? "");
            var path = request.RequestUri.AbsolutePath;
            int code = Status;
            string body;
            var headers = new List<(string, string)>();
            if (path.EndsWith("/voices"))
            {
                Checks++;
                code = 200;
                Assert.Contains("type=shared", request.RequestUri.Query);
                Assert.Contains("locale=en", request.RequestUri.Query);
                body = request.RequestUri.Query.Contains("cursor=p2")
                    ? """{"voices":[{"id":"oliver","display_name":"Oliver","gender":"male","locale":"en-GB","type":"shared","tags":["timbre:deep","calm"]},{"id":"kid","display_name":"Kid","gender":"female","locale":"en-US","type":"shared","tags":["child"]}],"next_cursor":null,"has_more":false}"""
                    : """{"voices":[{"id":"anna","display_name":"Anna","gender":"female","locale":"en-GB","type":"shared","tags":["warm"],"staff_pick_rank":1},{"id":"george","display_name":"George","gender":"male","locale":"en-US","type":"shared","tags":[]},{"id":"mine","display_name":"Mine","gender":"male","locale":"en-US","type":"personal"}],"next_cursor":"p2","has_more":true}""";
            }
            else if (path.EndsWith("/audio/speech"))
            {
                if (BusyOnce && !BusyServed)
                {
                    BusyServed = true;
                    var busy = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
                    {
                        Content = new StringContent("""{"error":{"code":"rate_limited","message":"Too many requests"}}""", Encoding.UTF8, "application/json"),
                    };
                    busy.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(1));
                    return busy;
                }
                if (code == 200)
                {
                    var sent = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                    lock (Speech) Speech.Add(sent);
                    body = new JsonObject
                    {
                        ["audio_data"] = Convert.ToBase64String(Wav(0.6)),
                        ["audio_format"] = "wav",
                        ["billable_characters_count"] = 42,
                        ["speech_marks"] = new JsonObject(),
                    }.ToJsonString();
                }
                else if (code == 401) body = """{"error":{"code":"unauthorized","message":"Invalid API key"}}""";
                else body = """{"error":{"code":"payment_required","message":"Character limit reached"}}""";
            }
            else { code = 404; body = "{}"; }
            return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }

        static byte[] Wav(double seconds)
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
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static SpeechRequest Request(bool allowed) => new()
    {
        Text = "Tom & Jerry.", Choice = new VoiceChoice(VoiceEngine.Speechify, "anna"), Tone = Tone.Whispered, Kind = LineKind.Spoken,
        CloudAllowed = allowed, UsageBook = "Placeholder Book|1",
    };

    [AvaloniaFact]
    public async Task NothingGoesToSpeechifyWithoutKeyConsentAndTheBooksOwnYes()
    {
        VoiceSetup.Start();
        var fake = new FakeSpeechify();
        SpeechifyVoices.TestHttp = new HttpClient(fake);
        var speechify = SpeechifyVoices.Shared;
        speechify.Disconnect();
        SpeechifyPace.Shared.Reset();
        Pump();
        var renderer = SpeechRenderer.Shared;
        try
        {
            Assert.False(speechify.IsSetUp);
            Assert.False(EngineSwitches.Shared.IsOn(VoiceEngine.Speechify));
            Assert.DoesNotContain(VoiceEngine.Speechify, EngineSwitches.Shared.Choices);
            await Assert.ThrowsAsync<SpeechException>(() => speechify.Render(Request(true), default));
            speechify.Save("sp-key", consent: false);
            await Assert.ThrowsAsync<SpeechException>(() => speechify.Render(Request(true), default));

            // Check Key lists the voices (both pages), and sends no book text.
            await SpeechifyVoices.Check("sp-key");
            Assert.Equal(2, fake.Checks);
            Assert.Empty(fake.Speech);
            speechify.Save("sp-key", consent: true);
            EngineSwitches.Shared.Set(VoiceEngine.Speechify, true);
            Pump();
            Assert.Contains(VoiceEngine.Speechify, EngineSwitches.Shared.Choices);
            // Shared voices only (the personal clone is left out), staff picks first.
            var voices = EngineSwitches.Shared.VoicesFor(VoiceEngine.Speechify);
            Assert.Equal(4, voices.Count);
            Assert.Equal("Anna", voices[0].Name);
            Assert.True(voices[0] is { Gender: Gender.Female, Accent: "British" });
            Assert.DoesNotContain(voices, v => v.VoiceID == "mine");
            Assert.Equal(VoiceAge.Child, voices.Single(v => v.VoiceID == "kid").Age);

            // Set up, and the book allows ElevenLabs and Google, but not Speechify: nothing may
            // go to Speechify.
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Purpose", p => { p.Settings.Purpose = ProjectPurpose.AiVoices; });
            doc.SetCloudVoicesAllowed(true);
            doc.SetCloudAllowed(VoiceEngine.Gemini, true);
            doc.SetVoiceEngine(VoiceEngine.Speechify);
            // Speechify stays chosen, but nothing plays until the book allows Speechify.
            Assert.Equal(VoiceEngine.Speechify, doc.Project.EngineInUse());
            Assert.False(await CloudGate.Allows(doc));
            Assert.False(CloudInUse.Speechify(doc.Project));
            Assert.False(AudioExportWindow.SpeechifyNoteShown(doc.Project));
            var segs = SpeechScript.Segments([0], doc.Project, doc.Index, includeNarration: true);
            var lines = segs.Where(s => s.LineID is not null).Take(2).ToList();
            renderer.ClearCache();
            var r0 = VoiceSetup.Resolver(doc);
            foreach (var seg in lines) try { if (r0.RequestFor(seg) is { } q) await renderer.Render(q); } catch (SpeechException) { }
            // A request that names a Speechify voice but carries no permission is refused too.
            var refused = await Assert.ThrowsAsync<SpeechException>(() => renderer.Render(Request(false)));
            Assert.Contains("hasn’t been allowed", refused.Message);
            var mixed = VoiceSetup.Resolver(doc, VoiceEngine.Mixed);
            Assert.DoesNotContain(VoiceEngine.Speechify, mixed.Usable);
            Assert.DoesNotContain(VoiceEngine.Speechify, mixed.CloudAllowed);
            Assert.Empty(fake.Speech);
            doc.SetCloudVoicesAllowed(false);
            doc.SetCloudAllowed(VoiceEngine.Gemini, false);

            // The book allows Speechify (and only Speechify): its own flag, not ElevenLabs' or Google's.
            doc.SetCloudAllowed(VoiceEngine.Speechify, true);
            doc.SetVoiceEngine(VoiceEngine.Speechify);
            Assert.NotNull(doc.Project.Settings.SpeechifyVoicesAllowed);
            Assert.Null(doc.Project.Settings.CloudVoicesAllowed);
            Assert.Null(doc.Project.Settings.GoogleVoicesAllowed);
            Assert.True(CloudInUse.Speechify(doc.Project));
            Assert.True(AudioExportWindow.SpeechifyNoteShown(doc.Project));
            var speaking = doc.Project.Characters.Where(c => (doc.Index.StatsFor(c.Id)?.Lines ?? 0) > 0).ToList();
            Assert.NotEmpty(speaking);
            Assert.All(speaking, c => Assert.Equal(VoiceEngine.Speechify, c.Voice(VoiceEngine.Speechify)?.Choice.Engine));
            renderer.ClearCache();
            var r = VoiceSetup.Resolver(doc);
            var toned = segs.FirstOrDefault(s => s.LineID is not null && s.Tone != Tone.Neutral) ?? lines[0];
            var req = r.RequestFor(toned)!;
            Assert.True(req.CloudAllowed);
            var audio = await renderer.Render(req);
            Pump();
            var body = fake.Speech[^1];
            Assert.Equal(SpeechifyTTS.Model, (string?)body["model"]);
            Assert.Equal("wav_24000", (string?)body["output_format"]);
            var input = (string?)body["input"] ?? "";
            Assert.StartsWith("<speak>", input);
            Assert.Contains(SpeechifyTTS.Ssml(toned.Text, null, null)[7..][..Math.Min(6, toned.Text.Length)], input);
            if (toned.Tone != Tone.Neutral) Assert.Contains("<prosody", input);
            Assert.True(SpeechAudio.Seconds(audio) > 0.4);
            Assert.All(fake.Keys, k => Assert.Equal("Bearer sp-key", k));
            Assert.Contains(CloudUsageStore.Shared.Records, u => u.Service == CloudService.Speechify && u.Characters >= 42 && u.Requests > 0);

            // Direct: escaped SSML with the tone as prosody; no delivery hint or sound is sent.
            fake.Speech.Clear();
            await speechify.Render(Request(true), default);
            Assert.Equal("<speak><prosody rate=\"-8%\" pitch=\"-2%\" volume=\"x-soft\">Tom &amp; Jerry.</prosody></speak>", (string?)fake.Speech.Single()["input"]);

            // Long text goes in pieces of at most 1,800 characters.
            fake.Speech.Clear();
            var longText = string.Join(" ", Enumerable.Repeat("The rain kept falling on the old roof all night long.", 80));
            await speechify.Render(Request(true) with { Text = longText }, default);
            Assert.True(fake.Speech.Count >= 3);
            Assert.All(fake.Speech, s => Assert.True(((string?)s["input"] ?? "").Length < 1900));

            // Busy: Speechify's 429 is waited out and tried again.
            fake.Speech.Clear();
            fake.BusyOnce = true;
            await speechify.Render(Request(true), default);
            Assert.True(fake.BusyServed);
            Assert.Single(fake.Speech);
            Assert.True(SpeechifyPace.Shared.Interval >= 1);
            SpeechifyPace.Shared.Reset();

            // Errors say what to do.
            fake.Status = 401;
            renderer.ClearCache();
            var badKey = await Assert.ThrowsAsync<SpeechException>(() => renderer.Render(r.RequestFor(lines[^1])!));
            Assert.Contains("API key", badKey.Message);
            fake.Status = 402;
            var empty = await Assert.ThrowsAsync<SpeechException>(() => renderer.Render(r.RequestFor(lines[^1])!));
            Assert.Contains("characters left", empty.Message);
            fake.Status = 200;

            // Stop sending this book: nothing more goes to Speechify.
            doc.SetCloudAllowed(VoiceEngine.Speechify, false);
            Assert.Null(doc.Project.Settings.SpeechifyVoicesAllowed);
            int before = fake.Speech.Count;
            renderer.ClearCache();
            var r2 = VoiceSetup.Resolver(doc);
            foreach (var seg in lines) try { if (r2.RequestFor(seg) is { } q) await renderer.Render(q); } catch (SpeechException) { }
            Assert.Equal(before, fake.Speech.Count);
            Assert.False(await CloudGate.Allows(doc));
            Assert.False(AudioExportWindow.SpeechifyNoteShown(doc.Project));

            speechify.Disconnect();
            Pump();
            Assert.False(speechify.IsSetUp);
            Assert.DoesNotContain(VoiceEngine.Speechify, EngineSwitches.Shared.Choices);
        }
        finally
        {
            speechify.Disconnect();
            SpeechifyVoices.TestHttp = null;
            SpeechifyPace.Shared.Reset();
            renderer.ClearCache();
            Pump();
        }
    }

    static List<string> Texts(Avalonia.Controls.Window w) =>
        w.GetLogicalDescendants().Select(c => c switch
        {
            Avalonia.Controls.TextBlock t => t.Text,
            Avalonia.Controls.Button { Content: string s } => s,
            Avalonia.Controls.ContentControl { Content: Avalonia.Controls.TextBlock t } => t.Text,
            _ => null,
        }).OfType<string>().ToList();

    /// Settings › Voices shows Speechify marked Cloud, and the setup window has the Mac's words.
    [AvaloniaFact]
    public void SettingsShowSpeechifyMarked()
    {
        VoiceSetup.Start();
        var dialog = SettingsWindow.SpeechifySetupDialog();
        dialog.Show();
        Pump();
        var texts = Texts(dialog);
        Assert.Contains("Set Up Speechify", texts);
        Assert.Contains("Speechify sends your book to Speechify", texts);
        Assert.Contains("Turn On Speechify", texts);
        Assert.Contains("Open Speechify API Keys", texts);
        Assert.Contains(texts, t => t.StartsWith("Each book asks before its text is sent the first time, separately from ElevenLabs and Google."));
        dialog.Close();

        var settings = SettingsWindow.Show(null, "Voices");
        Pump();
        var all = Texts(settings);
        Assert.Contains("Speechify", all);
        Assert.Contains(all, t => t.Contains("ElevenLabs, Gemini TTS and Speechify are different"));
        settings.Close();
    }

    /// 401 is the key, 402 is out of characters, 429 and 502–504 are busy (with Speechify's
    /// Retry-After), anything else Speechify's own words.
    [Xunit.Fact]
    public void ErrorsSayWhatToDo()
    {
        var bad = SpeechifyVoices.Failure(401, Encoding.UTF8.GetBytes("""{"error":{"code":"unauthorized","message":"Invalid API key"}}"""));
        Assert.Equal(SpeechifyErrors.BadKey, bad.Message);
        Assert.False(bad.Busy);
        Assert.Equal(SpeechifyErrors.OutOfCharacters, SpeechifyVoices.Failure(402, Encoding.UTF8.GetBytes("{}")).Message);
        var busy = SpeechifyVoices.Failure(429, Encoding.UTF8.GetBytes("{}"), TimeSpan.FromSeconds(3));
        Assert.True(busy.Busy);
        Assert.Equal(TimeSpan.FromSeconds(3), busy.RetryAfter);
        Assert.True(SpeechifyVoices.Failure(504, []).Busy);
        var other = SpeechifyVoices.Failure(400, Encoding.UTF8.GetBytes("""{"error":{"code":"invalid_request","message":"Unknown voice"}}"""));
        Assert.Equal("Speechify couldn’t make the audio (400): Unknown voice.", other.Message);
        Assert.False(other.Busy);
    }

    /// After Speechify says busy, requests wait out its Retry-After together and then start a
    /// second apart (the Mac's SpeechifyPace).
    [Xunit.Fact]
    public async Task PaceSpacesRequestsAfterBusy()
    {
        var pace = new SpeechifyPace();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await pace.WaitTurn(default);
        await pace.WaitTurn(default);
        Assert.True(clock.ElapsedMilliseconds < 100);   // not busy yet: at once
        pace.Limited(0.2);
        Assert.Equal(1.05, pace.Interval);
        clock.Restart();
        await Task.WhenAll(pace.WaitTurn(default), pace.WaitTurn(default));
        Assert.InRange(clock.ElapsedMilliseconds, 1150, 4000);
    }

    /// Cloud Usage shows Speechify while it's in use, and always once it has usage for the book.
    [AvaloniaFact]
    public void CloudUsageShowsSpeechifyWithUsage()
    {
        var doc = ProjectDocument.Unsaved(TestApp.Sample());
        var book = doc.Project.UsageKey();
        if (CloudInUse.Speechify(doc.Project)) return;
        var records = new List<CloudUsageRecord>
        {
            new() { Day = "2026-11-21", Book = book, Service = CloudService.Speechify, Model = SpeechifyTTS.Model, Characters = 900 },
        };
        Assert.True(CloudUsageView.Shown(doc.Project, book, records).Speechify);
        Assert.False(CloudUsageView.Shown(doc.Project, "Another|1", records).Speechify);
        Assert.True(CloudUsageView.Shown(doc.Project, null, records).Speechify);
        Assert.False(CloudUsageView.Shown(doc.Project, book, []).Speechify);
    }

    [Fact]
    public void ExportEstimatesSpeechifyCharactersAndCost()
    {
        Assert.Equal(12.0, SpeechifyTTS.EstimatedDollars(600_000), 3);
        Assert.Contains("600,000 Speechify characters", AudioExportWindow.SpeechifyCostText(600_000));
        Assert.Contains("$12.00", AudioExportWindow.SpeechifyCostText(600_000));
        var q = AudioExportWindow.PaidQuestion(72, true, null, 0, GeminiModel.Flash, 0, 600_000)!.Value;
        Assert.Contains("600,000 Speechify characters", q.Message);
        Assert.DoesNotContain("ElevenLabs", q.Message);
    }
}
