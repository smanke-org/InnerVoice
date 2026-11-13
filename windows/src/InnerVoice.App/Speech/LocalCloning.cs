// Clone a Voice › On This PC, the Mac's CloneAudio: joins the recordings at 24 kHz, finds the
// words with Windows' own speech recognizer on this PC (SAPI dictation, never a cloud
// recognizer), and cuts a clip of at most 10 seconds at a pause (CloneClip). The narrator can
// fix the words; the clip and its words are what Pocket TTS clones from.

using System.Globalization;
using InnerVoice.Core.Speech;

namespace InnerVoice.App.Speech;

public static class LocalCloning
{
    /// The clip a voice is learned from, and what it says.
    public sealed record Clip(float[] Audio, string Text, bool Recognized)
    {
        public double Seconds => SpeechAudio.Seconds(Audio);
    }

    /// Tests find words without a recognizer: (24 kHz audio) → transcript and word times.
    public static Func<float[], (string Text, List<CloneClip.Word> Words)?>? TestTranscriber { get; set; }

    /// Reads, trims and joins the recordings, finds the words, and cuts the clip.
    public static Clip Prepare(IEnumerable<string> paths, CancellationToken ct = default)
    {
        var audio = Load(paths);
        ct.ThrowIfCancellationRequested();
        if (audio.Length == 0) return new Clip([], "", false);
        if (Transcribe(audio) is { } found && CloneClip.Cut(found.Words) is { } span)
        {
            int start = (int)(span.Start * SpeechAudio.SampleRate), end = Math.Min(audio.Length, (int)(span.End * SpeechAudio.SampleRate));
            var piece = audio[Math.Min(start, end)..end];
            return new Clip(SpeechAudio.Leveled(piece), CloneClip.Text(found.Text, span.TextEnd), true);
        }
        var first = audio[..Math.Min(audio.Length, (int)(CloneClip.Longest * SpeechAudio.SampleRate))];
        return new Clip(SpeechAudio.Leveled(first), "", false);
    }

    /// Each recording at 24 kHz mono with its quiet ends cut, joined with a short pause.
    public static float[] Load(IEnumerable<string> paths)
    {
        var parts = new List<float[]>();
        foreach (var p in paths)
        {
            var trimmed = Core.Speech.Neural.LocalClones.Trim(Read(p));
            if (trimmed.Length > 0) parts.Add(trimmed);
        }
        var joined = new List<float>();
        foreach (var part in parts)
        {
            if (joined.Count > 0) joined.AddRange(SpeechAudio.Silence(0.3));
            joined.AddRange(part);
        }
        return joined.ToArray();
    }

    /// A recording as 24 kHz mono: WAV anywhere; MP3, M4A and the rest through Windows' Media
    /// Foundation.
    public static float[] Read(string path)
    {
        if (path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)) return WavFile.Read(path);
#if WINDOWS
        using var reader = new NAudio.Wave.MediaFoundationReader(path);
        var provider = NAudio.Wave.WaveExtensionMethods.ToSampleProvider(reader);
        int channels = provider.WaveFormat.Channels, rate = provider.WaveFormat.SampleRate;
        var all = new List<float>();
        var buffer = new float[rate * channels];
        int n;
        while ((n = provider.Read(buffer, 0, buffer.Length)) > 0)
            for (int i = 0; i + channels <= n; i += channels)
            {
                float s = 0;
                for (int c = 0; c < channels; c++) s += buffer[i + c];
                all.Add(s / channels);
            }
        return SpeechAudio.Resampled(all.ToArray(), rate);
#else
        throw new IOException($"{Path.GetFileName(path)}: only WAV recordings can be read here.");
#endif
    }

    /// What's said in the first half minute, with each word's time, recognized on this PC
    /// only. null when Windows has no English speech recognizer installed; then the narrator
    /// types it.
    public static (string Text, List<CloneClip.Word> Words)? Transcribe(float[] audio)
    {
        var first = audio[..Math.Min(audio.Length, 30 * SpeechAudio.SampleRate)];
        if (TestTranscriber is { } fake) return fake(first);
        if (!OperatingSystem.IsWindows()) return null;
        try { return Sapi(first); }
        catch (Exception) { return null; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    static (string Text, List<CloneClip.Word> Words)? Sapi(float[] audio)
    {
        // The desktop recognizer (SAPI) runs entirely on this PC. Dictation through WinRT's
        // SpeechRecognizer would use Microsoft's online service, so it's never used here.
        var info = System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers()
            .FirstOrDefault(r => r.Culture.Name.Equals("en-US", StringComparison.OrdinalIgnoreCase))
            ?? System.Speech.Recognition.SpeechRecognitionEngine.InstalledRecognizers().FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == "en");
        if (info is null) return null;
        using var engine = new System.Speech.Recognition.SpeechRecognitionEngine(info);
        engine.LoadGrammar(new System.Speech.Recognition.DictationGrammar());
        // 16 kHz, 16-bit mono PCM, which the recognizer reads directly.
        var pcm16k = SpeechAudio.Resampled(audio, SpeechAudio.SampleRate, 16000);
        var bytes = new byte[pcm16k.Length * 2];
        for (int i = 0; i < pcm16k.Length; i++)
        {
            short v = (short)Math.Clamp(pcm16k[i] * 32767f, short.MinValue, short.MaxValue);
            bytes[2 * i] = (byte)v;
            bytes[2 * i + 1] = (byte)(v >> 8);
        }
        using var stream = new MemoryStream(bytes);
        engine.SetInputToAudioStream(stream, new System.Speech.AudioFormat.SpeechAudioFormatInfo(16000,
            System.Speech.AudioFormat.AudioBitsPerSample.Sixteen, System.Speech.AudioFormat.AudioChannel.Mono));
        var text = new System.Text.StringBuilder();
        var words = new List<CloneClip.Word>();
        using var done = new ManualResetEventSlim();
        // Every phrase it hears, until the recording ends (the whole stream is read).
        engine.SpeechRecognized += (_, e) =>
        {
            foreach (var w in e.Result.Words)
            {
                var span = e.Result.GetAudioForWordRange(w, w);
                if (text.Length > 0) text.Append(' ');
                text.Append(w.Text);
                words.Add(new CloneClip.Word(span.AudioPosition.TotalSeconds, (span.AudioPosition + span.Duration).TotalSeconds, text.Length));
            }
        };
        engine.RecognizeCompleted += (_, _) => done.Set();
        engine.RecognizeAsync(System.Speech.Recognition.RecognizeMode.Multiple);
        if (!done.Wait(TimeSpan.FromSeconds(90))) engine.RecognizeAsyncCancel();
        if (words.Count == 0) return null;
        return (text.ToString(), words);
    }

    /// The clip as a temporary WAV file, for Play Clip.
    public static string TemporaryFile(float[] audio)
    {
        var file = Path.Combine(Path.GetTempPath(), $"InnerVoice-clip-{Guid.NewGuid():N}.wav");
        WavFile.Write(file, audio);
        return file;
    }

    public static string Clock(double seconds) => string.Create(CultureInfo.InvariantCulture, $"{(int)seconds / 60}:{(int)seconds % 60:00}");
}
