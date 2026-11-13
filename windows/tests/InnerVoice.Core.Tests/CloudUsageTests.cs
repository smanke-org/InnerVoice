using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InnerVoice.Core.AI;

namespace InnerVoice.Core.Tests;

/// Cloud Usage: the Kit's cloudUsageTotalsAndCost, plus what the Claude model and the
/// ElevenLabs credits reader record or read (fake HTTP, no network).
public class CloudUsageTests
{
    static CloudUsageRecord Claude(string day, string book, string model, long input, long output) =>
        new(day, book, CloudService.Claude, model) { InputTokens = input, OutputTokens = output, Requests = 1 };

    [Fact]
    public void CloudUsageTotalsAndCost()
    {
        var list = new List<CloudUsageRecord>();
        CloudUsage.Merge(Claude("2026-11-12", "A", "claude-sonnet-5-5", 1_000_000, 100_000), list);
        CloudUsage.Merge(Claude("2026-11-12", "A", "claude-sonnet-5-5", 1_000_000, 0), list);
        CloudUsage.Merge(Claude("2026-11-12", "B", "claude-opus-5-5", 500_000, 0), list);
        CloudUsage.Merge(new CloudUsageRecord("2026-11-11", "A", CloudService.Elevenlabs, "eleven_v4") { Characters = 1200 }, list);
        Assert.Equal(3, list.Count);
        var a = CloudUsage.Totals(list, "A");
        Assert.Equal(2, a.Count);
        var sonnet = a.First(r => r.Model == "claude-sonnet-5-5");
        Assert.Equal(2, sonnet.Requests);
        Assert.Equal(5.0, sonnet.Cost!.Value, 6); // 2M in × $2 + 0.1M out × $10
        Assert.Null(a.First(r => r.Service == CloudService.Elevenlabs).Cost);
        Assert.Equal(3, CloudUsage.Totals(list, null).Count);
        var cached = new CloudUsageRecord("d", "A", CloudService.Claude, "claude-haiku-5-5") { CacheReadTokens = 1_000_000, CacheWriteTokens = 1_000_000 };
        Assert.Equal(0.135, cached.Cost!.Value, 6);
        // Totals and days never change the stored records.
        Assert.Equal(2_000_000, list[0].InputTokens);
        var days = CloudUsage.ByDay(list, null);
        Assert.Equal(["2026-11-11", "2026-11-12", "2026-11-12"], days.Select(d => d.Day));
    }

    [Fact]
    public void RecordsUseTheMacsJsonNamesAndTheBookKey()
    {
        var json = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(
            new CloudUsageRecord("2026-11-12", "T|1", CloudService.Elevenlabs, "eleven_v4") { Characters = 5 }, ProjectJson.Options))!;
        Assert.Equal("elevenlabs", (string?)json["service"]);
        Assert.Equal(5, (long?)json["characters"]);
        Assert.Equal("2026-11-12", (string?)json["day"]);
        var p = new Project { Title = "Book", ImportedAt = new AppleDate(800_000_000.7) };
        Assert.Equal("Book|1778307200", p.UsageKey());
    }

    sealed class Fake(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Sent { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Sent.Add(request);
            return Task.FromResult(reply(request));
        }
    }

    [Fact]
    public async Task ClaudeRecordsTokensAndTheRateLimitLeft()
    {
        var fake = new Fake(_ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    {"id":"m","type":"message","role":"assistant","model":"claude-haiku-5-5","content":[{"type":"text","text":"ok"}],
                     "stop_reason":"end_turn","stop_sequence":null,
                     "usage":{"input_tokens":120,"output_tokens":30,"cache_read_input_tokens":1000,"cache_creation_input_tokens":40}}
                    """, Encoding.UTF8, "application/json"),
            };
            r.Headers.Add("anthropic-ratelimit-tokens-remaining", "45000");
            return r;
        });
        var got = new List<(CloudUsageRecord Record, long? Left)>();
        var model = new ClaudeTextModel("sk-ant-test", true, true, "claude-haiku-5-5", "Claude Haiku 5.5", new HttpClient(fake),
                                        book: "Book|1", onUsage: (r, left) => got.Add((r, left)));
        Assert.Equal("ok", await model.Complete([new ChatMessage(ChatRole.User, "Hi")], null, 10));
        var (record, tokensLeft) = Assert.Single(got);
        Assert.Equal(45000, tokensLeft);
        Assert.Equal(("Book|1", CloudService.Claude, "claude-haiku-5-5"), (record.Book, record.Service, record.Model));
        Assert.Equal((120L, 30L, 1000L, 40L, 1L), (record.InputTokens, record.OutputTokens, record.CacheReadTokens, record.CacheWriteTokens, record.Requests));
        Assert.Equal(CloudUsageRecord.DayOf(), record.Day);
    }

    [Fact]
    public async Task ElevenLabsCreditsComeFromTheSubscription()
    {
        var fake = new Fake(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"tier":"creator","character_count":12000,"character_limit":100000,"voice_limit":30}""", Encoding.UTF8, "application/json"),
        });
        var credits = await ElevenLabsCredits.Read(" xi-test ", new HttpClient(fake));
        Assert.Equal(new ElevenLabsCredits("creator", 12000, 100000), credits);
        Assert.Equal(88000, credits.Left);
        var sent = Assert.Single(fake.Sent);
        Assert.Equal("https://api.elevenlabs.io/v1/user/subscription", sent.RequestUri!.ToString());
        Assert.Equal("xi-test", sent.Headers.GetValues("xi-api-key").Single());
        Assert.Null(sent.Content);
        var refused = new Fake(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ElevenLabsCredits.Read("bad", new HttpClient(refused)));
    }
}
