// Ported from the Mac's Speech/GeminiVoices.swift: Google's Gemini TTS, the second cloud voice
// engine. Like ElevenLabs it sends text off the PC (to Google), so it's off until the narrator
// sets it up (a Gemini API key, kept in Windows Credential Manager, and a clear yes) and each
// book must allow Google separately (`ProjectSettings.GoogleVoicesAllowed`). Nothing is sent
// otherwise. Request/response shapes live in the Core (`GeminiTTS`).

using System.Net.Http.Headers;
using InnerVoice.App.AI;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using InnerVoice.Core;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

/// Why Gemini TTS couldn't do something, in words for the narrator (the Mac's GeminiError).
public static class GeminiErrors
{
    public const string BookNotAllowed = "This book hasn’t been allowed to use Gemini TTS. Choose Gemini TTS on the Voices page to allow it.";
    public const string BadKey = "Google didn’t accept the Gemini API key. Check it in Settings › Voices.";
    public const string OutOfQuota = "Your Gemini API quota is used up for now. Wait, raise the limit in Google AI Studio, or use a voice engine on this PC.";
    public const string Busy = "Gemini TTS is busy. Wait a moment, then play the line again.";
    public const string Offline = "Unable to reach Google. Check the internet connection, or use a voice engine on this PC.";
    public static string NoAudio(string? said) => $"Gemini TTS answered without audio{(said is null ? "" : ": " + said)}. Try rewording the line or another voice.";
    public static string Server(int code, string message) => $"Gemini TTS couldn’t make the audio ({code}){(message.Length == 0 ? "" : ": " + message)}.";
}

public sealed class GeminiVoices : IVoiceEngine
{
    public static GeminiVoices Shared { get; } = new();

    public static void Register() => VoiceEngineRegistry.Register(Shared);

    public const string KeysPage = "https://aistudio.google.com/apikey";
    public const string PrivacyPage = "https://ai.google.dev/gemini-api/terms";

    const string CredentialTarget = "InnerVoice/gemini";

    /// Changes (setup, the model), on the UI thread.
    public event Action? Changed;

    /// Test runs only: the HTTP client underneath, so tests answer with a fake Google. A test
    /// run without one sends nothing at all.
    public static HttpClient? TestHttp { get; set; }
    static readonly HttpClient RealHttp = new() { Timeout = TimeSpan.FromSeconds(120) };

    static HttpClient Http => AppPaths.IsTestRun
        ? TestHttp ?? throw new SpeechException(GeminiErrors.Offline)
        : RealHttp;

    readonly Lock gate = new();
    string? key;
    bool keyRead;
    DateTime? testConsent;
    GeminiModel testModel = GeminiModel.Flash;

    GeminiVoices() => CloudInUseHook();

    /// Cloud Usage asks whether Gemini TTS is set up.
    static void CloudInUseHook() => InnerVoice.App.Windows.CloudInUse.GeminiSetUp = () => Shared.IsSetUp;

    // MARK: IVoiceEngine

    public VoiceEngine Engine => VoiceEngine.Gemini;
    // Gemini has no speed or pitch setting: the renderer shades the finished audio, and
    // Render stretches it to the narrator's speed for the voice.
    public bool ShadesPitch => false;
    public bool ShadesPace => false;
    public bool IsReady => IsSetUp;
    public string? NotReadyReason => IsSetUp ? null : "Set up Gemini TTS in Settings › Voices: an API key and your yes to sending text to Google.";

    public IReadOnlyList<VoiceInfo> Voices => GeminiTTS.Voices;

    public VoiceInfo? Info(VoiceChoice choice) => GeminiTTS.Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);

    // MARK: Setup

    /// Flash or Flash-Lite (Settings › Voices); Flash unless the narrator picks otherwise.
    public GeminiModel Model
    {
        get => AppPaths.IsTestRun ? testModel : GeminiTTS.ModelFrom(AppSettings.Current.GeminiModel) ?? GeminiModel.Flash;
        set
        {
            if (AppPaths.IsTestRun) testModel = value;
            else AppSettings.Update(s => s.GeminiModel = value.Raw());
            Notify();
        }
    }

    /// The narrator's yes to sending text to Google. Test runs keep it in memory.
    public DateTime? Consented => AppPaths.IsTestRun ? testConsent : AppSettings.Current.GeminiConsent;

    static bool StoresInCredentialManager => OperatingSystem.IsWindows() && !AppPaths.IsTestRun;

    /// Read once and kept in memory: views ask whether Gemini TTS is set up on every redraw
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

    /// Set up: a key, and the narrator's yes to sending text to Google.
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
        else AppSettings.Update(s => s.GeminiConsent = consent ? DateTime.UtcNow : null);
        Notify();
    }

    /// Forgets the key and the yes. Books keep their permission, but nothing is sent until
    /// Gemini TTS is set up again.
    public void Disconnect() => Save("", consent: false);

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    /// Checks a key by listing the voices (sends no text from the book).
    public static async Task Check(string apiKey, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, GeminiTTS.Host + "voices?type=prebuilt&page_size=1");
        request.Headers.Add("x-goog-api-key", apiKey.Trim());
        await Send(request, ct);
    }

    // MARK: Speech

    static readonly Regex Tags = new("<[^>]*>", RegexOptions.CultureInvariant);

    /// The text sent for a request: the sound's tag ("<sigh>") first, then the words.
    public static string TextFor(SpeechRequest r) => r.Sound is { } s ? GeminiTTS.Tag(s) + " " + r.Text : r.Text;

    /// One line (or piece of narration) in a voice. The text leaves the PC here, and only
    /// here, and only when the narrator set Gemini TTS up and the book allows Google.
    public async Task<float[]> Render(SpeechRequest request, CancellationToken ct)
    {
        if (!request.CloudAllowed) throw new SpeechException(GeminiErrors.BookNotAllowed);
        if (!IsSetUp || ApiKey is not { } k) throw SpeechException.EngineUnavailable(VoiceEngine.Gemini);
        var model = Model;
        var style = request.Kind is null ? null : GeminiTTS.Style(request.Tone, request.Kind, request.Delivery);
        var pieces = new List<float[]>();
        foreach (var piece in ElevenLabsEngine.Chunks(TextFor(request), 1500).Where(p => Tags.Replace(p, "").Any(char.IsLetterOrDigit)))
        {
            ct.ThrowIfCancellationRequested();
            using var http = new HttpRequestMessage(HttpMethod.Post, GeminiTTS.Host + "interactions")
            {
                Content = new ByteArrayContent(GeminiTTS.RequestBody(piece, request.Choice.VoiceID, style, model)),
            };
            http.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            http.Headers.Add("x-goog-api-key", k);
            var data = await Send(http, ct);
            byte[] pcm;
            try { pcm = GeminiTTS.Pcm16FromWavOrRaw(GeminiTTS.AudioFromResponse(data)); }
            catch (GeminiTTS.NoAudioException e) { throw new SpeechException(GeminiErrors.NoAudio(e.Said)); }
            int count = piece.Length;
            double seconds = pcm.Length / 2 / 24_000.0;
            var book = request.UsageBook ?? "";
            var raw = model.Raw();
            Dispatcher.UIThread.Post(() =>
            {
                Activity.Record(ActivityKey.GeminiCharacters, count);
                CloudUsageStore.Shared.Add(CloudUsageRecord.Google(book, raw, count, seconds));
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
            var audio = SpeechAudio.Leveled(SpeechAudio.Join(joined));
            // Gemini has no speed setting: the narrator's speed for the voice stretches the
            // audio (the renderer adds the shade's pitch and pace).
            return Math.Abs(rate - 1) > 0.001 ? SpeechAudio.TimeStretched(audio, rate) : audio;
        }, ct);
        return SpeechAudio.Scaled(leveled, request.Kind == LineKind.Thought ? 0.85f : 1);
    }

    static async Task<byte[]> Send(HttpRequestMessage request, CancellationToken ct)
    {
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new SpeechException(GeminiErrors.Offline); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new SpeechException(GeminiErrors.Offline); }
        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode) throw Failure((int)response.StatusCode, data);
            return data;
        }
    }

    /// Google's error, in words for the narrator.
    public static SpeechException Failure(int code, byte[] data)
    {
        var e = GeminiTTS.ErrorMessage(data);
        var message = e?.Message ?? System.Text.Encoding.UTF8.GetString(data.AsSpan(0, Math.Min(300, data.Length)));
        var status = e?.Status ?? "";
        SpeechLog.Write($"Gemini {code}: {status} {(message.Length > 300 ? message[..300] : message)}");
        string text = code switch
        {
            400 when message.Contains("API key", StringComparison.OrdinalIgnoreCase) => GeminiErrors.BadKey,
            401 => GeminiErrors.BadKey,
            403 when status == "PERMISSION_DENIED" => GeminiErrors.BadKey,
            429 => message.Contains("quota", StringComparison.OrdinalIgnoreCase) ? GeminiErrors.OutOfQuota : GeminiErrors.Busy,
            >= 500 and < 600 => GeminiErrors.Busy,
            _ => GeminiErrors.Server(code, message),
        };
        return new SpeechException(text) { Busy = text == GeminiErrors.Busy };
    }
}
