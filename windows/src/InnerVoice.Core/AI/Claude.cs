// Claude, Anthropic's cloud model, as a choice for the AI features (Tag Speakers, Build
// Summaries). Ported from the Mac's Sources/InnerVoice/AI/Claude.swift, through the official
// Anthropic C# SDK. Book text leaves the PC only with a key, the narrator's yes in Settings,
// AND the book's own `cloudTextAllowed`: `ClaudeTextModel` checks all three before it can be
// made, so no request can be sent without them.

using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;

namespace InnerVoice.Core.AI;

/// Something that answers a prompt: a llama.cpp model or Claude.
public interface ITextModel
{
    string DisplayName { get; }
    /// Words per passage the model takes in comfortably.
    int PassageWords { get; }
    /// True for a model that runs in the cloud (Claude).
    bool IsCloud { get; }
    Task<string> Complete(IEnumerable<ChatMessage> messages, string? grammar, int maxTokens, Action<double>? progress = null,
                          CancellationToken ct = default);
}

public sealed record ClaudeModelInfo(string Id, string Name);

public static class Claude
{
    public const string Host = "https://api.anthropic.com";
    public const string KeysPage = "https://platform.claude.com/settings/keys";
    public const string PrivacyHelp = "https://privacy.anthropic.com/en/articles/7996868-is-my-data-used-for-model-training";
    public const string Warning =
        "Claude runs in the cloud: the book text you send goes to Anthropic. Anthropic doesn't train on API data unless your organization has opted in. Check your organization's privacy settings in the Claude Console.";
    /// The question each book asks once before anything is sent.
    public const string BookQuestion = "Send this book to Claude?";
    /// The question's text: the warning, what this run sends and its estimated cost.
    public static string BookQuestionDetail(string action, string what, string modelName, string estimate) =>
        $"{Warning}\n\n{action} sends {what} to {modelName}. {estimate}\n\n" +
        "InnerVoice sends text only when you run an AI feature. To stop, choose Book › Stop Sending This Book to Claude.";
    /// The model choice is saved as "claude:<model id>".
    public const string SelectionPrefix = "claude:";
    public const string DefaultModelID = "claude-sonnet-5-5";

    public static readonly IReadOnlyList<ClaudeModelInfo> BuiltIn =
    [
        new("claude-fable-5-2", "Claude Fable 5.2"),
        new("claude-opus-5-5", "Claude Opus 5.5"),
        new("claude-sonnet-5-5", "Claude Sonnet 5.5"),
        new("claude-haiku-5-5", "Claude Haiku 5.5"),
    ];

    /// A client that only ever uses the key it is given (an explicit key stops the SDK reading
    /// ANTHROPIC_API_KEY, profiles or any other credential on the PC) and only ever talks to
    /// api.anthropic.com (never ANTHROPIC_BASE_URL). `http` lets tests put a fake handler underneath.
    public static AnthropicClient Client(string apiKey, HttpClient? http = null)
    {
        var timeout = TimeSpan.FromMinutes(10);
        return http is null
            ? new AnthropicClient { ApiKey = apiKey, BaseUrl = Host, MaxRetries = 2, Timeout = timeout }
            : new AnthropicClient { ApiKey = apiKey, BaseUrl = Host, MaxRetries = 2, Timeout = timeout, HttpClient = http };
    }


    /// Asks Anthropic which models the key can use (no book text is sent). Empty: keep the list you have.
    public static async Task<List<ClaudeModelInfo>> ListModels(string apiKey, HttpClient? http = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ClaudeException(ClaudeException.NotSetUp);
        try
        {
            var page = await Client(apiKey, http).Models.List(new ModelListParams { Limit = 100 }, ct);
            return page.Items.Where(m => m.ID.StartsWith("claude-", StringComparison.Ordinal))
                .Select(m => new ClaudeModelInfo(m.ID, string.IsNullOrEmpty(m.DisplayName) ? m.ID : m.DisplayName)).ToList();
        }
        catch (Exception e) when (ClaudeException.From(e) is { } friendly) { throw friendly; }
    }
}

/// What went wrong talking to Anthropic, in words for the narrator.
public sealed class ClaudeException(string message, Exception? inner = null) : Exception(message, inner)
{
    public const string NotSetUp = "Set up Claude in Settings › AI first.";
    public const string BookNotAllowed = "This book hasn't been allowed to use Claude. Nothing was sent.";
    public const string BadKey = "Anthropic didn't accept the API key. Check it in Settings › AI.";
    public const string RateLimited = "Anthropic asked InnerVoice to slow down. Try again in a minute.";
    public const string Overloaded = "Claude is busy right now. Try again in a few minutes.";
    public const string Refused = "Claude declined this passage.";
    public const string BadReply = "Claude's reply couldn't be read.";
    public const string Offline = "Couldn't reach Anthropic. Check the connection, then try again.";

    /// The SDK's errors as the Mac words them; null for cancellation and anything not from the SDK.
    public static ClaudeException? From(Exception e) => e switch
    {
        ClaudeException c => c,
        OperationCanceledException => null,
        AnthropicUnauthorizedException or AnthropicForbiddenException => new(BadKey, e),
        AnthropicRateLimitException => new(RateLimited, e),
        AnthropicApiException api when (int)api.StatusCode == 529 => new(Overloaded, e),
        AnthropicApiException api => new(string.IsNullOrWhiteSpace(api.Message)
            ? $"Anthropic answered {(int)api.StatusCode}." : $"Anthropic answered {(int)api.StatusCode}: {api.Message}", e),
        AnthropicIOException => new(Offline, e),
        AnthropicInvalidDataException => new(BadReply, e),
        _ => null,
    };
}

/// Claude answering prompts. Can only be made with a key, the narrator's yes, and the book's yes.
public sealed class ClaudeTextModel : ITextModel
{
    readonly AnthropicClient client;
    readonly string modelID;
    readonly string book;
    readonly Action<CloudUsageRecord, long?>? onUsage;

    /// `book`: the book's usage key; `onUsage` gets what each request used (numbers only) and
    /// the per-minute tokens left from Anthropic's rate-limit header, for Cloud Usage.
    public ClaudeTextModel(string? apiKey, bool consented, bool bookAllowed, string modelID, string displayName, HttpClient? http = null,
                           string book = "", Action<CloudUsageRecord, long?>? onUsage = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || !consented) throw new ClaudeException(ClaudeException.NotSetUp);
        if (!bookAllowed) throw new ClaudeException(ClaudeException.BookNotAllowed);
        this.modelID = modelID;
        this.book = book;
        this.onUsage = onUsage;
        DisplayName = displayName;
        client = Claude.Client(apiKey, http);
    }

    public string DisplayName { get; }
    /// Claude reads long passages well, so fewer, bigger requests.
    public int PassageWords => 2500;
    public bool IsCloud => true;

    /// Grammars are for local models; Claude follows the prompt's format without one.
    public async Task<string> Complete(IEnumerable<ChatMessage> messages, string? grammar, int maxTokens, Action<double>? progress = null,
                                       CancellationToken ct = default)
    {
        var list = messages.ToList();
        var system = string.Join("\n\n", list.Where(m => m.Role == ChatRole.System).Select(m => m.Content));
        var turns = list.Where(m => m.Role != ChatRole.System)
            .Select(m => m.Role == ChatRole.User
                ? (MessageParam)new UserMessageParam { Content = m.Content }
                : new AssistantMessageParam { Content = m.Content })
            .ToList();
        var request = new MessageCreateParams
        {
            Model = modelID,
            MaxTokens = Math.Max(4096, maxTokens * 4),
            Messages = turns,
            OutputConfig = new OutputConfig { Effort = Effort.Low },
        };
        if (system.Length > 0)
            request = request with { System = new List<TextBlockParam> { new() { Text = system, CacheControl = new CacheControlEphemeral() } } };
        try
        {
            var raw = await client.WithRawResponse.Messages.Create(request, ct);
            var reply = await raw.Deserialize(ct);
            RecordUsage(reply.Usage, raw.Headers);
            ct.ThrowIfCancellationRequested();
            if (reply.StopReason == "refusal") throw new ClaudeException(ClaudeException.Refused);
            progress?.Invoke(1);
            return string.Concat(reply.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));
        }
        catch (Exception e) when (ClaudeException.From(e) is { } friendly && !ReferenceEquals(friendly, e)) { throw friendly; }
    }

    /// Counts what the request used for Cloud Usage (numbers only).
    void RecordUsage(Usage? usage, IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers)
    {
        if (onUsage is null || usage is null) return;
        var r = new CloudUsageRecord(CloudUsageRecord.DayOf(), book, CloudService.Claude, modelID)
        {
            InputTokens = usage.InputTokens,
            OutputTokens = usage.OutputTokens,
            CacheReadTokens = usage.CacheReadInputTokens ?? 0,
            CacheWriteTokens = usage.CacheCreationInputTokens ?? 0,
            Requests = 1,
        };
        var value = headers.FirstOrDefault(h => string.Equals(h.Key, "anthropic-ratelimit-tokens-remaining", StringComparison.OrdinalIgnoreCase)).Value?.FirstOrDefault();
        long? left = long.TryParse(value, out var n) ? n : null;
        onUsage(r, left);
    }
}
