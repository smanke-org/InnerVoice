// Ported from the Mac's Speech/ElevenLabs.swift: ElevenLabs, the one cloud voice engine
// (eleven_v4). Unlike everything else in InnerVoice it sends text off the PC: each line or
// piece of narration it speaks goes to ElevenLabs' servers. So it's off until the narrator
// sets it up (an API key, kept in Windows Credential Manager, and a clear yes), and each book
// must allow it too (`ProjectSettings.CloudVoicesAllowed`). Nothing is sent otherwise.

using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Avalonia.Threading;
using InnerVoice.App.AI;
using InnerVoice.Core;
using InnerVoice.Core.AI;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

/// Why ElevenLabs couldn't do something, in words for the narrator.
public static class ElevenLabsErrors
{
    public const string BookNotAllowed = "This book hasn’t been allowed to use ElevenLabs. Choose ElevenLabs on the Voices page to allow it.";
    public const string BadKey = "ElevenLabs didn’t accept the API key. Check it in Settings › Voices.";
    public const string OutOfCredits = "Your ElevenLabs plan is out of credits for this month. Add credits at elevenlabs.io, or use a voice engine on this PC.";
    public const string Busy = "ElevenLabs is busy. Wait a moment, then play the line again.";
    public const string DescriptionTooShort = "There isn’t enough to design a voice from. Add a few words about how this character sounds to their Voice Notes.";
    public const string CloneNeedsConsent = "Confirm that you have the right and the consent to clone this voice.";
    public const string CloneNeedsName = "Give the voice a name.";
    public const string CloneNeedsSample = "Add a recording of the voice first.";
    public const string CloningNeedsPaidPlan = "Voice cloning needs a paid ElevenLabs plan (Starter or above). Change plans at elevenlabs.io, then try again.";
    public const string VoiceSlotsFull = "All of your ElevenLabs plan’s voice slots are in use. Delete a voice you no longer need in Settings › Voices, or at elevenlabs.io.";
    public const string Offline = "Unable to reach ElevenLabs. Check the internet connection, or use a voice engine on this PC.";
    public static string Server(int code, string message) => $"ElevenLabs couldn’t make the audio ({code}){(message.Length == 0 ? "" : ": " + message)}.";
}

public sealed class ElevenLabsEngine : IVoiceEngine
{
    public static ElevenLabsEngine Shared { get; } = new();

    public static void Register() => VoiceEngineRegistry.Register(Shared);

    public const string Model = "eleven_v4";
    public const string DesignModel = "eleven_ttv_v3";
    /// ElevenLabs takes a description of 20 to 1,000 characters.
    public const int DesignLimit = 1000;
    public const string Host = "https://api.elevenlabs.io";
    public const string KeysPage = "https://elevenlabs.io/app/settings/api-keys";
    public const string SignUpPage = "https://elevenlabs.io/sign-up";
    public const string PrivacyPage = "https://elevenlabs.io/privacy-policy";
    public const string PlansPage = "https://elevenlabs.io/pricing";
    /// ElevenLabs needs at least 10 seconds; 1 to 2 minutes clones best, and past 3 adds little.
    public const double CloneMinimumSeconds = 10, CloneMostSeconds = 180;

    const string CredentialTarget = "InnerVoice/elevenlabs";

    /// Changes (setup, the voice list), on the UI thread.
    public event Action? Changed;

    /// Test runs only: the HTTP client underneath, so tests answer with a fake ElevenLabs.
    /// A test run without one sends nothing at all.
    public static HttpClient? TestHttp { get; set; }
    static readonly HttpClient RealHttp = new() { Timeout = TimeSpan.FromSeconds(300) };

    static HttpClient Http => AppPaths.IsTestRun
        ? TestHttp ?? throw new SpeechException(ElevenLabsErrors.Offline)
        : RealHttp;

    readonly Lock gate = new();
    List<VoiceInfo> voices = [];
    string? key;
    bool keyRead;

    ElevenLabsEngine()
    {
        if (!AppPaths.IsTestRun && AppSettings.Current.ElevenLabsVoices is { } saved) voices = Sort(saved.Select(FromCache));
        CloudInUseHook();
    }

    /// Cloud Usage asks for the key (to read the plan's credits) only while set up.
    static void CloudInUseHook() => InnerVoice.App.Windows.CloudInUse.ElevenLabsKey = () => Shared.IsSetUp ? Shared.ApiKey : null;

    // MARK: IVoiceEngine

    public VoiceEngine Engine => VoiceEngine.Elevenlabs;
    public bool ShadesPitch => false;
    public bool ShadesPace => false;
    public bool IsReady => IsSetUp;
    public string? NotReadyReason => IsSetUp ? null : "Set up ElevenLabs in Settings › Voices: an API key and your yes to sending text to ElevenLabs.";

    /// The account's voices (premade and the narrator's own), from the last fetch. Cloned
    /// voices come first.
    public IReadOnlyList<VoiceInfo> Voices
    {
        get { lock (gate) return voices; }
    }

    /// Voices cloned from recordings (here or on elevenlabs.io). Never given out by casting.
    public IReadOnlyList<VoiceInfo> Clones => Voices.Where(v => v.Cloned).ToList();

    public VoiceInfo? Info(VoiceChoice choice) => Voices.FirstOrDefault(v => v.VoiceID == choice.VoiceID);

    // MARK: Setup

    /// The narrator's yes to sending text to ElevenLabs.
    public DateTime? Consented => AppSettings.Current.ElevenLabsConsent;

    static bool StoresInCredentialManager => OperatingSystem.IsWindows() && !AppPaths.IsTestRun;

    /// Read once and kept in memory: views ask whether ElevenLabs is set up on every redraw
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

    /// Set up: a key, and the narrator's yes to sending text to ElevenLabs.
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
        AppSettings.Update(s => s.ElevenLabsConsent = consent ? DateTime.UtcNow : null);
        Notify();
    }

    /// Forgets the key, the yes and the voice list. Books keep their permission, but nothing
    /// is sent until ElevenLabs is set up again.
    public void Disconnect()
    {
        Save("", consent: false);
        lock (gate) voices = [];
        if (!AppPaths.IsTestRun) AppSettings.Update(s => s.ElevenLabsVoices = null);
        Notify();
    }

    /// Voice cloning is offered only to a book that uses ElevenLabs: set up, allowed for this
    /// book, and the book's engine (or one of Mix Engines') is ElevenLabs.
    public static bool CloningOffered(Project p)
    {
        if (!Shared.IsSetUp || !p.ShowsVoices || p.Settings.CloudVoicesAllowed is null) return false;
        var engine = p.EngineInUse();
        return engine == VoiceEngine.Elevenlabs || (engine == VoiceEngine.Mixed && EngineSwitches.Shared.IsOn(VoiceEngine.Elevenlabs));
    }

    /// Voice Design is offered the same way (it also uses credits and a voice slot).
    public static bool DesignOffered(Project p) => CloningOffered(p);

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    // MARK: Account

    /// Checks a key and returns the plan's credits. Sends no text from the book.
    public static Task<ElevenLabsCredits> Check(string apiKey, CancellationToken ct = default) =>
        Guard(() => ElevenLabsCredits.Read(apiKey, Http, ct));

    /// The plan's credits right now, for Cloud Usage. Sends no text from the book.
    public Task<ElevenLabsCredits> Account(CancellationToken ct = default) =>
        ApiKey is { } k ? Check(k, ct) : throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);

    /// What the plan allows: Instant Voice Cloning needs a paid plan (Starter or above), and
    /// each clone takes one of the plan's custom voice slots.
    public sealed record CloneAllowance(bool CanClone, int SlotsUsed, int SlotLimit, string Tier)
    {
        public bool SlotsFull => SlotLimit > 0 && SlotsUsed >= SlotLimit;
    }

    /// Reads the plan (no book text, no audio).
    public async Task<CloneAllowance> Allowance(CancellationToken ct = default)
    {
        var k = ApiKey ?? throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        var obj = await Send(HttpMethod.Get, "/v1/user/subscription", k, null, ct);
        string tier = Str(obj, "tier") ?? "";
        bool can = obj?["can_use_instant_voice_cloning"] is JsonValue v && v.TryGetValue<bool>(out var b) ? b : !tier.Equals("free", StringComparison.OrdinalIgnoreCase);
        return new CloneAllowance(can, Int(obj, "voice_slots_used"), Int(obj, "voice_limit"), tier);
    }

    /// Fetches the account's voices (names and labels only; no book text is sent).
    public async Task<IReadOnlyList<VoiceInfo>> RefreshVoices(CancellationToken ct = default)
    {
        var k = ApiKey ?? throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        var found = new List<VoiceInfo>();
        string? token = null;
        do
        {
            var path = "/v2/voices?page_size=100" + (token is null ? "" : "&next_page_token=" + Uri.EscapeDataString(token));
            var obj = await Send(HttpMethod.Get, path, k, null, ct);
            foreach (var v in obj?["voices"] as JsonArray ?? [])
                if (v is JsonObject o && FromJson(o) is { } info) found.Add(info);
            token = obj?["has_more"] is JsonValue hm && hm.TryGetValue<bool>(out var more) && more ? Str(obj, "next_page_token") : null;
        } while (token is not null && found.Count < 1000);
        SetVoices(found);
        return Voices;
    }

    // MARK: Voices

    static List<VoiceInfo> Sort(IEnumerable<VoiceInfo> list) =>
        list.OrderBy(v => v.Cloned ? 0 : 1).ThenBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToList();

    void SetVoices(IEnumerable<VoiceInfo> list)
    {
        var sorted = Sort(list);
        lock (gate) voices = sorted;
        if (!AppPaths.IsTestRun)
        {
            var cache = sorted.Select(ToCache).ToList();
            Dispatcher.UIThread.Post(() => AppSettings.Update(s => s.ElevenLabsVoices = cache));
        }
        Notify();
    }

    static CachedVoice ToCache(VoiceInfo v) =>
        new(v.VoiceID, v.Name, v.Gender.ToString().ToLowerInvariant(), v.Age.ToString().ToLowerInvariant(), v.Accent, v.Detail, v.Cloned ? "cloned" : null);

    static VoiceInfo FromCache(CachedVoice c) => new(VoiceEngine.Elevenlabs, c.VoiceID, c.Name,
        Enum.TryParse<Gender>(c.Gender, true, out var g) ? g : Gender.Neutral,
        Enum.TryParse<VoiceAge>(c.Age, true, out var a) ? a : VoiceAge.Adult, c.Accent, 3, c.Detail, Cloned: c.Category == "cloned");

    static readonly TextInfo Title = CultureInfo.GetCultureInfo("en-US").TextInfo;

    /// A voice from ElevenLabs' list: its labels give gender, age and accent.
    public static VoiceInfo? FromJson(JsonObject v)
    {
        if (Str(v, "voice_id") is not { } id || Str(v, "name") is not { } name) return null;
        var labels = v["labels"] as JsonObject;
        string? Label(string k) => labels?[k] is JsonValue lv && lv.TryGetValue<string>(out var s) ? s : null;
        var gender = Label("gender")?.ToLowerInvariant() switch { "female" => Gender.Female, "male" => Gender.Male, _ => Gender.Neutral };
        var ageLabel = Label("age")?.ToLowerInvariant();
        var age = ageLabel switch
        {
            "young" => VoiceAge.Young,
            "old" or "older" => VoiceAge.Older,
            "middle_aged" or "middle aged" => VoiceAge.Adult,
            _ => VoiceAge.Adult,
        };
        var accent = string.Join(" ", (Label("accent") ?? "American").Split('-').Select(p => Title.ToTitleCase(p.ToLowerInvariant())));
        var detail = string.Join(", ", new[] { Label("description"), Label("use_case") ?? Label("use case") }.OfType<string>()
            .Select(d => Title.ToTitleCase(d.Replace('_', ' ').ToLowerInvariant())));
        bool cloned = Str(v, "category")?.ToLowerInvariant() is "cloned" or "professional";
        // Shortened names like "Rachel - calm narrator" keep the first part.
        int dash = name.IndexOf(" - ", StringComparison.Ordinal);
        var shortName = dash > 0 ? name[..dash] : name;
        return new VoiceInfo(VoiceEngine.Elevenlabs, id, shortName, gender, age, accent, 3, cloned ? "Cloned" : detail, Cloned: cloned);
    }

    // MARK: Speech

    /// What ElevenLabs is sent: the text, with v4 audio tags for the tone (with the line's
    /// delivery hint in the same bracket, "[sad, barely audible]") and any sound the speech
    /// tag asks for.
    public static string TextFor(SpeechRequest request)
    {
        var tags = new List<string>();
        if (request.Kind is not null && DeliveryPrompt.AudioTag(request.Tone, request.Delivery) is { } tag) tags.Add(tag);
        if (request.Sound is { } sound) tags.Add(sound.AudioTag());
        tags.Add(request.Text);
        return string.Join(" ", tags);
    }

    /// The same text in the same voice comes out the same. Kept under 2^31: some ElevenLabs
    /// endpoints reject larger seeds.
    public static long Seed(string s)
    {
        var h = SHA256.HashData(Encoding.UTF8.GetBytes(s));
        ulong n = 0;
        for (int i = 0; i < 8; i++) n = n << 8 | h[i];
        return (long)(n % 2_147_483_647);
    }

    static readonly Regex Tags = new(@"\[[^\]]*\]", RegexOptions.CultureInvariant);

    /// Text with nothing to say ("…", a lone dash) is rejected by ElevenLabs: not sent.
    public static bool HasWords(string s) => Tags.Replace(s, "").Any(char.IsLetterOrDigit);

    /// Long text in pieces of at most `limit` characters, at sentences (then words).
    public static List<string> Chunks(string text, int limit = 2000)
    {
        if (text.Length <= limit) return [text];
        var output = new List<string>();
        var current = "";
        foreach (var sentence in SentenceSplitter.Sentences(text).SelectMany(s => s.Length > limit ? SplitLong(s, limit) : [s]))
        {
            if (current.Length == 0) current = sentence;
            else if (current.Length + 1 + sentence.Length <= limit) current += " " + sentence;
            else { output.Add(current); current = sentence; }
        }
        if (current.Length > 0) output.Add(current);
        return output.Count == 0 ? [text] : output;
    }

    static List<string> SplitLong(string s, int limit)
    {
        var output = new List<string>();
        var current = "";
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && (current.Length + 1 + w.Length > limit || (current.Length > limit / 2 && ",;:—".Contains(current[^1]))))
            {
                output.Add(current);
                current = w;
            }
            else current = current.Length == 0 ? w : current + " " + w;
        }
        if (current.Length > 0) output.Add(current);
        return output;
    }

    /// One line (or piece of narration) in a voice. The text leaves the PC here, and only
    /// here, and only when the narrator set ElevenLabs up and the book allows it.
    public async Task<float[]> Render(SpeechRequest request, CancellationToken ct)
    {
        if (!request.CloudAllowed) throw new SpeechException(ElevenLabsErrors.BookNotAllowed);
        if (!IsSetUp || ApiKey is not { } k) throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        var pieces = new List<float[]>();
        foreach (var piece in Chunks(TextFor(request)).Where(HasWords))
        {
            ct.ThrowIfCancellationRequested();
            var body = new JsonObject { ["text"] = piece, ["model_id"] = Model, ["seed"] = Seed(piece + request.Choice.VoiceID) };
            if (request.Choice.Rate is { } rate && rate != 1) body["voice_settings"] = new JsonObject { ["speed"] = Math.Clamp(rate, 0.7, 1.2) };
            var bytes = await SendForBytes($"/v1/text-to-speech/{Uri.EscapeDataString(request.Choice.VoiceID)}?output_format=pcm_24000", k, body, ct);
            int count = piece.Length;
            var book = request.UsageBook ?? "";
            Dispatcher.UIThread.Post(() =>
            {
                Activity.Record(ActivityKey.ElevenLabsCharacters, count);
                CloudUsageStore.Shared.Add(CloudUsageRecord.ElevenLabs(book, Model, count));
            });
            pieces.Add(Pcm16(bytes));
        }
        if (pieces.Count == 0) return SpeechAudio.Silence(0.2);
        var joined = new List<float[]>();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (i > 0) joined.Add(new float[(int)(SpeechAudio.SampleRate * 0.18)]);
            joined.Add(pieces[i]);
        }
        var leveled = await Task.Run(() => SpeechAudio.Leveled(SpeechAudio.Join(joined)), ct);
        return SpeechAudio.Scaled(leveled, request.Kind == LineKind.Thought ? 0.85f : 1);
    }

    /// 16-bit little-endian mono PCM to floats.
    public static float[] Pcm16(byte[] data)
    {
        var output = new float[data.Length / 2];
        for (int i = 0; i < output.Length; i++) output[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        return output;
    }

    // MARK: Voice Design

    /// What ElevenLabs is sent to design a character's voice: who they are, then their Voice Notes.
    public static string DesignDescription(BookCharacter c, Project p, BookIndex? index = null)
    {
        var profile = VoiceCasting.Profile(c, p, index ?? new BookIndex(p));
        return VoiceDescription.Design(VoiceCasting.Describe(profile, c.Name), c, DesignLimit);
    }

    /// Designs a voice for a character from their description and Voice Notes, saved in the
    /// narrator's ElevenLabs voices as "Name (InnerVoice)". Only for a book that allows
    /// ElevenLabs; uses credits and one of the plan's voice slots. The caller gives it to
    /// the character (`document.SetVoice(info.Choice, c.Id)`).
    public Task<VoiceInfo> DesignVoice(BookCharacter c, Project p, CancellationToken ct)
    {
        var index = new BookIndex(p);
        var profile = VoiceCasting.Profile(c, p, index);
        var labels = new Dictionary<string, string>();
        if (profile.Gender is { } g && g != Gender.Neutral) labels["gender"] = g == Gender.Female ? "female" : "male";
        if (profile.Age is { } a) labels["age"] = a == VoiceAge.Older ? "old" : a == VoiceAge.Adult ? "middle_aged" : "young";
        if (profile.Accent is { } accent) labels["accent"] = accent.ToLowerInvariant();
        return DesignVoice($"{c.Name} (InnerVoice)", DesignDescription(c, p, index), labels, p.Settings.CloudVoicesAllowed is not null, ct);
    }

    /// Designs a new voice from a description and saves it in the narrator's ElevenLabs voices.
    /// The description and the name leave the PC here, only when the book allows ElevenLabs.
    public async Task<VoiceInfo> DesignVoice(string name, string description, IReadOnlyDictionary<string, string> labels, bool allowed, CancellationToken ct)
    {
        if (!allowed) throw new SpeechException(ElevenLabsErrors.BookNotAllowed);
        if (!IsSetUp || ApiKey is not { } k) throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        var text = description.Trim();
        if (text.Length > DesignLimit) text = text[..DesignLimit];
        if (text.Length < 20) throw new SpeechException(ElevenLabsErrors.DescriptionTooShort);
        // 1. Previews of voices that fit the description; the first is kept.
        var design = await Send(HttpMethod.Post, "/v1/text-to-voice/design", k, new JsonObject
        {
            ["voice_description"] = text, ["model_id"] = DesignModel, ["auto_generate_text"] = true, ["seed"] = Seed(text),
        }, ct);
        var generated = (design?["previews"] as JsonArray)?.FirstOrDefault() is JsonObject first ? Str(first, "generated_voice_id") : null;
        if (generated is null) throw new SpeechException(ElevenLabsErrors.Server(0, "No voice came back"));
        ct.ThrowIfCancellationRequested();
        // 2. Saved as a voice of the narrator's own.
        var labelJson = new JsonObject();
        foreach (var (lk, lv) in labels) labelJson[lk] = lv;
        var saved = await Send(HttpMethod.Post, "/v1/text-to-voice", k, new JsonObject
        {
            ["voice_name"] = name, ["voice_description"] = text, ["generated_voice_id"] = generated, ["labels"] = labelJson,
        }, ct);
        var id = Str(saved, "voice_id") ?? throw new SpeechException(ElevenLabsErrors.Server(0, "The voice wasn’t saved"));
        var info = (FromJson(new JsonObject { ["voice_id"] = id, ["name"] = name, ["labels"] = labelJson.DeepClone() }) ?? new VoiceInfo(VoiceEngine.Elevenlabs, id, name, Gender.Neutral))
            with { Name = name, Detail = "Designed from Voice Notes" };
        SetVoices(Voices.Where(v => v.VoiceID != id).Append(info));
        int count = text.Length;
        Dispatcher.UIThread.Post(() => Activity.Record(ActivityKey.ElevenLabsCharacters, count));
        return info;
    }

    // MARK: Voice Cloning

    /// Makes a voice from recordings of one person (Instant Voice Cloning) and saves it under
    /// the narrator's name for it in their ElevenLabs voices, where every project (and
    /// elevenlabs.io) can use it. The recordings and the name leave the PC here, and only
    /// after the narrator has confirmed they have the right to clone this voice. No book text
    /// is sent.
    public async Task<VoiceInfo> CloneVoice(string name, IReadOnlyList<string> recordingPaths, bool rightsConfirmed, CancellationToken ct,
                                            bool removeBackgroundNoise = false)
    {
        if (!rightsConfirmed) throw new SpeechException(ElevenLabsErrors.CloneNeedsConsent);
        if (!IsSetUp || ApiKey is not { } k) throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        name = name.Trim();
        if (name.Length == 0) throw new SpeechException(ElevenLabsErrors.CloneNeedsName);
        if (recordingPaths.Count == 0) throw new SpeechException(ElevenLabsErrors.CloneNeedsSample);
        using var form = new MultipartFormDataContent("InnerVoice-" + Guid.NewGuid().ToString("N"));
        form.Add(new StringContent(name), "name");
        form.Add(new StringContent("Cloned in InnerVoice"), "description");
        form.Add(new StringContent(removeBackgroundNoise ? "true" : "false"), "remove_background_noise");
        foreach (var path in recordingPaths)
        {
            var file = new ByteArrayContent(await File.ReadAllBytesAsync(path, ct));
            file.Headers.ContentType = new MediaTypeHeaderValue(AudioType(path));
            form.Add(file, "files", Path.GetFileName(path).Replace("\"", "'"));
        }
        var obj = await SendContent(HttpMethod.Post, "/v1/voices/add", k, form, ct);
        var id = Str(obj, "voice_id") ?? throw new SpeechException(ElevenLabsErrors.Server(0, "The voice wasn’t saved"));
        var info = new VoiceInfo(VoiceEngine.Elevenlabs, id, name, Gender.Neutral, VoiceAge.Adult, "American", 3, "Cloned", Cloned: true);
        SetVoices(Voices.Where(v => v.VoiceID != id).Append(info));
        Dispatcher.UIThread.Post(() => Activity.Record(ActivityKey.VoicesCloned));
        return info;
    }

    /// Renames a voice in the narrator's ElevenLabs account (every project sees the new name).
    public async Task RenameVoice(string id, string name, CancellationToken ct = default)
    {
        var k = ApiKey ?? throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        name = name.Trim();
        if (name.Length == 0) throw new SpeechException(ElevenLabsErrors.CloneNeedsName);
        using var form = new MultipartFormDataContent("InnerVoice-" + Guid.NewGuid().ToString("N")) { { new StringContent(name), "name" } };
        await SendContent(HttpMethod.Post, $"/v1/voices/{Uri.EscapeDataString(id)}/edit", k, form, ct);
        SetVoices(Voices.Select(v => v.VoiceID == id ? v with { Name = name } : v));
    }

    /// Deletes a voice from the narrator's ElevenLabs account, which frees its voice slot.
    /// Characters that used it, in any book, need another voice.
    public async Task DeleteVoice(string id, CancellationToken ct = default)
    {
        var k = ApiKey ?? throw SpeechException.EngineUnavailable(VoiceEngine.Elevenlabs);
        await Send(HttpMethod.Delete, $"/v1/voices/{Uri.EscapeDataString(id)}", k, null, ct);
        SetVoices(Voices.Where(v => v.VoiceID != id));
    }

    public static string AudioType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".wav" or ".wave" => "audio/wav",
        ".m4a" or ".mp4" or ".aac" => "audio/mp4",
        ".flac" => "audio/flac",
        ".ogg" or ".oga" => "audio/ogg",
        ".aif" or ".aiff" => "audio/aiff",
        _ => "application/octet-stream",
    };

    // MARK: HTTP

    static string? Str(JsonNode? o, string k) => o?[k] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static int Int(JsonNode? o, string k) => o?[k] is JsonValue v && v.TryGetValue<int>(out var n) ? n : 0;

    static async Task<T> Guard<T>(Func<Task<T>> work)
    {
        try { return await work(); }
        catch (HttpRequestException) { throw new SpeechException(ElevenLabsErrors.Offline); }
        catch (InvalidOperationException e) when (e.Message.Contains("API key")) { throw new SpeechException(ElevenLabsErrors.BadKey); }
    }

    static Task<JsonNode?> Send(HttpMethod method, string path, string key, JsonObject? body, CancellationToken ct) =>
        SendContent(method, path, key, body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

    static async Task<JsonNode?> SendContent(HttpMethod method, string path, string key, HttpContent? content, CancellationToken ct)
    {
        var bytes = await Raw(method, path, key, content, ct);
        try { return bytes.Length == 0 ? null : JsonNode.Parse(bytes); } catch (JsonException) { return null; }
    }

    static async Task<byte[]> SendForBytes(string path, string key, JsonObject body, CancellationToken ct) =>
        await Raw(HttpMethod.Post, path, key, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct);

    static async Task<byte[]> Raw(HttpMethod method, string path, string key, HttpContent? content, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, Host + path) { Content = content };
        request.Headers.Add("xi-api-key", key.Trim());
        HttpResponseMessage response;
        try { response = await Http.SendAsync(request, ct); }
        catch (HttpRequestException) { throw new SpeechException(ElevenLabsErrors.Offline); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new SpeechException(ElevenLabsErrors.Offline); }
        using (response)
        {
            var data = await response.Content.ReadAsByteArrayAsync(ct);
            if (!response.IsSuccessStatusCode) throw Failure((int)response.StatusCode, path, data);
            return data;
        }
    }

    /// ElevenLabs' error, in words for the narrator.
    public static SpeechException Failure(int code, string path, byte[] data)
    {
        JsonNode? obj = null;
        try { obj = JsonNode.Parse(data); } catch { }
        var detail = obj?["detail"];
        var status = (Str(detail as JsonObject, "status") ?? "").ToLowerInvariant();
        string? list = detail is JsonArray arr
            ? string.Join("; ", arr.OfType<JsonObject>().Select(i => (Str(i, "msg"), (i["loc"] as JsonArray)?.LastOrDefault()?.ToString()))
                .Where(x => x.Item1 is not null).Select(x => x.Item2 is null ? x.Item1! : $"{x.Item2}: {x.Item1}"))
            : null;
        var message = Str(detail as JsonObject, "message") ?? (detail is JsonValue dv && dv.TryGetValue<string>(out var ds) ? ds : null) ?? list ?? "";
        SpeechLog.Write($"ElevenLabs {code} {path.Split('?')[0]}: {status} {(message.Length > 300 ? message[..300] : message)}");
        string text = code switch
        {
            401 when status.Contains("permission") => ElevenLabsErrors.Server(401, message),
            401 => status.Contains("quota") ? ElevenLabsErrors.OutOfCredits : ElevenLabsErrors.BadKey,
            402 => ElevenLabsErrors.OutOfCredits,
            429 => ElevenLabsErrors.Busy,
            _ when status.Contains("voice_limit") || status.Contains("voice_slots") => ElevenLabsErrors.VoiceSlotsFull,
            _ when status.Contains("voice_cloning") || status.Contains("can_not_use_instant") => ElevenLabsErrors.CloningNeedsPaidPlan,
            _ when status.Contains("quota") => ElevenLabsErrors.OutOfCredits,
            _ => ElevenLabsErrors.Server(code, message),
        };
        return new SpeechException(text);
    }
}
