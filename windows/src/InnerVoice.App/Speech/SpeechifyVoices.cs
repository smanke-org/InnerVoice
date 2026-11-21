// Ported from the Mac's Speech/SpeechifyVoices.swift: Speechify's API, the third cloud voice
// engine. Like ElevenLabs and Gemini TTS it sends text off the PC (to Speechify), so it's off
// until the narrator sets it up (a Speechify API key, kept in Windows Credential Manager, and a
// clear yes) and each book must allow Speechify separately
// (`ProjectSettings.SpeechifyVoicesAllowed`). Nothing is sent otherwise. Request/response
// shapes live in the Core (`SpeechifyTTS`).

using System.Net.Http.Headers;
using System.Text;
using InnerVoice.App.AI;
using Avalonia.Threading;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

/// Why Speechify couldn't do something, in words for the narrator (the Mac's SpeechifyError).
public static class SpeechifyErrors
{
    public const string BookNotAllowed = "This book hasn’t been allowed to use Speechify. Choose Speechify on the Voices page to allow it.";
    public const string BadKey = "Speechify didn’t accept the API key. Check it in Settings › Voices.";
    public const string OutOfCharacters = "Your Speechify API plan has no characters left. Wait for next month’s allowance, change plans on the Speechify API platform, or use a voice engine on this PC.";
    public const string Busy = "Speechify is busy. Wait a moment, then play the line again.";
    public const string Offline = "Unable to reach Speechify. Check the internet connection, or use a voice engine on this PC.";
    public const string NoAudio = "Speechify answered without audio. Try rewording the line or another voice.";
    public static string Server(int code, string message) => $"Speechify couldn’t make the audio ({code}){(message.Length == 0 ? "" : ": " + message)}.";
}

/// Spaces Speechify requests (the Mac's SpeechifyPace): none at first; after a "busy", one
/// request a second (the free plan's limit), after waiting out Speechify's Retry-After. Kept
/// for the app's run.
public sealed class SpeechifyPace
{
    public static SpeechifyPace Shared { get; } = new();
    readonly Lock gate = new();
    DateTime next = DateTime.MinValue;

    /// Seconds between request starts; 0 until Speechify first says busy.
    public double Interval { get; private set; }

    public async Task WaitTurn(CancellationToken ct)
    {
        TimeSpan delay;
        lock (gate)
        {
            var now = DateTime.UtcNow;
            var slot = next > now ? next : now;
            next = slot + TimeSpan.FromSeconds(Interval);
            delay = slot - now;
        }
        if (delay > TimeSpan.Zero) await Task.Delay(delay, ct);
    }

    public void Limited(double? retryAfter)
    {
        lock (gate)
        {
            Interval = Math.Max(Interval, 1.05);
            var resume = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(30, retryAfter ?? 2));
            if (resume > next) next = resume;
        }
        SpeechLog.Write($"Speechify: busy; one request every {Interval:0.00} s");
    }

    /// Back to no pacing (tests start from it).
    public void Reset()
    {
        lock (gate) { Interval = 0; next = DateTime.MinValue; }
    }
}

public sealed class SpeechifyVoices : IVoiceEngine
{
    public static SpeechifyVoices Shared { get; } = new();

    public static void Register() => VoiceEngineRegistry.Register(Shared);

    public const string KeysPage = "https://platform.speechify.ai/api-keys";
    public const string TermsPage = "https://speechify.ai/terms";

    const string CredentialTarget = "InnerVoice/speechify";

    /// Changes (setup, the voice list), on the UI thread.
    public event Action? Changed;

    /// Test runs only: the HTTP client underneath, so tests answer with a fake Speechify. A
    /// test run without one sends nothing at all.
    public static HttpClient? TestHttp { get; set; }
    static readonly HttpClient RealHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

    static HttpClient Http => AppPaths.IsTestRun
        ? TestHttp ?? throw new SpeechException(SpeechifyErrors.Offline)
        : RealHttp;

    readonly Lock gate = new();
    string? key;
    bool keyRead;
    DateTime? testConsent;
    List<string> testPages = [];
    List<VoiceInfo>? cachedVoices;
    bool refreshing;

    SpeechifyVoices() => CloudInUseHook();

    /// Cloud Usage asks whether Speechify is set up.
    static void CloudInUseHook() => InnerVoice.App.Windows.CloudInUse.SpeechifySetUp = () => Shared.IsSetUp;

    // MARK: IVoiceEngine

    public VoiceEngine Engine => VoiceEngine.Speechify;
    // Speechify has no speed or pitch setting (the tone's pace and pitch go in the SSML): the
    // renderer shades the finished audio, and Render stretches it to the narrator's speed.
    public bool ShadesPitch => false;
    public bool ShadesPace => false;
    public bool IsReady => IsSetUp;
    public string? NotReadyReason => IsSetUp ? null : "Set up Speechify in Settings › Voices: an API key and your yes to sending text to Speechify.";

    /// Speechify's ready-made English voices, as last fetched (kept between runs). Fetched again
    /// in the background when there are none yet.
    public IReadOnlyList<VoiceInfo> Voices
    {
        get
        {
            List<VoiceInfo> v;
            bool fetch;
            lock (gate)
            {
                if (cachedVoices is null)
                {
                    var pages = AppPaths.IsTestRun ? testPages : AppSettings.Current.SpeechifyVoicePages ?? [];
                    cachedVoices = SpeechifyTTS.VoicesFromPages(pages.Select(p => Encoding.UTF8.GetBytes(p)));
                }
                v = cachedVoices;
                fetch = v.Count == 0 && !refreshing;
            }
            if (fetch && IsSetUp && (!AppPaths.IsTestRun || TestHttp is not null))
                _ = Task.Run(async () =>
                {
                    try { await RefreshVoices(); }
                    catch (Exception e) { SpeechLog.Write($"Speechify: voice list not fetched: {e.Message}"); }
                });
            return v;
        }
    }

    public VoiceInfo? Info(VoiceChoice choice) => Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);

    // MARK: Setup

    /// The narrator's yes to sending text to Speechify. Test runs keep it in memory.
    public DateTime? Consented => AppPaths.IsTestRun ? testConsent : AppSettings.Current.SpeechifyConsent;

    static bool StoresInCredentialManager => OperatingSystem.IsWindows() && !AppPaths.IsTestRun;

    /// Read once and kept in memory: views ask whether Speechify is set up on every redraw
    /// (every engine check does), and Credential Manager is a call into another process.
    public string? ApiKey
    {
        get
        {
            lock (gate)
            {
                if (!keyRead)
                {
                    keyRead = true;
                    if (StoresInCredentialManager) key = CredentialStore.Read(CredentialTarget);
                }
                return key;
            }
        }
    }

    /// Set up: a key, and the narrator's yes to sending text to Speechify.
    public bool IsSetUp => !string.IsNullOrWhiteSpace(ApiKey) && Consented is not null;

    /// Saves the key (empty deletes it) and the narrator's yes.
    public void Save(string newKey, bool consent)
    {
        newKey = newKey.Trim();
        lock (gate)
        {
            key = newKey.Length == 0 ? null : newKey;
            keyRead = true;
        }
        if (StoresInCredentialManager)
        {
            if (newKey.Length == 0) CredentialStore.Delete(CredentialTarget);
            else CredentialStore.Write(CredentialTarget, newKey);
        }
        if (AppPaths.IsTestRun) testConsent = consent ? DateTime.UtcNow : null;
        else AppSettings.Update(s => s.SpeechifyConsent = consent ? DateTime.UtcNow : null);
        Notify();
    }

    /// Forgets the key and the yes. Books keep their permission, but nothing is sent until
    /// Speechify is set up again.
    public void Disconnect() => Save("", consent: false);

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    /// Checks a key by listing the voices (sends no text from the book), and keeps the list.
    public static Task Check(string apiKey, CancellationToken ct = default) => Shared.RefreshVoices(apiKey.Trim(), ct);

    /// Fetches the voice list (every page of shared English voices) and keeps it.
    public async Task RefreshVoices(string? apiKey = null, CancellationToken ct = default)
    {
        if ((apiKey ?? ApiKey) is not { } k || k.Length == 0) return;
        lock (gate) refreshing = true;
        try
        {
            var pages = new List<string>();
            string? cursor = null;
            do
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, SpeechifyTTS.VoicesUrl(cursor));
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                var (status, data, retryAfter) = await Send(request, timeout.Token, ct);
                if (status is < 200 or >= 300) throw Failure(status, data, retryAfter);
                pages.Add(Encoding.UTF8.GetString(data));
                try { cursor = SpeechifyTTS.VoicesFromResponse(data).Next; }
                catch (System.Text.Json.JsonException) { throw new SpeechException(SpeechifyErrors.Server(status, "the voice list couldn’t be read")); }
            } while (cursor is not null && pages.Count < 20);
            lock (gate)
            {
                if (AppPaths.IsTestRun) testPages = pages;
                cachedVoices = null;
            }
            if (!AppPaths.IsTestRun) AppSettings.Update(s => s.SpeechifyVoicePages = pages);
            Notify();
        }
        finally
        {
            lock (gate) refreshing = false;
        }
    }

    // MARK: Speech

    /// One line (or piece of narration) in a voice. The text leaves the PC here, and only
    /// here, and only when the narrator set Speechify up and the book allows Speechify.
    public async Task<float[]> Render(SpeechRequest request, CancellationToken ct)
    {
        if (!request.CloudAllowed) throw new SpeechException(SpeechifyErrors.BookNotAllowed);
        if (!IsSetUp || ApiKey is not { } k) throw SpeechException.EngineUnavailable(VoiceEngine.Speechify);
        var pieces = new List<float[]>();
        foreach (var piece in ElevenLabsEngine.Chunks(request.Text, SpeechifyTTS.ChunkLimit).Where(ElevenLabsEngine.HasWords))
        {
            ct.ThrowIfCancellationRequested();
            var body = SpeechifyTTS.RequestBody(piece, request.Choice.VoiceID, request.Tone, request.Kind);
            // The free plan allows about one request a second: on "busy", wait and try again.
            int tries = 0;
            (int Status, byte[] Data, TimeSpan? RetryAfter) reply;
            while (true)
            {
                await SpeechifyPace.Shared.WaitTurn(ct);
                using var http = new HttpRequestMessage(HttpMethod.Post, SpeechifyTTS.Host + "audio/speech") { Content = new ByteArrayContent(body) };
                http.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                http.Headers.Authorization = new AuthenticationHeaderValue("Bearer", k);
                reply = await Send(http, ct, ct);
                if (reply.Status is 429 or 503 && tries < 4)
                {
                    tries++;
                    SpeechifyPace.Shared.Limited(reply.RetryAfter?.TotalSeconds);
                    continue;
                }
                break;
            }
            if (reply.Status is < 200 or >= 300) throw Failure(reply.Status, reply.Data, reply.RetryAfter);
            (byte[] Audio, int? Billable) audio;
            try { audio = SpeechifyTTS.AudioFromResponse(reply.Data); }
            catch (SpeechifyTTS.NoAudioException) { throw new SpeechException(SpeechifyErrors.NoAudio); }
            var pcm = GeminiTTS.Pcm16FromWavOrRaw(audio.Audio);
            int count = piece.Length;
            int billed = audio.Billable ?? count;
            double seconds = pcm.Length / 2 / 24_000.0;
            var book = request.UsageBook ?? "";
            Dispatcher.UIThread.Post(() =>
            {
                Activity.Record(ActivityKey.SpeechifyCharacters, count);
                CloudUsageStore.Shared.Add(CloudUsageRecord.Speechify(book, SpeechifyTTS.Model, billed, seconds));
            });
            pieces.Add(ElevenLabsEngine.Pcm16(pcm));
        }
        if (pieces.Count == 0) return SpeechAudio.Silence(0.2);
        var joined = new List<float[]>();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (i > 0) joined.Add(new float[(int)(SpeechAudio.SampleRate * 0.18)]);
            joined.Add(pieces[i]);
        }
        double rate = request.Choice.Rate ?? 1;
        var leveled = await Task.Run(() =>
        {
            var samples = SpeechAudio.Leveled(SpeechAudio.Join(joined));
            // The tone's pace and pitch are in the SSML; the narrator's speed for the voice
            // stretches the audio (the renderer adds the shade's pitch and pace).
            return Math.Abs(rate - 1) > 0.001 ? SpeechAudio.TimeStretched(samples, rate) : samples;
        }, ct);
        return SpeechAudio.Scaled(leveled, request.Kind == LineKind.Thought ? 0.85f : 1);
    }

    /// Sends a request and reads the reply, whatever its status. `ct` is the request's own
    /// token (with a timeout); `caller` is the caller's, so a timeout reads as offline.
    static async Task<(int Status, byte[] Data, TimeSpan? RetryAfter)> Send(HttpRequestMessage request, CancellationToken ct, CancellationToken caller)
    {
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new SpeechException(SpeechifyErrors.Offline); }
        catch (TaskCanceledException) when (!caller.IsCancellationRequested) { throw new SpeechException(SpeechifyErrors.Offline); }
        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(caller);
            return ((int)response.StatusCode, data, response.Headers.RetryAfter?.Delta);
        }
    }

    /// Speechify's error, in words for the narrator (the Mac's throwIfFailed).
    public static SpeechException Failure(int code, byte[] data, TimeSpan? retryAfter = null)
    {
        var e = SpeechifyTTS.ErrorMessage(data);
        var message = e?.Message ?? Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(300, data.Length)));
        var kind = SpeechifyTTS.Classify(code, e?.Code ?? "");
        SpeechLog.Write($"Speechify {code}: {e?.Code ?? ""} {(message.Length > 300 ? message[..300] : message)}");
        string text = kind switch
        {
            SpeechifyTTS.Failure.BadKey => SpeechifyErrors.BadKey,
            SpeechifyTTS.Failure.OutOfCharacters => SpeechifyErrors.OutOfCharacters,
            SpeechifyTTS.Failure.Busy => SpeechifyErrors.Busy,
            _ => SpeechifyErrors.Server(code, message),
        };
        bool busy = kind == SpeechifyTTS.Failure.Busy;
        return new SpeechException(text) { Busy = busy, RetryAfter = busy ? retryAfter : null };
    }
}
