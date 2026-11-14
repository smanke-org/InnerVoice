// The local neural voices as engines (IVoiceEngine), ported from the Mac's ModelSpeech.swift:
// long text goes a few sentences at a time, Pocket TTS speaks every line by cloning a voice
// from a clip (a stock voice's clip ships with the app; a clone uses the narrator's own), the
// pitch of a shade, of casting's age and of the tone shifts that clip first, the tone sets
// the temperature, loudness and pace (PocketDelivery), Orpheus takes a sound tag in front of
// the first piece, and every line is leveled to -20 dB. Qwen3-TTS speaks every line by cloning
// a voice sample (one per description and tone, made once by the VoiceDesign model and kept in
// Voice Models\Samples, or the narrator's own clip for a clone); a shade shifts that sample's
// pitch.

using System.Security.Cryptography;
using System.Text;
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

/// A few seconds of a voice, and exactly what it says: what Qwen3-TTS clones a line from (Pocket
/// TTS doesn't need the words; they're kept for the narrator).
public sealed record VoiceSample(float[] Audio, string Text);

/// Loads the models when they're first needed. Pocket TTS is small (about 2 GB while it
/// speaks), so PCs with 16 GB or more keep it beside Qwen3-TTS or Orpheus; Qwen3-TTS and
/// Orpheus stay loaded together only from 32 GB; below that, loading one drops the other, as
/// on the Mac.
public sealed class NeuralModels
{
    public static NeuralModels Shared { get; set; } = new();

    /// The computer's memory; the app sets it from the hardware check.
    public int MemoryGB { get; set; } = 16;
    /// Whether two engines' models may stay loaded together.
    bool KeepTogether(VoiceEngine a, VoiceEngine b) =>
        a == VoiceEngine.Pocket || b == VoiceEngine.Pocket ? MemoryGB >= 16 : MemoryGB >= 32;

    readonly Lock gate = new();
    PocketModel? pocket;
    QwenShared? qwenShared;
    QwenModel? design, clone;
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
        e switch
        {
            VoiceEngine.Pocket => Volatile.Read(ref pocket) is not null,
            VoiceEngine.Qwen => Volatile.Read(ref design) is not null || Volatile.Read(ref clone) is not null,
            VoiceEngine.Orpheus => Volatile.Read(ref orpheus) is not null,
            _ => false,
        };

    static readonly VoiceEngine[] Local = [VoiceEngine.Pocket, VoiceEngine.Qwen, VoiceEngine.Orpheus];

    T Load<T>(ref T? slot, Func<T> make, VoiceEngine engine) where T : class
    {
        lock (gate)
        {
            if (slot is not null) return slot;
            foreach (var other in Local)
                if (other != engine && !KeepTogether(engine, other)) DropLocked(other);
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
    public QwenModel Design() => Load(ref design, () => new QwenModel(VoicePacks.QwenDesignFolder, SharedQwen()), VoiceEngine.Qwen);
    public QwenModel Clone() => Load(ref clone, () => new QwenModel(VoicePacks.QwenCloneFolder, SharedQwen()), VoiceEngine.Qwen);
    public OrpheusModel Orpheus() => Load(ref orpheus, () => new OrpheusModel(VoicePacks.OrpheusFolder), VoiceEngine.Orpheus);

    QwenShared SharedQwen() => qwenShared ??= new QwenShared(VoicePacks.QwenSharedFolder);

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
        else if (engine == VoiceEngine.Qwen)
        {
            design?.Dispose(); clone?.Dispose(); qwenShared?.Dispose();
            design = clone = null;
            qwenShared = null;
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

/// Qwen3-TTS voice samples: a few seconds in the designed voice, which every line is cloned
/// from so a character sounds the same from line to line (designing each line separately
/// drifts; CLAUDE.md).
public static class QwenSamples
{
    /// What the sample says: plain, unhurried and long enough to carry the voice.
    public const string SampleText = "I have lived in this town for a long time, and I know every road that leads out of it.";

    static readonly Dictionary<string, VoiceSample> memory = [];
    static readonly Lock gate = new();

    /// Makes a sample (the VoiceDesign model); tests put a fake here.
    public static Func<string, string, ulong, CancellationToken, float[]> Designer { get; set; } =
        (text, description, seed, ct) => NeuralModels.Shared.Design().Design(text, description, "english", seed, 1200, ct);

    /// The tone the line's sample is recorded in. Narration is always even.
    public static Tone SampleTone(SpeechRequest r) => r.Kind is null ? Tone.Neutral : r.Tone;

    public static string Key(string design, Tone tone) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("v1\u001F" + design + "\u001F" + tone.Raw())));

    static string FileFor(string key) => Path.Combine(VoicePacks.SamplesFolder, key + ".wav");

    public static VoiceSample Sample(string design, Tone tone, CancellationToken ct)
    {
        var k = Key(design, tone);
        lock (gate) if (memory.TryGetValue(k, out var m)) return m;
        var file = FileFor(k);
        if (File.Exists(file))
        {
            var s = new VoiceSample(WavFile.Read(file), SampleText);
            lock (gate) memory[k] = s;
            return s;
        }
        var prompt = tone == Tone.Neutral ? design : design + " Speaking " + tone.Direction() + ".";
        // The Mac's seed first; a draw that contradicts the description is made again.
        var audio = Designer(SampleText, prompt, Sampler.Seed(design), ct);
        for (int retry = 1; retry < 3 && Pitch.Contradicts(design, audio); retry++)
            audio = Designer(SampleText, prompt, Sampler.Seed(design + retry), ct);
        if (audio.Length == 0) throw SpeechException.Failed("Qwen3-TTS made no sound for this voice. Try a different description.");
        Save(file, audio);
        var made = new VoiceSample(audio, SampleText);
        lock (gate) memory[k] = made;
        return made;
    }

    /// The sample with its pitch shifted `semitones` (a shade), kept beside the sample so it's
    /// made once. 0 is the sample itself.
    public static VoiceSample Shaded(VoiceSample sample, string key, int semitones)
    {
        if (semitones == 0) return sample;
        var k = $"{key}-shade{semitones}";
        lock (gate) if (memory.TryGetValue(k, out var m)) return m;
        var file = FileFor(k);
        var audio = File.Exists(file) ? WavFile.Read(file) : ShadeAudio.Apply(sample.Audio, semitones, 0);
        if (!File.Exists(file)) Save(file, audio);
        var s = new VoiceSample(audio, sample.Text);
        lock (gate) memory[k] = s;
        return s;
    }

    static void Save(string file, float[] audio)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            WavFile.Write(file, audio, float32: true);
        }
        catch (IOException) { /* kept in memory; made again next time */ }
    }

    /// The longest sample the clone model takes: its codec encoder in the ONNX export takes 10
    /// seconds.
    public const double LongestSample = 10;

    /// A sample Qwen3-TTS can clone from: a clip longer than 10 seconds (clones are cut at up to
    /// 15 for Pocket TTS) is cut at the quietest moment in its last few seconds before 10, with
    /// the words cut at the same share of the text (the model needs what the sample says).
    public static VoiceSample Fitted(VoiceSample sample)
    {
        int limit = (int)(LongestSample * SpeechAudio.SampleRate);
        if (sample.Audio.Length <= limit) return sample;
        int window = SpeechAudio.SampleRate / 50, from = (int)(6.0 * SpeechAudio.SampleRate);
        int cut = limit;
        double quietest = double.MaxValue;
        for (int i = from; i + window <= limit; i += window)
        {
            double e = 0;
            for (int k = i; k < i + window; k++) e += sample.Audio[k] * sample.Audio[k];
            if (e < quietest) { quietest = e; cut = i + window / 2; }
        }
        double share = cut / (double)sample.Audio.Length;
        var text = sample.Text.Trim();
        int at = (int)Math.Round(text.Length * share);
        // At a word boundary: back to the space before the word the share falls in.
        while (at > 0 && at < text.Length && !char.IsWhiteSpace(text[at])) at--;
        return new VoiceSample(sample.Audio[..cut], at > 0 ? text[..at].Trim() : text);
    }

    public static void Forget()
    {
        lock (gate) memory.Clear();
    }
}

/// Voices cloned on this PC (Clone a Voice › On This PC): a clip of the narrator's recording
/// and what it says, kept in Cloned Voices as `<id>.wav` + voices.json. voiceID "clone:<id>"
/// on the Pocket TTS or the Qwen3-TTS engine (both speak from the same clip). As the Mac's
/// LocalClones.swift.
public sealed class LocalClones
{
    public const string Prefix = PocketVoice.ClonePrefix;

    public sealed record Clone(string Id, string Name, Gender Gender, string Text, double Seconds, DateTime Created)
    {
        public string VoiceID => Prefix + Id;
    }

    public static LocalClones Shared { get; set; } = new(Path.Combine(Path.GetTempPath(), "InnerVoice Cloned Voices (test)"));

    public static bool IsClone(VoiceChoice c) =>
        c.Engine is VoiceEngine.Pocket or VoiceEngine.Qwen && c.VoiceID.StartsWith(Prefix, StringComparison.Ordinal);

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

    public static VoiceInfo Info(Clone c) => Info(c, VoiceEngine.Pocket);

    /// The clone as a voice of `engine` (Pocket TTS or Qwen3-TTS).
    public static VoiceInfo Info(Clone c, VoiceEngine engine) => new(engine, c.VoiceID, c.Name, c.Gender, VoiceAge.Adult, "", 3, "Cloned on this PC", Cloned: true);

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
        QwenSamples.Forget();
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

/// Qwen3-TTS on this PC.
public sealed class QwenEngine : IVoiceEngine
{
    public VoiceEngine Engine => VoiceEngine.Qwen;
    public bool IsReady => VoicePacks.IsInstalled(VoicePacks.Qwen);
    public string? NotReadyReason => IsReady ? null : "Qwen3-TTS isn’t downloaded yet. Download it in Settings › Voices.";
    public bool ShadesPitch => true;
    public bool ShadesPace => false;

    /// Clones from a sample (the Base model); tests put a fake here.
    public Func<string, VoiceSample, ulong, CancellationToken, float[]> Cloner { get; set; } =
        (text, sample, seed, ct) => NeuralModels.Shared.Clone().Clone(text, sample.Audio, sample.Text, "english", seed, 2400, ct);

    /// Test runs speak without the download.
    public bool AssumeReady { get; set; }
    bool Ready => AssumeReady || IsReady;

    public IReadOnlyList<VoiceInfo> Voices =>
        LocalClones.Shared.All.Select(c => LocalClones.Info(c, VoiceEngine.Qwen)).Concat(VoiceInfo.QwenPresets).ToList();

    public VoiceInfo? Info(VoiceChoice choice)
    {
        if (LocalClones.IsClone(choice)) return LocalClones.Shared.For(choice.VoiceID) is { } c ? LocalClones.Info(c, VoiceEngine.Qwen) : null;
        if (VoiceInfo.QwenPresets.FirstOrDefault(v => v.VoiceID == choice.VoiceID) is { } p) return p;
        return new VoiceInfo(VoiceEngine.Qwen, choice.VoiceID, VoiceNames.Name(choice), VoiceCasting.GenderOfDescription(choice.Design ?? "") ?? Gender.Neutral,
                             VoiceAge.Adult, "", 2, "Designed from a description", choice.Design);
    }

    public Task<float[]> Render(SpeechRequest r, CancellationToken ct) => Task.Run(() => RenderNow(r, ct), ct);

    float[] RenderNow(SpeechRequest r, CancellationToken ct)
    {
        if (!Ready) throw SpeechException.EngineUnavailable(VoiceEngine.Qwen);
        var pieces = new List<float[]>();
        int semitones = r.Shade?.Pitch ?? 0;
        foreach (var piece in SpeechChunks.Chunks(r.Text))
        {
            ct.ThrowIfCancellationRequested();
            if (LocalClones.IsClone(r.Choice))
            {
                // A voice cloned on this PC: every line from the narrator's own clip.
                var sample = QwenSamples.Shaded(QwenSamples.Fitted(LocalClones.Shared.Sample(r.Choice.VoiceID)),
                                                 r.Choice.VoiceID.Replace(':', '-'), semitones); // no ':' in a file name
                pieces.Add(Cloner(piece, sample, Sampler.Seed(piece + r.Choice.VoiceID), ct));
            }
            else
            {
                var design = r.Choice.Design ?? VoiceInfo.QwenPreset(r.Choice.VoiceID)?.Design ?? VoiceInfo.QwenPresets[0].Design!;
                var tone = QwenSamples.SampleTone(r);
                // A shade (Casting Style) shifts the sample's pitch; the clone keeps the voice.
                var sample = QwenSamples.Shaded(QwenSamples.Sample(design, tone, ct), QwenSamples.Key(design, tone), semitones);
                pieces.Add(Cloner(piece, sample, Sampler.Seed(piece + design), ct));
            }
        }
        return LocalRender.Finish(pieces, r);
    }

    public Task Prepare(CancellationToken ct) => Ready && !AssumeReady ? Task.Run(() => NeuralModels.Shared.Clone(), ct) : Task.CompletedTask;

    public void Unload() => NeuralModels.Shared.Unload(VoiceEngine.Qwen);
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
