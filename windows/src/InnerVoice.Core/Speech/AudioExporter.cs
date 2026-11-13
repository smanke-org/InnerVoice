// Ported from the Mac's Sources/InnerVoice/Speech/AudioExport.swift (AudioExporter): writes the
// voices to audio files, the whole book, some chapters or one character's lines, as M4A or
// WAV, one file per chapter or all in one, Combined or Separate (a file per voice). Files are
// 44.1 kHz mono like the Mac's; M4A goes through an encoder the app supplies (Media
// Foundation on Windows), since .NET has none of its own.

using System.Text;

namespace InnerVoice.Core.Speech;

public enum AudioFormat { M4a, Wav }

public static class AudioFormats
{
    public static string Title(this AudioFormat f) => f == AudioFormat.M4a ? "M4A" : "WAV";
    public static string Extension(this AudioFormat f) => f == AudioFormat.M4a ? "m4a" : "wav";
    public static AudioFormat? Parse(string? raw) => raw?.ToUpperInvariant() switch { "M4A" => AudioFormat.M4a, "WAV" => AudioFormat.Wav, _ => null };
}

/// Turns a finished WAV (44.1 kHz, 16-bit mono) into an M4A (AAC). Windows builds use Media
/// Foundation; tests use a fake.
public interface IAudioEncoder
{
    Task EncodeM4a(string wavPath, string m4aPath, CancellationToken ct);
}

/// One file to write: its name (without extension) and what goes in it.
public sealed record AudioPart(string Name, IReadOnlyList<SpeechSegment> Segments);

/// A file written: where, which part it holds, which voice (Separate; "" when Combined), and
/// its length in seconds.
public sealed record ExportedAudio(string Path, int Part, string Voice, double Seconds);

/// A 16-bit mono WAV written as it goes (a chapter of audio doesn't fit in memory as floats),
/// its sizes filled in when it's closed.
public sealed class WavWriter : IDisposable
{
    readonly FileStream file;
    readonly BinaryWriter w;
    public int Rate { get; }
    public long Frames { get; private set; }

    public WavWriter(string path, int rate)
    {
        Rate = rate;
        file = File.Create(path);
        w = new BinaryWriter(file, Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8); w.Write(0); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(0);
    }

    public void Write(float[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            short s = (short)Math.Round(Math.Clamp(samples[i], -1f, 1f) * 32767);
            bytes[2 * i] = (byte)s;
            bytes[2 * i + 1] = (byte)(s >> 8);
        }
        w.Write(bytes);
        Frames += samples.Length;
    }

    public void WriteSilence(long frames)
    {
        var chunk = new byte[Math.Min(frames, 65536) * 2];
        long left = frames;
        while (left > 0)
        {
            int n = (int)Math.Min(left, chunk.Length / 2);
            w.Write(chunk, 0, n * 2);
            left -= n;
        }
        Frames += frames;
    }

    public void Dispose()
    {
        long data = Frames * 2;
        w.Flush();
        file.Position = 4;
        w.Write((int)Math.Min(int.MaxValue, 36 + data));
        file.Position = 40;
        w.Write((int)Math.Min(int.MaxValue, data));
        w.Dispose();
        file.Dispose();
    }
}

public static class AudioExporter
{
    /// The files' sample rate, as the Mac's.
    public const int FileRate = 44_100;

    /// Renders and writes each part into `folder`. `progress` hears (done, total, part name)
    /// before each segment, and (total, total, "") at the end. With `stem`, each part is
    /// written Separate: one file per voice (named by `stem`, such as "Narrator" or "Men’s
    /// Voice"), all the same length, with silence wherever another voice speaks, so they line
    /// up when mixed. `timing` hears where each spoken segment starts and ends in its part's
    /// file, in seconds (part index, segment, start, end). Each file is made in a local
    /// temporary folder and moved into place when it's finished (cloud folders take a file
    /// over while it's still being written). Always 1× (Play's speed doesn't apply).
    public static async Task<List<ExportedAudio>> Write(
        IReadOnlyList<AudioPart> parts, string folder, AudioFormat format, VoiceResolver resolver,
        SpeechRenderer? renderer = null, IAudioEncoder? encoder = null, Func<SpeechSegment, string>? stem = null,
        Action<int, SpeechSegment, double, double>? timing = null, Action<int, int, string>? progress = null,
        PauseSettings? pauses = null, CancellationToken ct = default)
    {
        renderer ??= SpeechRenderer.Shared;
        pauses ??= PauseSettings.Current;
        if (format == AudioFormat.M4a && encoder is null)
            throw new InvalidOperationException("M4A needs Windows' audio encoder. Choose WAV instead.");
        int total = parts.Sum(p => p.Segments.Count), done = 0;
        var output = new List<ExportedAudio>();
        var staging = Path.Combine(Path.GetTempPath(), $"InnerVoice Export {Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        Directory.CreateDirectory(folder);
        try
        {
            for (int partIndex = 0; partIndex < parts.Count; partIndex++)
            {
                var part = parts[partIndex];
                if (part.Segments.Count == 0) continue;
                // One file (Combined), or one per voice in this part (Separate), in order of first use.
                var names = new List<string>();
                foreach (var seg in part.Segments)
                {
                    var n = stem?.Invoke(seg) ?? "";
                    if (!names.Contains(n)) names.Add(n);
                }
                var writers = new Dictionary<string, (WavWriter Writer, string Temp, string FileName)>();
                try
                {
                    foreach (var n in names)
                    {
                        var fileName = SafeName(n.Length == 0 ? part.Name : part.Name + " - " + n);
                        var temp = Path.Combine(staging, fileName + ".wav");
                        writers[n] = (new WavWriter(temp, FileRate), temp, fileName);
                    }
                    long position = 0;
                    foreach (var seg in part.Segments)
                    {
                        ct.ThrowIfCancellationRequested();
                        progress?.Invoke(done, total, part.Name);
                        if (resolver.RequestFor(seg) is { } request)
                        {
                            var audio = SpeechAudio.Trimmed(await renderer.Render(request, ct).ConfigureAwait(false));
                            var samples = SpeechAudio.Resampled(audio, SpeechAudio.SampleRate, FileRate);
                            var mine = stem?.Invoke(seg) ?? "";
                            foreach (var (n, x) in writers)
                            {
                                if (n == mine) x.Writer.Write(samples); else x.Writer.WriteSilence(samples.Length);
                            }
                            timing?.Invoke(partIndex, seg, (double)position / FileRate, (double)(position + samples.Length) / FileRate);
                            position += samples.Length;
                        }
                        double gap = seg.Pause == PauseKind.None ? seg.PauseAfter : pauses.Seconds(seg.Pause);
                        if (gap > 0)
                        {
                            // Silence goes in every file alike.
                            long frames = (long)Math.Round(gap * FileRate);
                            foreach (var x in writers.Values) x.Writer.WriteSilence(frames);
                            position += frames;
                        }
                        done++;
                    }
                    foreach (var x in writers.Values) x.Writer.Dispose();
                    // Encode (M4A), then move each into the chosen folder.
                    foreach (var (n, x) in writers)
                    {
                        ct.ThrowIfCancellationRequested();
                        var finished = x.Temp;
                        if (format == AudioFormat.M4a)
                        {
                            finished = Path.ChangeExtension(x.Temp, "m4a");
                            await encoder!.EncodeM4a(x.Temp, finished, ct).ConfigureAwait(false);
                            File.Delete(x.Temp);
                        }
                        var target = Path.Combine(folder, x.FileName + "." + format.Extension());
                        File.Move(finished, target, overwrite: true);
                        output.Add(new ExportedAudio(target, partIndex, n, (double)position / FileRate));
                    }
                }
                finally
                {
                    foreach (var x in writers.Values) try { x.Writer.Dispose(); } catch { }
                }
            }
            progress?.Invoke(total, total, "");
            return output;
        }
        finally
        {
            try { Directory.Delete(staging, recursive: true); } catch { }
        }
    }

    /// Seconds of speech and silence the files hold (for Activity), counting each part once.
    public static double Seconds(IEnumerable<ExportedAudio> files) => files.GroupBy(f => f.Part).Sum(g => g.First().Seconds);

    /// Separate: which file a segment goes in. Everything the narrator's voice reads is
    /// "Narrator"; a shared voice (Casting Style) is its name; anyone else is their own.
    public static Func<SpeechSegment, string> Stems(Project project, BookIndex index, VoiceResolver resolver)
    {
        var readers = CastingStyleRules.Readers(project, index);
        var names = project.Characters.ToDictionary(c => c.Id, c => c.Name);
        return seg =>
        {
            if (seg.SpeakerID is not { } id) return "Narrator";
            bool shared = readers.TryGetValue(id, out var r) && CastingStyleRules.SharedRoles.Contains(r);
            if (resolver.ChoiceFor(seg) == resolver.Narrator && !shared) return "Narrator";
            if (shared) return r.VoiceTitle();
            return names.GetValueOrDefault(id, "Narrator");
        };
    }

    /// A file name without the characters Windows and the Mac forbid.
    public static string SafeName(string s)
    {
        foreach (var bad in "/:\\?%*|\"<>") s = s.Replace(bad, '-');
        return s.Trim().TrimEnd('.');
    }

    /// A chapter's name in lists: its title, or "Opening" / "Untitled".
    public static string ChapterTitle(Project project, int c)
    {
        var t = project.Chapters[c].Title.Trim();
        return t.Length == 0 ? (c == 0 ? "Opening" : "Untitled") : t;
    }

    /// What Export Audio writes, as the Mac's sheet builds it. `characterID`: one character's
    /// lines (`NarratorOnly` for everything the narrator's voice reads); null: all voices.
    public static List<AudioPart> Parts(Project project, BookIndex index, IReadOnlyList<int> chapters, bool narration, bool recaps,
                                        bool perChapter, VoiceResolver resolver, Guid? characterID = null)
    {
        var picked = chapters.Distinct().Order().Where(c => c >= 0 && c < project.Chapters.Count).ToList();
        bool every = picked.Count == project.Chapters.Count;
        var title = project.Title;
        // "Book - Chapters 1–3, 5" when only some chapters go out.
        string which = every ? "" : (characterID is null ? " - " : ", ") + SpeechScript.ChapterLabel(picked);
        if (characterID is null)
        {
            if (picked.Count == 1)
                return [new AudioPart($"{title} - {ChapterTitle(project, picked[0])}",
                                      SpeechScript.Segments(picked[0], project, index, narration, recaps: recaps))];
            if (perChapter)
                return picked.Select(c => new AudioPart($"{c + 1:00} {ChapterTitle(project, c)}",
                                                        SpeechScript.Segments(c, project, index, narration, recaps: recaps))).ToList();
            return [new AudioPart(title + which, SpeechScript.Segments(picked, project, index, narration, recaps: recaps))];
        }
        if (characterID == NarratorOnly)
        {
            // Everything the narrator's voice reads, in order.
            var all = SpeechScript.Segments(picked, project, index, includeNarration: true);
            return [new AudioPart($"{title} - Narrator{which}", all.Where(s => resolver.ChoiceFor(s) == resolver.Narrator).ToList())];
        }
        if (project.Character(characterID) is not { } ch) return [];
        return [new AudioPart($"{title} - {ch.Name}{which}", SpeechScript.Segments(picked, project, index, includeNarration: false, only: ch.Id))];
    }

    /// The Character choice for the narrator's own reading.
    public static readonly Guid NarratorOnly = new("00000000-0000-0000-0000-00000000AAAA");
}
