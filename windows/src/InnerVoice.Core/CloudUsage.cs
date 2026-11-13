// Ported from Sources/InnerVoiceKit/CloudUsage.swift.

using System.Globalization;
using System.Text.Json.Serialization;
using InnerVoice.Core.AI;

namespace InnerVoice.Core;

public enum CloudService { Claude, Elevenlabs }

/// What the cloud services used, per day, book and model: numbers only, never book text.
/// JSON names match the Mac's (camelCase; service "claude" / "elevenlabs").
public sealed record CloudUsageRecord
{
    /// "yyyy-MM-dd" in this PC's time zone.
    public string Day { get; set; } = "";
    /// `Project.UsageKey` of the book it was for.
    public string Book { get; set; } = "";
    public CloudService Service { get; set; }
    public string Model { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public long CacheReadTokens { get; set; }
    public long CacheWriteTokens { get; set; }
    /// ElevenLabs bills by characters.
    public long Characters { get; set; }
    public long Requests { get; set; }

    public CloudUsageRecord() { }
    public CloudUsageRecord(string day, string book, CloudService service, string model)
    {
        Day = day;
        Book = book;
        Service = service;
        Model = model;
    }

    [JsonIgnore] public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens;

    /// Dollars at Anthropic's list prices (cache reads 0.1×, cache writes 1.25× the input
    /// price). null for ElevenLabs and for models without a known price.
    [JsonIgnore]
    public double? Cost
    {
        get
        {
            if (Service != CloudService.Claude || ClaudePricing.Price(Model) is not { } p) return null;
            double input = InputTokens + CacheReadTokens * 0.1 + CacheWriteTokens * 1.25;
            return (input * p.Input + OutputTokens * p.Output) / 1_000_000;
        }
    }

    public void Add(CloudUsageRecord other)
    {
        InputTokens += other.InputTokens;
        OutputTokens += other.OutputTokens;
        CacheReadTokens += other.CacheReadTokens;
        CacheWriteTokens += other.CacheWriteTokens;
        Characters += other.Characters;
        Requests += other.Requests;
    }

    public static string DayOf(DateTime? date = null) =>
        (date ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// One ElevenLabs request, for the store: the ElevenLabs engine records each request with
    /// this (characters sent), as the Mac's ElevenLabs.render does.
    public static CloudUsageRecord ElevenLabs(string book, string model, int characters) =>
        new(DayOf(), book, CloudService.Elevenlabs, model) { Characters = characters, Requests = 1 };
}

public static class CloudUsage
{
    /// Adds a request to the list, folding it into the same day, book and model.
    public static void Merge(CloudUsageRecord added, List<CloudUsageRecord> list)
    {
        if (list.Find(r => r.Day == added.Day && r.Book == added.Book && r.Service == added.Service && r.Model == added.Model) is { } same)
            same.Add(added);
        else
            list.Add(added with { });
    }

    /// One total per service and model, for one book (or all books with null).
    public static List<CloudUsageRecord> Totals(IEnumerable<CloudUsageRecord> list, string? book)
    {
        var output = new List<CloudUsageRecord>();
        foreach (var r in list.Where(r => book is null || r.Book == book))
            Merge(r with { Day = "", Book = book ?? "" }, output);
        return output.OrderBy(r => r.Service.ToString().ToLowerInvariant(), StringComparer.Ordinal).ThenBy(r => r.Model, StringComparer.Ordinal).ToList();
    }

    /// Per day and model, for the charts, oldest first.
    public static List<CloudUsageRecord> ByDay(IEnumerable<CloudUsageRecord> list, string? book)
    {
        var output = new List<CloudUsageRecord>();
        foreach (var r in list.Where(r => book is null || r.Book == book))
            Merge(r with { Book = book ?? "" }, output);
        return output.OrderBy(r => r.Day, StringComparer.Ordinal).ThenBy(r => r.Model, StringComparer.Ordinal).ToList();
    }
}

public static class UsageKeys
{
    /// Names this book in the usage records without changing the file: the title and when it
    /// was imported, which don't change. The same key as the Mac's `Project.usageKey`.
    public static string UsageKey(this Project p) =>
        $"{p.Title}|{(long)(p.ImportedAt.Seconds + 978_307_200)}";
}

/// The ElevenLabs plan's credits this period, for Cloud Usage (the Mac's ElevenLabs.account).
public sealed record ElevenLabsCredits(string Tier, long Used, long Limit)
{
    public long Left => Math.Max(0, Limit - Used);

    public const string Host = "https://api.elevenlabs.io";

    /// Asks ElevenLabs for the plan's credits. Sends the key only, never text from a book.
    /// The ElevenLabs engine keeps the key (Windows Credential Manager) and the narrator's
    /// yes; Cloud Usage and the setup check call this with that key.
    public static async Task<ElevenLabsCredits> Read(string apiKey, HttpClient? http = null, CancellationToken ct = default)
    {
        var client = http ?? Shared;
        using var request = new HttpRequestMessage(HttpMethod.Get, Host + "/v1/user/subscription");
        request.Headers.Add("xi-api-key", apiKey.Trim());
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException((int)response.StatusCode is 401 or 403
                ? "ElevenLabs didn't accept the API key."
                : $"ElevenLabs answered {(int)response.StatusCode}.");
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        string tier = root.TryGetProperty("tier", out var t) && t.ValueKind == System.Text.Json.JsonValueKind.String ? t.GetString() ?? "" : "";
        long Number(string name) => root.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : 0;
        return new ElevenLabsCredits(tier, Number("character_count"), Number("character_limit"));
    }

    static readonly HttpClient Shared = new() { Timeout = TimeSpan.FromSeconds(30) };
}
