using System.Text;

namespace InnerVoice.Core.Tests;

/// The Mac's ServiceCatalogTests (Tests/InnerVoiceKitTests/ServiceCatalogTests.swift).
public class ServiceCatalogTests
{
    static readonly DateTime Day0 = new(2027, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void FirstRefreshMarksNothingNewThenNewOnesShow()
    {
        var st = new CatalogState();
        st.Record(["eleven_v4", "eleven_v3"], ["a", "b"], Day0);
        Assert.Null(st.NewSummary());
        st.Record(["eleven_v5", "eleven_v4", "eleven_v3"], ["a", "b", "c", "d"], Day0.AddHours(25));
        Assert.Equal(["eleven_v5"], st.NewModels);
        Assert.Equal(["c", "d"], st.NewVoices);
        Assert.Equal("1 new model, 2 new voices", st.NewSummary());
        st.Record(null, ["a", "b", "c", "d"], Day0.AddHours(60));
        Assert.Equal(["eleven_v5"], st.NewModels);
        Assert.Equal(["c", "d"], st.NewVoices);
        st.MarkSeen();
        Assert.Null(st.NewSummary());
    }

    [Fact]
    public void RetiredModelAndSuggestion()
    {
        var st = new CatalogState();
        Assert.False(st.IsRetired("eleven_v3"));
        st.Record(["eleven_v5", "eleven_flash_v2_5"], null, Day0);
        Assert.True(st.IsRetired("eleven_v3"));
        Assert.False(st.IsRetired("eleven_v5"));
        Assert.Equal("eleven_v5", ServiceCatalog.Suggestion("eleven_v3", st.Models));
        Assert.Equal("gemini-3.8-flash-lite-tts-2", ServiceCatalog.Suggestion("gemini-3.8-flash-lite-tts", ["gemini-4.0-flash-tts", "gemini-3.8-flash-lite-tts-2"]));
    }

    [Fact]
    public void DueOncePerDay()
    {
        var st = new CatalogState();
        Assert.True(ServiceCatalog.IsDue(null, Day0));
        st.Record(["x"], null, Day0);
        Assert.False(ServiceCatalog.IsDue(st, Day0.AddHours(1)));
        Assert.True(ServiceCatalog.IsDue(st, Day0.AddHours(24)));
    }

    [Fact]
    public void GeminiModelListKeepsOnlyTTS()
    {
        var json = """{"models":[{"name":"models/gemini-3.8-flash"},{"name":"models/gemini-3.8-flash-tts"},{"name":"models/gemini-3.8-flash-lite-tts"},{"name":"models/gemini-4.0-pro-tts-preview"}]}""";
        Assert.Equal(["gemini-3.8-flash-tts", "gemini-3.8-flash-lite-tts", "gemini-4.0-pro-tts-preview"], ServiceCatalog.GeminiTTSModels(Encoding.UTF8.GetBytes(json)));
        Assert.Equal(GeminiModel.FlashLite, GeminiTTS.PricingFor("gemini-4.0-flash-lite-tts"));
        Assert.Equal(GeminiModel.Flash, GeminiTTS.PricingFor("gemini-4.0-pro-tts-preview"));
        var body = Encoding.UTF8.GetString(GeminiTTS.RequestBody("Hi", "Kore", null, "gemini-4.0-pro-tts-preview"));
        Assert.Contains("\"model\":\"gemini-4.0-pro-tts-preview\"", body);
    }
}
