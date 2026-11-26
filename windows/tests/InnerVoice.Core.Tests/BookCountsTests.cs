namespace InnerVoice.Core.Tests;

/// The Mac's BookCountsTests (Tests/InnerVoiceKitTests/BookCountsTests.swift).
public class BookCountsTests
{
    [Fact]
    public void CountsLeaveOutSceneBreaksAndCountSpacesAndPunctuation()
    {
        var chapter = new Chapter("One", [
            new Paragraph("Chapter One", ParagraphStyle.Heading, null),
            new Paragraph("  “Hello, Tom,” said Mara.  ", ParagraphStyle.Body, null),
            new Paragraph("* * *", ParagraphStyle.SceneBreak, null),
            new Paragraph("It's 9 o'clock.", ParagraphStyle.Body, null),
        ]);
        var p = ProjectBuilder.Make(new ImportedBook("T", "", [chapter]), "t.txt");
        var index = new BookIndex(p);
        Assert.Equal(2 + 4 + 3, index.BookWords);
        Assert.Equal(11 + 24 + 15, index.BillableCharacters);
        Assert.Equal("12,345 words · 67,890 characters", BookCounts.Label(12345, 67890));
    }

    [Fact]
    public void CostHelpPutsTheBooksEngineFirst()
    {
        var date = new DateTime(2026, 11, 26, 0, 0, 0, DateTimeKind.Utc);
        var help = BookCounts.CostHelp(500_000, VoiceEngine.Speechify, GeminiModel.Flash, date);
        Assert.StartsWith("500,000 billable characters", help);
        var speechify = help.IndexOf("• Speechify: about $10.00 at pay-as-you-go (this book’s engine)", StringComparison.Ordinal);
        var eleven = help.IndexOf("• ElevenLabs: about 500,000 credits", StringComparison.Ordinal);
        Assert.True(speechify >= 0 && eleven > speechify);
        Assert.Contains("Gemini TTS: about $7.50 (Flash, paid tier)", help);
        Assert.DoesNotContain("no charge", help);
        Assert.Contains("runs on this computer: no charge", BookCounts.CostHelp(10, VoiceEngine.Pocket, GeminiModel.FlashLite, date));
    }

    [Fact]
    public void CostHelpUsesTheChosenElevenLabsModel()
    {
        var flash = ElevenLabsModel.Known.First(m => m.Id == "eleven_flash_v2_5");
        Assert.Contains("ElevenLabs: about 500 credits (Eleven Flash v2.5)",
            BookCounts.CostHelp(1000, VoiceEngine.Elevenlabs, GeminiModel.Flash, eleven: flash));
    }
}
