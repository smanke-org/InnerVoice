// Ported from the Mac's Sources/InnerVoice/Speech/SpeechRenderer.swift: makes the audio for
// a request, from the cache when it was made before. Rendered audio lives in the app's own
// folder (not the project), so projects stay small and replaying a line is instant.

namespace InnerVoice.Core.Speech;

public sealed class SpeechRenderer
{
    /// The app sets this at startup (LocalAppData\InnerVoice\Speech; test runs: a temp folder).
    public static SpeechRenderer Shared { get; set; } = new(Path.Combine(Path.GetTempPath(), "InnerVoice Speech (test)"));

    public string CacheFolder { get; }
    readonly Func<VoiceEngine, IVoiceEngine?> engineFor;
    int pruned;

    public SpeechRenderer(string cacheFolder, Func<VoiceEngine, IVoiceEngine?>? engines = null)
    {
        CacheFolder = cacheFolder;
        engineFor = engines ?? VoiceEngineRegistry.Get;
        Directory.CreateDirectory(cacheFolder);
    }

    string FileFor(SpeechRequest r) => Path.Combine(CacheFolder, r.CacheKey + ".wav");

    public bool IsCached(SpeechRequest request) => File.Exists(FileFor(request));

    public async Task<float[]> Render(SpeechRequest request, CancellationToken ct = default)
    {
        if (request.Text.Trim().Length == 0) return SpeechAudio.Silence(0.05);
        var file = FileFor(request);
        if (Read(file) is { } cached) return cached;
        if (request.Choice.Engine == VoiceEngine.Mixed || engineFor(request.Choice.Engine) is not { } engine)
            throw SpeechException.EngineUnavailable(request.Choice.Engine);
        if (!engine.IsReady) throw SpeechException.EngineUnavailable(request.Choice.Engine);
        var samples = await engine.Render(request, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        // Shading: what the engine didn't do itself is done to the finished audio.
        if (request.Shade is { } s && request.IsShaded)
        {
            int pitch = engine.ShadesPitch ? 0 : s.Pitch, pace = engine.ShadesPace ? 0 : s.Pace;
            if (pitch != 0 || pace != 0) samples = await Task.Run(() => ShadeAudio.Apply(samples, pitch, pace), ct).ConfigureAwait(false);
        }
        Write(samples, file);
        PruneIfNeeded();
        return samples;
    }

    static float[]? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            return WavFile.Read(path);
        }
        catch { return null; }
    }

    static void Write(float[] samples, string path)
    {
        // A temporary file of its own: Export Audio makes several lines at once, and the same
        // line (the same words in the same voice) can be in two of them.
        var temp = $"{path}.{Guid.NewGuid():N}.part";
        try
        {
            WavFile.Write(temp, samples, float32: true);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch { }
        }
    }

    /// Keeps the cache under about 1 GB, removing the oldest audio first.
    void PruneIfNeeded()
    {
        if (Interlocked.Exchange(ref pruned, 1) == 1) return;
        var folder = CacheFolder;
        _ = Task.Run(() => Prune(folder, 1_000_000_000, 700_000_000));
    }

    public static void Prune(string folder, long limit, long target)
    {
        try
        {
            var files = new DirectoryInfo(folder).GetFiles("*.wav").ToList();
            long total = files.Sum(f => f.Length);
            if (total <= limit) return;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc))
            {
                if (total <= target) break;
                total -= f.Length;
                try { f.Delete(); } catch { }
            }
        }
        catch { }
    }

    public void ClearCache()
    {
        try { Directory.Delete(CacheFolder, recursive: true); } catch { }
        Directory.CreateDirectory(CacheFolder);
    }

    public long CacheSize
    {
        get
        {
            try { return new DirectoryInfo(CacheFolder).GetFiles().Sum(f => f.Length); } catch { return 0; }
        }
    }
}
