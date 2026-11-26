using System.Text;

namespace InnerVoice.Core.Tests;

/// The Mac's ElevenLabsModelTests (Tests/InnerVoiceKitTests/ElevenLabsModelTests.swift).
public class ElevenLabsModelTests
{
    [Fact]
    public void ModelsComeFromTheAccountNewestFirst()
    {
        const string json = """
        [{"model_id":"eleven_multilingual_v2","name":"Eleven Multilingual v2","can_do_text_to_speech":true,"maximum_text_length_per_request":10000,"model_rates":{"character_cost_multiplier":1.0}},
         {"model_id":"eleven_flash_v2_5","name":"Eleven Flash v2.5","can_do_text_to_speech":true,"model_rates":{"character_cost_multiplier":0.5}},
         {"model_id":"eleven_english_sts_v2","name":"Speech to Speech","can_do_text_to_speech":false},
         {"model_id":"eleven_v4","name":"Eleven v4","can_do_text_to_speech":true,"model_rates":{"character_cost_multiplier":1.0}},
         {"model_id":"eleven_v5","name":"Eleven v5","can_do_text_to_speech":true,"model_rates":{"character_cost_multiplier":1.5}}]
        """;
        var models = ElevenLabsModel.Parse(Encoding.UTF8.GetBytes(json));
        Assert.NotNull(models);
        Assert.Equal(["eleven_v5", "eleven_v4", "eleven_multilingual_v2", "eleven_flash_v2_5"], models.Select(m => m.Id));
        var flash = models.First(m => m.Id == "eleven_flash_v2_5");
        Assert.Equal(501, flash.Credits(1001));
        Assert.Equal("0.5 credits per character", flash.RateText);
        Assert.False(flash.SupportsAudioTags);
        var v5 = models[0];
        Assert.True(v5.SupportsAudioTags);
        Assert.Equal(1500, v5.Credits(1000));   // a newer model needs no update
        Assert.Equal("1 credit per character", models[1].RateText);
        Assert.Equal(10000, models[2].MaxCharacters);
        Assert.Null(ElevenLabsModel.Parse(Encoding.UTF8.GetBytes("{}")));
    }

    [Fact]
    public void AudioTagsNeedV3OrLater()
    {
        Assert.True(ElevenLabsModel.SupportsAudioTagsFor("eleven_v3"));
        Assert.True(ElevenLabsModel.SupportsAudioTagsFor("eleven_v4"));
        Assert.True(ElevenLabsModel.SupportsAudioTagsFor("eleven_v12"));
        Assert.False(ElevenLabsModel.SupportsAudioTagsFor("eleven_multilingual_v2"));
        Assert.False(ElevenLabsModel.SupportsAudioTagsFor("eleven_flash_v2_5"));
        Assert.All(ElevenLabsModel.Known, m => Assert.Equal(m.Id is "eleven_v4" or "eleven_v3", m.SupportsAudioTags));
    }
}
