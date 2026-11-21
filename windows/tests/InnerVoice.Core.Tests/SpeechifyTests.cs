using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace InnerVoice.Core.Tests;

/// The Mac's SpeechifyTests (Tests/InnerVoiceKitTests/SpeechifyTests.swift), plus the file
/// format both computers share.
public class SpeechifyTTSTests
{
    [Fact]
    public void NarrationIsEvenAndEscaped() =>
        Assert.Equal("<speak>Tom &amp; &quot;Jerry&quot; &lt;3</speak>", SpeechifyTTS.Ssml("Tom & \"Jerry\" <3", Tone.Angry, null));

    [Fact]
    public void ToneBecomesProsody()
    {
        Assert.Equal("<speak><prosody rate=\"+8%\" pitch=\"+6%\" volume=\"loud\">Go!</prosody></speak>", SpeechifyTTS.Ssml("Go!", Tone.Excited, LineKind.Spoken));
        Assert.Equal("<speak>Hm.</speak>", SpeechifyTTS.Ssml("Hm.", Tone.Neutral, LineKind.Spoken));
        Assert.Contains("volume=\"soft\"", SpeechifyTTS.Ssml("Hm.", null, LineKind.Thought));
        Assert.Contains("volume=\"soft\"", SpeechifyTTS.Ssml("Hm.", Tone.Neutral, LineKind.Thought));
        Assert.Equal("<speak><prosody rate=\"-10%\" pitch=\"-5%\" volume=\"soft\">Oh.</prosody></speak>", SpeechifyTTS.Ssml("Oh.", Tone.Sad, LineKind.Spoken));
        Assert.Equal("<speak><prosody rate=\"-5%\" pitch=\"+2%\">What?</prosody></speak>", SpeechifyTTS.Ssml("What?", Tone.Confused, LineKind.Spoken));
        // Every tone but neutral changes the reading.
        Assert.All(Enum.GetValues<Tone>().Where(t => t != Tone.Neutral), t => Assert.NotNull(SpeechifyTTS.Prosody(t, LineKind.Spoken)));
    }

    [Fact]
    public void RequestShape()
    {
        var body = (JsonObject)JsonNode.Parse(SpeechifyTTS.RequestBody("Hi.", "anna", null, LineKind.Spoken))!;
        Assert.Equal("anna", (string?)body["voice_id"]);
        Assert.Equal("simba-3.5", (string?)body["model"]);
        Assert.Equal("wav", (string?)body["audio_format"]);
        Assert.Equal("wav_24000", (string?)body["output_format"]);
        Assert.Equal("<speak>Hi.</speak>", (string?)body["input"]);
        // Keys sorted like the Mac's.
        Assert.Equal(["audio_format", "input", "model", "output_format", "voice_id"], body.Select(kv => kv.Key));
    }

    [Fact]
    public void VoicesSkipPersonalAndReadAccentAndAge()
    {
        var data = Encoding.UTF8.GetBytes("""{"voices":[{"id":"a","display_name":"A","gender":"female","locale":"en-AU","type":"shared","tags":["old","raspy"]},{"id":"b","display_name":"B","gender":"male","locale":"en-US","type":"personal"}],"next_cursor":"x","has_more":true}""");
        var page = SpeechifyTTS.VoicesFromResponse(data);
        Assert.Equal("x", page.Next);
        var v = Assert.Single(page.Voices);
        Assert.True(v.Accent == "Australian" && v.Age == VoiceAge.Older && v.Gender == Gender.Female && v.Engine == VoiceEngine.Speechify);
        Assert.Equal("Old, Raspy", v.Detail);
        Assert.Equal(1, v.Quality);
        // The last page has no next cursor, even when Speechify sends one.
        var last = Encoding.UTF8.GetBytes("""{"voices":[{"id":"c","display_name":"C","gender":"not_specified","locale":"en-GB","tags":["timbre:deep","calm"],"staff_pick_rank":3}],"next_cursor":"y","has_more":false}""");
        var lastPage = SpeechifyTTS.VoicesFromResponse(last);
        Assert.Null(lastPage.Next);
        Assert.True(lastPage.Voices[0] is { Gender: Gender.Neutral, Accent: "British", Detail: "Calm", Quality: 2 });
        // Staff picks first, then by name.
        Assert.Equal(["C", "A"], SpeechifyTTS.VoicesFromPages([data, last]).Select(x => x.Name));
        Assert.Equal("English", SpeechifyTTS.Accent(""));
        Assert.Equal("American", SpeechifyTTS.Accent("en-us"));
    }

    [Fact]
    public void AudioAndErrors()
    {
        var reply = Encoding.UTF8.GetBytes("""{"audio_data":"AAEC","audio_format":"wav","billable_characters_count":3,"speech_marks":{}}""");
        var a = SpeechifyTTS.AudioFromResponse(reply);
        Assert.Equal(new byte[] { 0, 1, 2 }, a.Audio);
        Assert.Equal(3, a.Billable);
        Assert.Throws<SpeechifyTTS.NoAudioException>(() => SpeechifyTTS.AudioFromResponse(Encoding.UTF8.GetBytes("{}")));
        Assert.Throws<SpeechifyTTS.NoAudioException>(() => SpeechifyTTS.AudioFromResponse(Encoding.UTF8.GetBytes("not json")));
        Assert.Equal("unauthorized", SpeechifyTTS.ErrorMessage(Encoding.UTF8.GetBytes("""{"error":{"code":"unauthorized","message":"bad"}}"""))?.Code);
        Assert.Null(SpeechifyTTS.ErrorMessage(Encoding.UTF8.GetBytes("<html>")));
        Assert.Equal(SpeechifyTTS.Failure.BadKey, SpeechifyTTS.Classify(401, ""));
        Assert.Equal(SpeechifyTTS.Failure.BadKey, SpeechifyTTS.Classify(403, "unauthorized"));
        Assert.Equal(SpeechifyTTS.Failure.OutOfCharacters, SpeechifyTTS.Classify(402, ""));
        Assert.Equal(SpeechifyTTS.Failure.OutOfCharacters, SpeechifyTTS.Classify(400, "payment_required"));
        Assert.Equal(SpeechifyTTS.Failure.Busy, SpeechifyTTS.Classify(429, ""));
        Assert.Equal(SpeechifyTTS.Failure.Busy, SpeechifyTTS.Classify(503, ""));
        Assert.Equal(SpeechifyTTS.Failure.Other, SpeechifyTTS.Classify(500, ""));
    }

    [Fact]
    public void PermissionIsItsOwn()
    {
        var s = new ProjectSettings { CloudVoicesAllowed = AppleDate.Now, GoogleVoicesAllowed = AppleDate.Now };
        Assert.False(s.AllowsCloud(VoiceEngine.Speechify));
        s.SpeechifyVoicesAllowed = AppleDate.Now;
        Assert.True(s.AllowsCloud(VoiceEngine.Speechify) && VoiceEngine.Speechify.IsCloud() && VoiceEngine.Speechify.CloudCompany() == "Speechify");
        var only = new ProjectSettings { SpeechifyVoicesAllowed = AppleDate.Now };
        Assert.True(only.AllowsAnyCloudVoices && !only.AllowsCloud(VoiceEngine.Elevenlabs) && !only.AllowsCloud(VoiceEngine.Gemini));
        Assert.Equal("Speechify", VoiceEngine.Speechify.Title());
        Assert.Equal(VoiceEngine.Speechify, VoiceEngines.FromFile("speechify"));
    }

    /// Projects move between the Mac and a PC: Speechify's permission is saved as the Mac's
    /// `speechifyVoicesAllowed`, the engine as "speechify", and both read back.
    [Fact]
    public void TheFileFormatMatchesTheMac()
    {
        var p = new Project { Title = "Placeholder" };
        p.Settings.SpeechifyVoicesAllowed = AppleDate.Now;
        p.Settings.VoiceEngine = VoiceEngine.Speechify;
        var json = Encoding.UTF8.GetString(ProjectJson.Encode(p));
        Assert.Contains("\"speechifyVoicesAllowed\":", json);
        Assert.Contains("\"voiceEngine\":\"speechify\"", json);
        var back = ProjectJson.Decode(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(back.Settings.SpeechifyVoicesAllowed);
        Assert.Null(back.Settings.CloudVoicesAllowed);
        Assert.Null(back.Settings.GoogleVoicesAllowed);
        Assert.Equal(VoiceEngine.Speechify, back.Settings.VoiceEngine);
        Assert.NotNull(back.Settings.Copy().SpeechifyVoicesAllowed);
    }

    [Fact]
    public void UsageRecordsMatchTheMac()
    {
        var json = JsonSerializer.Serialize(CloudUsageRecord.Speechify("b", SpeechifyTTS.Model, 42, 0.6), ProjectJson.Options);
        Assert.Contains("\"service\":\"speechify\"", json);
        Assert.Contains("\"characters\":42", json);
        var mac = """{"day":"2026-11-21","book":"b","service":"speechify","model":"simba-3.5","inputTokens":0,"outputTokens":0,"cacheReadTokens":0,"cacheWriteTokens":0,"characters":42,"audioSeconds":0.6,"requests":1}""";
        var fromMac = JsonSerializer.Deserialize<CloudUsageRecord>(mac, ProjectJson.Options)!;
        Assert.Equal(CloudService.Speechify, fromMac.Service);
        Assert.Null(fromMac.Cost);
        Assert.Equal("speechifyCharacters", ActivityKey.SpeechifyCharacters);
    }
}
