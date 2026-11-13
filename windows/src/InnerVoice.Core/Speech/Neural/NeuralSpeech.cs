// The local neural voices as engines (IVoiceEngine), ported from the Mac's ModelSpeech.swift:
// long text goes a few sentences at a time, Pocket TTS speaks every line by cloning a voice
// from a clip (a stock voice's clip ships with the app; a clone uses the narrator's own), the
// pitch of a shade, of casting's age and of the tone shifts that clip first, the tone sets
// the temperature, loudness and pace (PocketDelivery), Orpheus takes a sound tag in front of
// the first piece, and every line is leveled to -20 dB.

using System.Text.Json;
using System.Text.RegularExpressions;

namespace InnerVoice.Core.Speech.Neural;

/// Splits long text the way the Mac does before the models see it.
public static partial class SpeechChunks
{
    // A sentence ends at . ! ? or … (and any closing quote), before a word that doesn't start
    // in lowercase ("“Run!” she said." is one), and not after a title ("Mr. Hale").
    [GeneratedRegex(@"(?<!\b(?:Mr|Mrs|Ms|Dr|St|Mt|Jr|Sr|Prof|Capt|Col|Gen|Lt|Sgt|Rev|vs|etc)\.)(?<=[.!?…][""'”’)\]]*)\s+(?=[^\s\p{Ll}])")]
    private static partial Regex SentenceEnd();

    /// The sentences of a paragraph.
    public static List<string> Sentences(string text) =>
        SentenceEnd().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    /// Runs of whole sentences of at most about `limit` characters (the models stop after a
    /// dozen seconds or so).
    public static List<string> Chunks(string text, int limit = 200)
    {
        if (text.Length <= limit) return [text];
        var outp = new List<string>();
        var current = "";
        foreach (var s in Sentences(text).SelectMany(s => s.Length > limit ? SplitLong(s, limit) : [s]))
        {
            if (current.Length == 0) current = s;
            else if (current.Length + 1 + s.Length <= limit) current += " " + s;
            else { outp.Add(current); current = s; }
        }
        if (current.Length > 0) outp.Add(current);
        return outp.Count == 0 ? [text] : outp;
    }

    /// A sentence too long on its own breaks at commas, semicolons and dashes, or at words.
    static List<string> SplitLong(string s, int limit)
    {
        var outp = new List<string>();
        var current = "";
        foreach (var w in s.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.Length > 0 && (current.Length + 1 + w.Length > limit || (current.Length > limit / 2 && ",;:—".Contains(current[^1]))))
            {
                outp.Add(current);
                current = w;
            }
            else current = current.Length == 0 ? w : current + " " + w;
        }
        if (current.Length > 0) outp.Add(current);
        return outp;
    }
}

/// A few seconds of a voice, and what it says (kept for the narrator; Pocket TTS doesn't need
/// the words).
public sealed record VoiceSample(float[] Audio, string Text);

/// Loads the models when they're first needed. Pocket TTS is small (about 2 GB while it
/// speaks), so PCs with 16 GB or more keep it beside Orpheus; smaller ones keep one model,
/// as on the Mac.
public sealed class NeuralModels
{
    public static NeuralModels Shared { get; set; } = new();

    /// The computer's memory; the app sets it from the hardware check.
    public int MemoryGB { get; set; } = 16;
    bool KeepBoth => MemoryGB >= 16;

    readonly Lock gate = new();
    PocketModel? pocket;
    OrpheusModel? orpheus;
    int loading;
    /// Pocket TTS's conditioning for each clip it has cloned from (voice ID and semitones):
    /// worked out once, the same for every line.
    readonly Dictionary<string, float[]> voices = [];

    /// A model is loading (Settings and the player show "Loading the voice model…").
    public event Action? Changed;
    public bool IsLoading => Volatile.Read(ref loading) > 0;
    /// Which engines have a model in memory now.
    /// Read without the lock: views ask on every redraw, and loading holds the lock for seconds.
    public bool IsLoaded(VoiceEngine e) =>
        e == VoiceEngine.Pocket ? Volatile.Read(ref pocket) is not null
            : e == VoiceEngine.Orpheus && Volatile.Read(ref orpheus) is not null;

    T Load<T>(ref T? slot, Func<T> make, VoiceEngine engine) where T : class
    {
        lock (gate)
        {
            if (slot is not null) return slot;
            if (!KeepBoth) DropLocked(engine == VoiceEngine.Pocket ? VoiceEngine.Orpheus : VoiceEngine.Pocket);
            Interlocked.Increment(ref loading);
            Changed?.Invoke();
            try { return slot = make(); }
            catch (Exception e) when (e is not SpeechException) { throw SpeechException.Failed($"the {engine.Title()} voice model couldn’t load ({e.Message}).", e); }
            finally
            {
                Interlocked.Decrement(ref loading);
                Changed?.Invoke();
            }
        }
    }

    public PocketModel Pocket() => Load(ref pocket, () => new PocketModel(VoicePacks.PocketFolder), VoiceEngine.Pocket);
    public OrpheusModel Orpheus() => Load(ref orpheus, () => new OrpheusModel(VoicePacks.OrpheusFolder), VoiceEngine.Orpheus);

    /// Speaks a line with Pocket TTS, cloning the voice from `clip` (`key` names it for the cache).
    public float[] SpeakPocket(PocketLine line, CancellationToken ct)
    {
        var model = Pocket();
        float[]? voice;
        lock (gate) voices.TryGetValue(line.ClipKey, out voice);
        if (voice is null)
        {
            voice = model.EncodeVoice(line.Clip);
            lock (gate) voices[line.ClipKey] = voice;
        }
        return model.Speak(line.Text, voice, line.Temperature, line.Seed, ct);
    }

    public void Unload(VoiceEngine engine)
    {
        lock (gate) DropLocked(engine);
        GC.Collect();
    }

    void DropLocked(VoiceEngine engine)
    {
        if (engine == VoiceEngine.Pocket)
        {
            pocket?.Dispose();
            pocket = null;
            voices.Clear();
        }
        else if (engine == VoiceEngine.Orpheus)
        {
            orpheus?.Dispose();
            orpheus = null;
        }
    }
}

/// One line for Pocket TTS: the text, the clip it clones the voice from (`ClipKey` names the
/// clip and its shift), the sampling temperature and the seed.
public sealed record PocketLine(string Text, float[] Clip, string ClipKey, double Temperature, ulong Seed);

/// The clips Pocket TTS clones its voices from: a stock voice's clip from the app
/// (windows/Resources/StockVoices, the Mac's nine clips as WAV), a cloned voice's recording,
/// each shifted by a shade's semitones when asked (made once, kept in memory). Every voice has
/// a clip, Alba included (Pocket TTS's own default voice).
public static class PocketClips
{
    /// What the Orpheus stock clips say (the Mac's self-test made them with this passage).
    public const string Passage = "I have lived in this town for a long time, and I know every road that leads out of it. "
        + "In the spring the river runs high, and the whole valley smells of rain.";

    static readonly Dictionary<string, float[]> memory = [];
    static readonly Lock gate = new();

    /// The stock voices that ship with the app ("alba", "tara"…).
    public static IReadOnlyList<string> StockNames { get; } = typeof(PocketClips).Assembly.GetManifestResourceNames()
        .Where(n => n.StartsWith("StockVoices.", StringComparison.Ordinal) && n.EndsWith(".wav", StringComparison.Ordinal))
        .Select(n => n["StockVoices.".Length..^4]).Order(StringComparer.Ordinal).ToList();

    /// A stock voice's clip, 24 kHz mono; null when the app has none by that name.
    public static float[]? StockClip(string name)
    {
        using var s = typeof(PocketClips).Assembly.GetManifestResourceStream("StockVoices." + name + ".wav");
        return s is null ? null : WavFile.Read(s);
    }

    /// The memory key of a voice's clip shifted `semitones`.
    public static string Key(VoiceChoice choice, int semitones) => choice.VoiceID + "\u001F" + semitones;

    /// The clip a voice is cloned from, shifted `semitones`.
    public static float[] Reference(VoiceChoice choice, int semitones)
    {
        var key = Key(choice, semitones);
        lock (gate) if (memory.TryGetValue(key, out var m)) return m;
        float[] clip;
        if (choice.VoiceID.StartsWith(PocketVoice.ClonePrefix, StringComparison.Ordinal))
            clip = LocalClones.Shared.Sample(choice.VoiceID).Audio;
        else
        {
            var name = PocketVoice.StockName(choice.VoiceID) ?? choice.VoiceID;
            clip = StockClip(name) ?? throw SpeechException.VoiceMissing(choice.VoiceID);
        }
        var shifted = ShadeAudio.Apply(clip, semitones, 0);
        lock (gate) memory[key] = shifted;
        return shifted;
    }

    public static void Forget()
    {
        lock (gate) memory.Clear();
    }
}

/// Voices cloned on this PC (Clone a Voice › On This PC): a clip of the narrator's recording
/// and what it says, kept in Cloned Voices as `<id>.wav` + voices.json. voiceID "clone:<id>"
/// on the Pocket TTS engine. As the Mac's LocalClones.swift.
public sealed class LocalClones
{
    public const string Prefix = PocketVoice.ClonePrefix;

    public sealed record Clone(string Id, string Name, Gender Gender, string Text, double Seconds, DateTime Created)
    {
        public string VoiceID => Prefix + Id;
    }

    public static LocalClones Shared { get; set; } = new(Path.Combine(Path.GetTempPath(), "InnerVoice Cloned Voices (test)"));

    public static bool IsClone(VoiceChoice c) => c.Engine == VoiceEngine.Pocket && c.VoiceID.StartsWith(Prefix, StringComparison.Ordinal);

    public string Folder { get; }
    readonly Lock gate = new();
    List<Clone> clones = [];
    readonly Dictionary<string, VoiceSample> samples = [];

    public event Action? Changed;

    public LocalClones(string folder)
    {
        Folder = folder;
        try
        {
            var list = Path.Combine(folder, "voices.json");
            if (File.Exists(list)) clones = JsonSerializer.Deserialize<List<Clone>>(File.ReadAllText(list), Json) ?? [];
        }
        catch (Exception) { clones = []; }
    }

    static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public IReadOnlyList<Clone> All
    {
        get { lock (gate) return clones.ToList(); }
    }

    string AudioFile(string id) => Path.Combine(Folder, id + ".wav");

    public static VoiceInfo Info(Clone c) => new(VoiceEngine.Pocket, c.VoiceID, c.Name, c.Gender, VoiceAge.Adult, "", 3, "Cloned on this PC", Cloned: true);

    public Clone? For(string voiceID)
    {
        if (!voiceID.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var id = voiceID[Prefix.Length..];
        lock (gate) return clones.FirstOrDefault(c => c.Id == id);
    }

    public Clone Add(string name, Gender gender, float[] audio, string text)
    {
        var c = new Clone(Guid.NewGuid().ToString().ToUpperInvariant(), name.Trim(), gender, text.Trim(), audio.Length / (double)SpeechAudio.SampleRate, DateTime.UtcNow);
        Directory.CreateDirectory(Folder);
        WavFile.Write(AudioFile(c.Id), audio, float32: true);
        if (!File.Exists(AudioFile(c.Id))) throw new IOException("The clip couldn’t be saved.");
        lock (gate)
        {
            clones.Add(c);
            samples[c.Id] = new VoiceSample(audio, c.Text);
            Save();
        }
        Changed?.Invoke();
        return c;
    }

    public void Rename(string id, string name)
    {
        var n = name.Trim();
        if (n.Length == 0) return;
        lock (gate)
        {
            int i = clones.FindIndex(c => c.Id == id);
            if (i < 0) return;
            clones[i] = clones[i] with { Name = n };
            Save();
        }
        Changed?.Invoke();
    }

    public void Delete(string id)
    {
        lock (gate)
        {
            clones.RemoveAll(c => c.Id == id);
            samples.Remove(id);
            Save();
        }
        try { File.Delete(AudioFile(id)); } catch (IOException) { }
        PocketClips.Forget();
        Changed?.Invoke();
    }

    /// The clip a clone speaks from.
    public VoiceSample Sample(string voiceID)
    {
        var c = For(voiceID) ?? throw SpeechException.VoiceMissing("a cloned voice deleted from this PC");
        lock (gate) if (samples.TryGetValue(c.Id, out var s)) return s;
        if (!File.Exists(AudioFile(c.Id))) throw SpeechException.VoiceMissing(c.Name);
        var made = new VoiceSample(WavFile.Read(AudioFile(c.Id)), c.Text);
        lock (gate) samples[c.Id] = made;
        return made;
    }

    void Save()
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, "voices.json"), JsonSerializer.Serialize(clones, Json));
    }

    /// Cuts leading and trailing quiet (20 ms windows under -45 dB), leaving 0.1 s of room.
    public static float[] Trim(float[] s)
    {
        int window = SpeechAudio.SampleRate / 50;
        if (s.Length <= window) return [];
        bool Loud(int i)
        {
            float e = 0;
            for (int k = i; k < Math.Min(s.Length, i + window); k++) e += s[k] * s[k];
            return MathF.Sqrt(e / window) > 0.0056f;
        }
        int first = 0;
        while (first < s.Length && !Loud(first)) first += window;
        int last = s.Length - window;
        while (last > first && !Loud(last)) last -= window;
        if (first >= last) return [];
        int pad = SpeechAudio.SampleRate / 10;
        return s[Math.Max(0, first - pad)..Math.Min(s.Length, last + window + pad)];
    }
}

/// Shared by the two local engines: chunks, the gap between them, leveling and the quieter
/// thought voice.
static class LocalRender
{
    public static float[] Finish(List<float[]> pieces, SpeechRequest r, double volume = 1)
    {
        var gap = SpeechAudio.Silence(0.18);
        var joined = new List<float>();
        for (int i = 0; i < pieces.Count; i++)
        {
            if (i > 0) joined.AddRange(gap);
            joined.AddRange(pieces[i]);
        }
        var leveled = SpeechAudio.Leveled(joined.ToArray());
        return SpeechAudio.Scaled(leveled, (float)((r.Kind == LineKind.Thought ? 0.85 : 1) * volume));
    }
}

/// Pocket TTS on this PC: every voice cloned from a clip. It shades by itself (the clip's
/// pitch before cloning; the pace, casting's speed for the character's age and the tone's
/// speed stretch the finished audio), so the renderer leaves it alone.
public sealed class PocketEngine : IVoiceEngine
{
    public VoiceEngine Engine => VoiceEngine.Pocket;
    public bool IsReady => VoicePacks.IsInstalled(VoicePacks.Pocket);
    public string? NotReadyReason => IsReady ? null : "Pocket TTS isn’t downloaded yet. Download it in Settings › Voices.";
    public bool ShadesPitch => true;
    public bool ShadesPace => true;

    /// Speaks one piece; tests put a fake here.
    public Func<PocketLine, CancellationToken, float[]> Speaker { get; set; } = (line, ct) => NeuralModels.Shared.SpeakPocket(line, ct);

    /// Test runs speak without the download.
    public bool AssumeReady { get; set; }
    bool Ready => AssumeReady || IsReady;

    public IReadOnlyList<VoiceInfo> Voices =>
        LocalClones.Shared.All.Select(LocalClones.Info).Concat(VoiceInfo.Pocket).ToList();

    public VoiceInfo? Info(VoiceChoice choice)
    {
        if (LocalClones.IsClone(choice)) return LocalClones.Shared.For(choice.VoiceID) is { } c ? LocalClones.Info(c) : null;
        return VoiceInfo.Pocket.FirstOrDefault(v => v.VoiceID == choice.VoiceID);
    }

    /// The tone's delivery: narration is always even.
    public static PocketDelivery Delivery(SpeechRequest r) => PocketDelivery.For(r.Tone, narration: r.Kind is null);

    /// The semitones the clip shifts by: the shade's, casting's (the voice's pitch) and the tone's.
    public static int Semitones(SpeechRequest r) => (r.Shade?.Pitch ?? 0) + PocketVoice.Semitones(r.Choice.Pitch) + Delivery(r).Semitones;

    /// The speed of the finished audio: the voice's rate, the shade's pace and the tone's.
    public static double Rate(SpeechRequest r) => (r.Choice.Rate ?? 1) * ShadeAudio.PaceRate(r.Shade?.Pace ?? 0) * Delivery(r).Rate;

    public Task<float[]> Render(SpeechRequest r, CancellationToken ct) => Task.Run(() =>
    {
        if (!Ready) throw SpeechException.EngineUnavailable(VoiceEngine.Pocket);
        if (Info(r.Choice) is null) throw SpeechException.VoiceMissing(r.Choice.VoiceID);
        var delivery = Delivery(r);
        int semitones = Semitones(r);
        var clip = PocketClips.Reference(r.Choice, semitones);
        var key = PocketClips.Key(r.Choice, semitones);
        var pieces = new List<float[]>();
        foreach (var piece in SpeechChunks.Chunks(r.Text))
        {
            ct.ThrowIfCancellationRequested();
            // No run-on cut: Pocket TTS stops when the line is said (the Mac's self-test checks
            // short lines at the even and the angry temperature).
            pieces.Add(Speaker(new PocketLine(piece, clip, key, delivery.Temperature, Sampler.Seed(piece + r.Choice.VoiceID)), ct));
        }
        var audio = LocalRender.Finish(pieces, r, delivery.Volume);
        double rate = Rate(r);
        return Math.Abs(rate - 1) > 0.001 ? SpeechAudio.TimeStretched(audio, rate) : audio;
    }, ct);

    public Task Prepare(CancellationToken ct) => Ready && !AssumeReady ? Task.Run(() => NeuralModels.Shared.Pocket(), ct) : Task.CompletedTask;

    public void Unload() => NeuralModels.Shared.Unload(VoiceEngine.Pocket);
}

/// Orpheus on this PC.
public sealed class OrpheusEngine : IVoiceEngine
{
    public VoiceEngine Engine => VoiceEngine.Orpheus;
    public bool IsReady => VoicePacks.IsInstalled(VoicePacks.Orpheus);
    public string? NotReadyReason => IsReady ? null : "Orpheus isn’t downloaded yet. Download it in Settings › Voices.";
    public bool ShadesPitch => false;
    public bool ShadesPace => false;

    public Func<string, string, ulong, CancellationToken, float[]> Speaker { get; set; } =
        (text, voice, seed, ct) => NeuralModels.Shared.Orpheus().Speak(text, voice, seed, ct);

    public bool AssumeReady { get; set; }
    bool Ready => AssumeReady || IsReady;

    public IReadOnlyList<VoiceInfo> Voices => VoiceInfo.Orpheus;
    public VoiceInfo? Info(VoiceChoice choice) => VoiceInfo.Orpheus.FirstOrDefault(v => v.VoiceID == choice.VoiceID);

    /// What Orpheus is given to say: the text with a sound from the speech tag in front
    /// ("she sighed" → <sigh>), which it performs.
    public static string Text(string text, VocalSound? sound) => sound is { } s ? $"<{s.Raw()}> {text}" : text;

    public Task<float[]> Render(SpeechRequest r, CancellationToken ct) => Task.Run(() =>
    {
        if (!Ready) throw SpeechException.EngineUnavailable(VoiceEngine.Orpheus);
        if (Info(r.Choice) is null) throw SpeechException.VoiceMissing(r.Choice.VoiceID);
        var pieces = new List<float[]>();
        var chunks = SpeechChunks.Chunks(r.Text);
        for (int n = 0; n < chunks.Count; n++)
        {
            ct.ThrowIfCancellationRequested();
            pieces.Add(Speaker(Text(chunks[n], n == 0 ? r.Sound : null), r.Choice.VoiceID, Sampler.Seed(chunks[n] + r.Choice.VoiceID), ct));
        }
        return LocalRender.Finish(pieces, r);
    }, ct);

    public Task Prepare(CancellationToken ct) => Ready && !AssumeReady ? Task.Run(() => NeuralModels.Shared.Orpheus(), ct) : Task.CompletedTask;

    public void Unload() => NeuralModels.Shared.Unload(VoiceEngine.Orpheus);
}
