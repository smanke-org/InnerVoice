using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Xunit;
using InnerVoice.App.Document;
using InnerVoice.App.Speech;
using InnerVoice.App.Windows;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Tests;

/// ElevenLabs against a fake server (the Mac's FakeElevenLabs): nothing is sent without a key,
/// the narrator's yes and the book's own allowance; lines carry eleven_v4 and the audio tags;
/// cloning sends only the recordings and the name; usage is counted.
[Collection("Model choice")]
public class ElevenLabsAppTests
{
    sealed class FakeElevenLabs : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Path, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            var path = request.RequestUri!.PathAndQuery;
            Assert.Equal("api.elevenlabs.io", request.RequestUri.Host);
            Assert.True(request.Headers.Contains("xi-api-key"));
            lock (Sent) Sent.Add((request.Method, path, body));
            string json;
            if (path.StartsWith("/v1/text-to-speech/"))
            {
                // Half a second of a 220 Hz tone, 16-bit PCM at 24 kHz.
                var pcm = new byte[24000];
                for (int i = 0; i < 12000; i++)
                {
                    short s = (short)(8000 * Math.Sin(2 * Math.PI * 220 * i / 24000.0));
                    pcm[2 * i] = (byte)s;
                    pcm[2 * i + 1] = (byte)(s >> 8);
                }
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(pcm) };
            }
            if (path.StartsWith("/v1/user/subscription"))
                json = """{"tier":"starter","character_count":1200,"character_limit":30000,"can_use_instant_voice_cloning":true,"voice_slots_used":2,"voice_limit":10}""";
            else if (path.StartsWith("/v2/voices"))
                json = """{"voices":[{"voice_id":"prem1","name":"Rachel - calm narrator","category":"premade","labels":{"gender":"female","age":"young","accent":"american","description":"calm"}},{"voice_id":"mine1","name":"Grandad","category":"cloned","labels":{}}],"has_more":false}""";
            else if (path == "/v1/voices/add") json = """{"voice_id":"clone2"}""";
            else if (path == "/v1/text-to-voice/design") json = """{"previews":[{"generated_voice_id":"gen1"}]}""";
            else if (path == "/v1/text-to-voice") json = """{"voice_id":"designed1"}""";
            else json = "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    static void Pump()
    {
        for (int i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    static FakeElevenLabs Start()
    {
        var fake = new FakeElevenLabs();
        ElevenLabsEngine.TestHttp = new HttpClient(fake);
        ElevenLabsEngine.Shared.Disconnect();
        Pump();
        return fake;
    }

    static SpeechRequest Request(bool allowed) => new()
    {
        Text = "Not here.", Choice = new VoiceChoice(VoiceEngine.Elevenlabs, "prem1"), Tone = Tone.Sad, Kind = LineKind.Spoken,
        Delivery = "barely audible", Sound = VocalSound.Sigh, CloudAllowed = allowed, UsageBook = "Placeholder Book|1",
    };

    [AvaloniaFact]
    public async Task NothingIsSentWithoutKeyConsentAndTheBooksYes()
    {
        var fake = Start();
        var eleven = ElevenLabsEngine.Shared;
        try
        {
            Assert.False(eleven.IsSetUp);
            await Assert.ThrowsAsync<SpeechException>(() => eleven.Render(Request(true), default));
            eleven.Save("xi-test-key", consent: false);
            Assert.False(eleven.IsSetUp);
            await Assert.ThrowsAsync<SpeechException>(() => eleven.Render(Request(true), default));
            eleven.Save("xi-test-key", consent: true);
            Assert.True(eleven.IsSetUp);
            // The book hasn't allowed ElevenLabs: still nothing leaves.
            var refused = await Assert.ThrowsAsync<SpeechException>(() => eleven.Render(Request(false), default));
            Assert.Contains("hasn’t been allowed", refused.Message);
            Assert.Empty(fake.Sent);
            // Nor does the resolver choose it for a book that hasn't said yes.
            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Purpose", p => { p.Settings.Purpose = ProjectPurpose.AiVoices; p.Settings.VoiceEngine = VoiceEngine.Elevenlabs; });
            Assert.NotEqual(VoiceEngine.Elevenlabs, doc.Project.EngineInUse());
            Assert.False(CloudInUse.ElevenLabs(doc.Project));
            Assert.False(ElevenLabsEngine.CloningOffered(doc.Project));
            doc.SetCloudVoicesAllowed(true);
            Assert.Equal(VoiceEngine.Elevenlabs, doc.Project.EngineInUse());
            Assert.True(CloudInUse.ElevenLabs(doc.Project));
            Assert.True(ElevenLabsEngine.CloningOffered(doc.Project));
            Assert.DoesNotContain(fake.Sent, s => s.Path.StartsWith("/v1/text-to-speech"));

            // Allowed: one request, eleven_v4, tone and hint in one bracket, then the sound.
            int before = CloudUsageStore.Shared.Records.Where(r => r.Service == CloudService.Elevenlabs).Sum(r => (int)r.Characters);
            var audio = await eleven.Render(Request(true), default);
            Pump();
            Assert.InRange(SpeechAudio.Seconds(audio), 0.45, 0.55);
            var (method, path, body) = Assert.Single(fake.Sent);
            Assert.Equal(HttpMethod.Post, method);
            Assert.Equal("/v1/text-to-speech/prem1?output_format=pcm_24000", path);
            var json = JsonNode.Parse(body)!;
            Assert.Equal("eleven_v4", (string?)json["model_id"]);
            Assert.Equal("[sad, barely audible] [sighs] Not here.", (string?)json["text"]);
            Assert.InRange((long)json["seed"]!, 0, 2_147_483_646);
            int after = CloudUsageStore.Shared.Records.Where(r => r.Service == CloudService.Elevenlabs).Sum(r => (int)r.Characters);
            Assert.Equal("[sad, barely audible] [sighs] Not here.".Length, after - before);
            Assert.Contains(CloudUsageStore.Shared.Records, r => r.Service == CloudService.Elevenlabs && r.Book == "Placeholder Book|1" && r.Model == "eleven_v4");
            // Narration gets no tone tag; a piece with no words isn't sent.
            Assert.Equal("Hello.", ElevenLabsEngine.TextFor(Request(true) with { Kind = null, Sound = null, Text = "Hello." }));
            Assert.False(ElevenLabsEngine.HasWords("[sad] …"));
        }
        finally
        {
            eleven.Disconnect();
            ElevenLabsEngine.TestHttp = null;
            Pump();
        }
    }

    [AvaloniaFact]
    public async Task VoicesClonesAndDesign()
    {
        var fake = Start();
        var eleven = ElevenLabsEngine.Shared;
        var wav = Path.Combine(Path.GetTempPath(), $"iv-clone-{Guid.NewGuid():N}.wav");
        WavFile.Write(wav, SpeechAudio.Silence(12));
        try
        {
            eleven.Save("xi-test-key", consent: true);
            var voices = await eleven.RefreshVoices();
            // Clones first, never castable; names shortened; labels read.
            Assert.Equal(["Grandad", "Rachel"], voices.Select(v => v.Name));
            Assert.True(voices[0].Cloned);
            Assert.Equal(Gender.Female, voices[1].Gender);
            Assert.Equal(["prem1"], VoiceInfo.Castable(voices).Select(v => v.VoiceID));

            var doc = ProjectDocument.Unsaved(TestApp.Sample());
            doc.Change("Purpose", p => { p.Settings.Purpose = ProjectPurpose.AiVoices; p.Settings.VoiceEngine = VoiceEngine.Elevenlabs; });
            var mara = doc.Project.Characters.First(c => c.Name.StartsWith("Mara"));
            doc.Change("Notes", p => p.Character(mara.Id)!.VoiceNotes = "Bright and quick, a little breathless.");
            fake.Sent.Clear();

            // Designing: refused for a book that hasn't allowed ElevenLabs.
            await Assert.ThrowsAsync<SpeechException>(() => eleven.DesignVoice(doc.Project.Character(mara.Id)!, doc.Project, default));
            Assert.Empty(fake.Sent);
            doc.SetCloudVoicesAllowed(true);
            var designed = await eleven.DesignVoice(doc.Project.Character(mara.Id)!, doc.Project, default);
            Assert.Equal("designed1", designed.VoiceID);
            Assert.Equal(["/v1/text-to-voice/design", "/v1/text-to-voice"], fake.Sent.Select(s => s.Path));
            Assert.Contains("Bright and quick", fake.Sent[0].Body);
            Assert.Contains("eleven_ttv_v3", fake.Sent[0].Body);
            fake.Sent.Clear();

            // Cloning: nothing until the rights box; then only the name and the recordings.
            var window = new CloneVoiceWindow(doc);
            window.Show();
            Pump();
            window.AddRecordings([wav]);
            window.Prepare("Placeholder Voice", rights: false, useFor: mara.Id);
            await window.Clone();
            Assert.DoesNotContain(fake.Sent, s => s.Path == "/v1/voices/add");
            await Assert.ThrowsAsync<SpeechException>(() => eleven.CloneVoice("Placeholder Voice", [wav], rightsConfirmed: false, default));
            window.Prepare("Placeholder Voice", rights: true, useFor: mara.Id);
            await window.Clone();
            Pump();
            var add = Assert.Single(fake.Sent, s => s.Path == "/v1/voices/add");
            Assert.Contains("name=name", add.Body.Replace("\"", ""));
            Assert.Contains("Placeholder Voice", add.Body);
            Assert.Contains("filename=", add.Body);
            foreach (var p in doc.Project.Chapters.SelectMany(c => c.Paragraphs).Where(p => p.Text.Length > 20))
                Assert.DoesNotContain(p.Text, add.Body);
            Assert.Equal("clone2", doc.Project.Character(mara.Id)!.Voice(VoiceEngine.Elevenlabs)?.Choice.VoiceID);
            Assert.Contains(eleven.Clones, v => v.VoiceID == "clone2");
            window.Close();

            // Rename and delete reach the account.
            await eleven.RenameVoice("clone2", "Placeholder Two");
            Assert.Contains(eleven.Voices, v => v.Name == "Placeholder Two");
            await eleven.DeleteVoice("clone2");
            Assert.DoesNotContain(eleven.Voices, v => v.VoiceID == "clone2");
            Assert.Contains(fake.Sent, s => s.Method == HttpMethod.Delete && s.Path == "/v1/voices/clone2");

            // Settings › Voices with ElevenLabs connected.
            if (Environment.GetEnvironmentVariable("INNERVOICE_SNAP_DIR") is { } dir)
            {
                var settings = SettingsWindow.Show(null, "Voices");
                settings.Height = 2000;
                Pump();
                Directory.CreateDirectory(dir);
                settings.CaptureRenderedFrame()?.Save(Path.Combine(dir, "Settings-Voices-ElevenLabs.png"), (int?)null);
                settings.Close();
                Snap(SettingsWindow.ElevenLabsSetupDialog(), Path.Combine(dir, "ElevenLabsSetup.png"));
                var clone = new CloneVoiceWindow(doc);
                Snap(clone, Path.Combine(dir, "CloneVoice.png"));
            }
        }
        finally
        {
            File.Delete(wav);
            eleven.Disconnect();
            ElevenLabsEngine.TestHttp = null;
            Pump();
        }
    }

    static void Snap(Avalonia.Controls.Window w, string file)
    {
        w.Show();
        Pump();
        w.CaptureRenderedFrame()?.Save(file, (int?)null);
        w.Close();
    }

    [AvaloniaFact]
    public void ErrorsSayWhatToDo()
    {
        Assert.Equal(ElevenLabsErrors.OutOfCredits, ElevenLabsEngine.Failure(402, "/x", []).Message);
        Assert.Equal(ElevenLabsErrors.BadKey, ElevenLabsEngine.Failure(401, "/x", "{\"detail\":{\"status\":\"invalid_api_key\"}}"u8.ToArray()).Message);
        Assert.Equal(ElevenLabsErrors.VoiceSlotsFull, ElevenLabsEngine.Failure(400, "/x", "{\"detail\":{\"status\":\"voice_limit_reached\",\"message\":\"x\"}}"u8.ToArray()).Message);
        Assert.Contains("text: field required", ElevenLabsEngine.Failure(422, "/x", "{\"detail\":[{\"loc\":[\"body\",\"text\"],\"msg\":\"field required\"}]}"u8.ToArray()).Message);
        Assert.Equal(2, ElevenLabsEngine.Chunks(string.Join(" ", Enumerable.Repeat("A short sentence here.", 120)), 2000).Count);
    }
}
