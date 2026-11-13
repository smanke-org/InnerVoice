using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InnerVoice.Core.AI;

namespace InnerVoice.Core.Tests;

/// Claude (the cloud model): nothing is sent without a key, the narrator's yes AND the book's
/// yes, proven against a fake HTTP handler that records every request (the Mac's
/// FakeElevenLabs idea). Plus Review Claude's Changes and the project-file names.
public class ClaudeTests
{
    /// Answers every request from `reply` and keeps what was asked.
    sealed class FakeAnthropic(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            Sent.Add((request, body));
            return reply(request);
        }
    }

    static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    static string MessageReply(string text, string stop = "end_turn") => $$$"""
        {"id":"msg_test","type":"message","role":"assistant","model":"claude-sonnet-5-5",
         "content":[{"type":"text","text":{{{JsonValue.Create(text).ToJsonString()}}}}],
         "stop_reason":"{{{stop}}}","stop_sequence":null,
         "usage":{"input_tokens":10,"output_tokens":5}}
        """;

    const string Passage = "\"Who goes there?\" asked Tom.";

    static readonly ChatMessage[] Prompt =
    [
        new(ChatRole.System, "Tag the speakers."),
        new(ChatRole.User, Passage),
    ];

    [Theory]
    [InlineData(null, true, true, ClaudeException.NotSetUp)]
    [InlineData("", true, true, ClaudeException.NotSetUp)]
    [InlineData("sk-ant-test", false, true, ClaudeException.NotSetUp)]
    [InlineData("sk-ant-test", true, false, ClaudeException.BookNotAllowed)]
    public void NothingIsSentWithoutKeyConsentAndTheBooksYes(string? key, bool consented, bool bookAllowed, string message)
    {
        var fake = new FakeAnthropic(_ => Json(MessageReply("1: Tom")));
        var e = Assert.Throws<ClaudeException>(() =>
            new ClaudeTextModel(key, consented, bookAllowed, Claude.DefaultModelID, "Claude Sonnet 5.5", new HttpClient(fake)));
        Assert.Equal(message, e.Message);
        Assert.Empty(fake.Sent);
    }

    [Fact]
    public async Task SendsThePassageOnlyToAnthropicWithTheKey()
    {
        var fake = new FakeAnthropic(_ => Json(MessageReply("1: Tom")));
        var model = new ClaudeTextModel("sk-ant-test", true, true, "claude-haiku-5-5", "Claude Haiku 5.5", new HttpClient(fake));
        var text = await model.Complete(Prompt, grammar: "root ::= x", maxTokens: 100);
        Assert.Equal("1: Tom", text);
        var (request, body) = Assert.Single(fake.Sent);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
        Assert.Equal("/v1/messages", request.RequestUri.AbsolutePath);
        Assert.Equal("Bearer sk-ant-test", request.Headers.Authorization?.ToString()); // the SDK sends API keys this way
        Assert.Equal("2026-10-21", request.Headers.GetValues("anthropic-version").Single());
        var json = JsonNode.Parse(body)!;
        Assert.Equal("claude-haiku-5-5", (string?)json["model"]);
        Assert.Equal(4096, (int?)json["max_tokens"]);
        Assert.Equal("low", (string?)json["output_config"]?["effort"]);
        Assert.Equal("Tag the speakers.", (string?)json["system"]?[0]?["text"]);
        Assert.Equal("ephemeral", (string?)json["system"]?[0]?["cache_control"]?["type"]);
        var messages = json["messages"]!.AsArray();
        Assert.Single(messages);
        Assert.Equal("user", (string?)messages[0]!["role"]);
        Assert.Contains("Who goes there?", messages[0]!["content"]!.ToJsonString());
        Assert.DoesNotContain("grammar", body);
        Assert.True(model.IsCloud);
        Assert.Equal(2500, model.PassageWords);
    }

    [Fact]
    public async Task ARefusalIsAnErrorNotAnEmptyAnswer()
    {
        var fake = new FakeAnthropic(_ => Json(MessageReply("", "refusal")));
        var model = new ClaudeTextModel("sk-ant-test", true, true, Claude.DefaultModelID, "Claude", new HttpClient(fake));
        var e = await Assert.ThrowsAsync<ClaudeException>(() => model.Complete(Prompt, null, 100));
        Assert.Equal(ClaudeException.Refused, e.Message);
    }

    [Fact]
    public async Task ABadKeyIsSaidPlainly()
    {
        var fake = new FakeAnthropic(_ => Json("""{"type":"error","error":{"type":"authentication_error","message":"invalid x-api-key"}}""",
                                               HttpStatusCode.Unauthorized));
        var model = new ClaudeTextModel("sk-ant-wrong", true, true, Claude.DefaultModelID, "Claude", new HttpClient(fake));
        var e = await Assert.ThrowsAsync<ClaudeException>(() => model.Complete(Prompt, null, 100));
        Assert.Equal(ClaudeException.BadKey, e.Message);
        Assert.Single(fake.Sent); // 401 isn't retried
    }

    [Fact]
    public async Task ListingModelsSendsNoBookTextAndKeepsOnlyClaude()
    {
        var fake = new FakeAnthropic(_ => Json("""
            {"data":[
              {"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5","created_at":"2026-10-01T00:00:00Z"},
              {"type":"model","id":"other-model","display_name":"Other","created_at":"2026-10-01T00:00:00Z"}],
             "has_more":false,"first_id":"claude-opus-5-5","last_id":"other-model"}
            """));
        var models = await Claude.ListModels("sk-ant-test", new HttpClient(fake));
        Assert.Equal([new ClaudeModelInfo("claude-opus-5-5", "Claude Opus 5.5")], models);
        var (request, body) = Assert.Single(fake.Sent);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v1/models", request.RequestUri!.AbsolutePath);
        Assert.Equal("", body);
        await Assert.ThrowsAsync<ClaudeException>(() => Claude.ListModels("", new HttpClient(fake)));
        Assert.Single(fake.Sent);
    }

    [Fact]
    public void BuiltInModelsAndWords()
    {
        Assert.Equal(["claude-fable-5-2", "claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5"], Claude.BuiltIn.Select(m => m.Id));
        Assert.Contains(Claude.DefaultModelID, Claude.BuiltIn.Select(m => m.Id));
        Assert.Equal("Claude runs in the cloud: the book text you send goes to Anthropic. Anthropic doesn't train on API data unless your organization has opted in. Check your organization's privacy settings in the Claude Console.",
                     Claude.Warning);
    }

    // MARK: Review Claude's Changes (port of the Kit's aiReview test, speakers only)

    [Fact]
    public void AIReviewListsAndUndoesOnlyTheAIsOwnChanges()
    {
        var p = new Project { Title = "T" };
        var tom = new BookCharacter("Tom", "#336699");
        var mara = new BookCharacter("Mara", "#996633");
        var added = new BookCharacter("Guard", "#669933");
        p.Characters = [tom, mara, added];
        static Line MakeLine(Guid? speaker, AssignmentSource source) =>
            new(Guid.NewGuid(), new TextSpan(0, 1)) { SpeakerID = speaker, Source = source };
        var ai = MakeLine(tom.Id, AssignmentSource.Ai);
        var mine = MakeLine(mara.Id, AssignmentSource.User);
        var same = MakeLine(tom.Id, AssignmentSource.Ai);
        var guard = MakeLine(added.Id, AssignmentSource.Ai);
        p.Lines = [ai, mine, same, guard];
        var before = new Dictionary<Guid, Line>
        {
            [ai.Id] = new Line { Id = ai.Id },
            [mine.Id] = new Line { Id = mine.Id },
            [same.Id] = same.Copy(),
            [guard.Id] = new Line { Id = guard.Id },
        };
        var items = AIReview.Items(before, p);
        Assert.Equal([ai.Id, guard.Id], items.Select(i => i.LineID));
        AIReview.Undo(new HashSet<Guid> { ai.Id, mine.Id, guard.Id }, before, new HashSet<Guid> { added.Id }, p);
        Assert.Null(p.Lines[0].SpeakerID);
        Assert.Equal(AssignmentSource.None, p.Lines[0].Source);
        Assert.Equal(mara.Id, p.Lines[1].SpeakerID);
        Assert.Equal(tom.Id, p.Lines[2].SpeakerID);
        Assert.DoesNotContain(p.Characters, c => c.Id == added.Id);
        Assert.True(new ProjectSettings().ReviewsCloudAI);
    }

    [Fact]
    public void ClaudeSettingsUseTheMacsJsonNames()
    {
        var p = new Project { Title = "T" };
        p.Settings.CloudTextAllowed = new AppleDate(800_000_000.5);
        p.Settings.ReviewCloudAI = false;
        var json = JsonNode.Parse(ProjectJson.Encode(p))!["settings"]!;
        Assert.Equal(800_000_000.5, (double?)json["cloudTextAllowed"]);
        Assert.False((bool?)json["reviewCloudAI"]);
        var back = ProjectJson.Decode(ProjectJson.Encode(p));
        Assert.Equal(p.Settings.CloudTextAllowed, back.Settings.CloudTextAllowed);
        Assert.False(back.Settings.ReviewsCloudAI);
        Assert.True(back.Settings.Extra is null || !back.Settings.Extra.ContainsKey("reviewCloudAI"));
        var plain = JsonNode.Parse(ProjectJson.Encode(new Project { Title = "T" }))!["settings"]!.AsObject();
        Assert.False(plain.ContainsKey("cloudTextAllowed"));
        Assert.False(plain.ContainsKey("reviewCloudAI"));
    }
}
