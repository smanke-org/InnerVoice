// The voice packs: the model files Pocket TTS and Orpheus need, pinned to one revision of each
// Hugging Face repository and checked by SHA-256, downloaded into
// %LOCALAPPDATA%\InnerVoice\Voice Models (APP_VOICE_MODELS in tests, as on the Mac). Like the
// Mac's VoiceModels.swift, one pack per engine; unlike it, the files are ONNX exports.

using System.Security.Cryptography;

namespace InnerVoice.Core.Speech.Neural;

/// A Hugging Face repository at one commit.
public sealed record VoiceRepo(string Id, string Revision, string License, string Credit);

/// One file of a pack: where it comes from and where it goes under the Voice Models folder.
public sealed record VoicePackFile(VoiceRepo Repo, string Path, string Local, long Bytes, string Sha256)
{
    public string Url => $"https://huggingface.co/{Repo.Id}/resolve/{Repo.Revision}/{Path}";
}

/// `RecommendedGpuGB`: the graphics card's own memory the engine wants to keep up with
/// playback; 0 when the processor is fast enough.
public sealed record VoicePack(VoiceEngine Engine, string Title, string Summary, IReadOnlyList<VoicePackFile> Files,
                               int RecommendedMemoryGB, int MinimumMemoryGB, double RecommendedGpuGB)
{
    public long Bytes => Files.Sum(f => f.Bytes);
    public string SizeDescription => Bytes < 1e9 ? $"{Bytes / 1e6:0} MB" : $"{Bytes / 1e9:0.0} GB";
    public IEnumerable<VoiceRepo> Repos => Files.Select(f => f.Repo).Distinct();
}

public static class VoicePacks
{
    /// Pocket TTS: Kyutai's weights (the Mac's mlx-community/pocket-tts is the same model), in
    /// KevinAHM's ONNX export. Its five graphs and the tokenizer are the original January 2026
    /// English model (tts_b6369a24), the files at the repository's root, unchanged since
    /// 2026-02-06 (the newer bundles in its subfolders are later models and other languages).
    /// windows/tools/PocketCheck/verify_weights.py shows every learned weight is Kyutai's.
    static readonly VoiceRepo PocketOnnx = new("KevinAHM/pocket-tts-onnx", "58a6d00cf13d239b6748cb0769f35c580a8f606c",
        "CC BY 4.0 (Kyutai’s model, Mimi codec and tokenizer)", "Pocket TTS by Kyutai; ONNX export by KevinAHM");

    static readonly VoiceRepo OrpheusLm = new("onnx-community/orpheus-3b-0.1-ft-ONNX", "03791aba66ad8415c41090919053b6fcc7a7a256",
        "Apache-2.0 (Orpheus), Llama 3.2 Community License (its base model)", "Orpheus by Canopy Labs, built on Meta’s Llama 3.2; ONNX export by the ONNX Community");
    static readonly VoiceRepo Snac = new("onnx-community/snac_24khz-ONNX", "f317cdff7efb7b2a63148c9e68bbb0320fe629a1",
        "MIT", "SNAC by Hubert Siuzdak; ONNX export by the ONNX Community");

    static readonly VoicePackFile[] All =
    [
    // KevinAHM/pocket-tts-onnx @ 58a6d00cf13d239b6748cb0769f35c580a8f606c (fp32: what the Mac runs)
    new(PocketOnnx, "onnx/flow_lm_main.onnx", "pocket/onnx/flow_lm_main.onnx", 302742542, "1c5f40935cd4a76846666d95dfc6980142db7ecea084a596dcea7a0a8e93746d"),
    new(PocketOnnx, "onnx/flow_lm_flow.onnx", "pocket/onnx/flow_lm_flow.onnx", 39097094, "cf738ec759287034952dad4b21e764310d7838da079920c9584401d6714306e3"),
    new(PocketOnnx, "onnx/mimi_encoder.onnx", "pocket/onnx/mimi_encoder.onnx", 73165554, "360f050cd0b1e1c9e92e25584391a2214cb7574ba16c67ac5f471f9dce8588e4"),
    new(PocketOnnx, "onnx/mimi_decoder.onnx", "pocket/onnx/mimi_decoder.onnx", 41471963, "f58ecc92a349d64541f2dc7742de8f5559e4953b4bae0bd974d4b9ea8ecd2e8b"),
    new(PocketOnnx, "onnx/text_conditioner.onnx", "pocket/onnx/text_conditioner.onnx", 16388363, "80ea69f46d8153a9bd42373723cedae4c88ccbde0e052c2a96e9e8f19445adf1"),
    // Kyutai's SentencePiece model, the same file as kyutai/pocket-tts-without-voice-cloning's.
    new(PocketOnnx, "tokenizer.model", "pocket/tokenizer.model", 59339, "d461765ae179566678c93091c5fa6f2984c31bbe990bf1aa62d92c64d91bc3f6"),
    new(PocketOnnx, "onnx/LICENSE", "pocket/LICENSE", 18655, "fe7b4ce83b8381cc5b216bbb4af73c570688d1b819c73bbaed8ca401f4677cd6"),
    // onnx-community/orpheus-3b-0.1-ft-ONNX @ 03791aba66ad8415c41090919053b6fcc7a7a256
    new(OrpheusLm, "config.json", "orpheus/config.json", 1158, "e43c6ad5c3d0aa5782ac69849d1305fc0adfbdde496dbfcffd138d9514ee93d1"),
    new(OrpheusLm, "onnx/model_q4.onnx", "orpheus/onnx/model_q4.onnx", 281966, "a7bdfbdb63768b08ccb70b59dcad21595b34dc2ba0f212184d2408a283adc61c"),
    new(OrpheusLm, "onnx/model_q4.onnx_data", "orpheus/onnx/model_q4.onnx_data", 2085583936, "87536d71853fb2d69b9a91681783a552b0ec2229a01403acf17f100a8cda3982"),
    new(OrpheusLm, "onnx/model_q4.onnx_data_1", "orpheus/onnx/model_q4.onnx_data_1", 337790976, "0e3f53730fdb75b148e29e405a5106c407c8ffc87e2f86674cdfa8d4f518c84f"),
    new(OrpheusLm, "tokenizer.json", "orpheus/tokenizer.json", 15722697, "f17508bf9f772747e43b038fae379a4c3e80f5a7d39da8123bbf9794b94f20b6"),
    // onnx-community/snac_24khz-ONNX @ f317cdff7efb7b2a63148c9e68bbb0320fe629a1
    new(Snac, "onnx/decoder_model.onnx", "orpheus/snac/decoder_model.onnx", 52600822, "9e2991fce96c3b49c508304e656052f4f6b860fe8432164fcd976eee195fd4f0"),
    ];

    /// Pocket TTS: small and quick, clones every voice from a clip. The Mac's text, "this PC".
    /// About 2 GB of memory while it speaks on ONNX Runtime (measured; 1 GB on the Mac's MLX);
    /// fast enough on a processor, so no card is needed.
    public static readonly VoicePack Pocket = new(VoiceEngine.Pocket, "Pocket TTS",
        "Kyutai's small, fast model: nine stock voices and the voices you clone on this PC, kept the same on every line. Uses about 2 GB of memory while it speaks.",
        All.Where(f => f.Local.StartsWith("pocket/", StringComparison.Ordinal)).ToList(), RecommendedMemoryGB: 8, MinimumMemoryGB: 4, RecommendedGpuGB: 0);

    /// Orpheus: eight expressive voices that laugh, sigh and gasp. The Mac's text.
    public static readonly VoicePack Orpheus = new(VoiceEngine.Orpheus, "Orpheus",
        "Eight natural American voices that act out the words, with sighs, laughs and gasps from the speech tags.",
        All.Where(f => f.Local.StartsWith("orpheus/", StringComparison.Ordinal)).ToList(), RecommendedMemoryGB: 16, MinimumMemoryGB: 8, RecommendedGpuGB: 8);

    public static IReadOnlyList<VoicePack> Packs { get; } = [Pocket, Orpheus];

    public static VoicePack? For(VoiceEngine engine) => engine switch
    {
        VoiceEngine.Pocket => Pocket,
        VoiceEngine.Orpheus => Orpheus,
        _ => null,
    };

    /// The Voice Models folder. The app sets it at startup (test runs: a temporary folder);
    /// APP_VOICE_MODELS overrides it, as on the Mac.
    public static string Folder
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("APP_VOICE_MODELS") is { Length: > 0 } env ? env : folder
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerVoice", "Voice Models");
            return dir;
        }
        set { folder = value; Forget(); }
    }
    static string? folder;

    public static string PathOf(VoicePackFile f) => Path.Combine(Folder, f.Local.Replace('/', Path.DirectorySeparatorChar));
    public static string PocketFolder => Path.Combine(Folder, "pocket");
    public static string OrpheusFolder => Path.Combine(Folder, "orpheus");

    /// Qwen3-TTS (retired 2026-11-13): its models (Voice Models\qwen) and voice samples (Voice
    /// Models\Samples), if this PC still has them. Settings › Voices offers to remove them.
    public static IReadOnlyList<string> OldQwenFiles =>
        new[] { Path.Combine(Folder, "qwen"), Path.Combine(Folder, "Samples") }.Where(Directory.Exists).ToList();

    /// How much room the old files take.
    public static long SizeOf(IEnumerable<string> folders) => folders.Sum(d =>
    {
        try { return new DirectoryInfo(d).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return 0L; }
    });

    static readonly Dictionary<VoiceEngine, bool> installed = [];
    static readonly Lock gate = new();

    /// Every file is in place at its full size (each was checked by SHA-256 before it was
    /// moved there). Cached until a download or removal: views ask on every redraw.
    public static bool IsInstalled(VoicePack pack)
    {
        lock (gate)
        {
            if (installed.TryGetValue(pack.Engine, out var known)) return known;
            bool all = pack.Files.All(f => new FileInfo(PathOf(f)) is { Exists: true } fi && fi.Length == f.Bytes);
            installed[pack.Engine] = all;
            return all;
        }
    }

    public static bool IsInstalled(VoiceEngine engine) => For(engine) is { } p && IsInstalled(p);

    /// Bytes already downloaded (finished files and .part files).
    public static long BytesPresent(VoicePack pack) => pack.Files.Sum(f =>
        File.Exists(PathOf(f)) ? f.Bytes : new FileInfo(PathOf(f) + ".part") is { Exists: true } part ? Math.Min(part.Length, f.Bytes) : 0);

    public static void Forget()
    {
        lock (gate) installed.Clear();
    }

    /// Removes a pack's files. Files another pack still needs stay.
    public static void Remove(VoicePack pack)
    {
        var keep = Packs.Where(p => p != pack).SelectMany(p => p.Files).Select(f => f.Local).ToHashSet();
        foreach (var f in pack.Files.Where(f => !keep.Contains(f.Local)))
        {
            try { File.Delete(PathOf(f)); } catch (IOException) { }
            try { File.Delete(PathOf(f) + ".part"); } catch (IOException) { }
        }
        Forget();
    }
}

/// Progress of a pack download: a share of the bytes, or checking them.
public readonly record struct VoicePackProgress(double Fraction, long Bytes, bool Verifying);

/// Downloads a pack: each file to a .part file (resuming one a cancel or a dropped connection
/// left), checked by SHA-256, then moved into place, so a file in place is always whole.
public static class VoicePackDownloader
{
    public sealed class NotEnoughSpaceException(string message) : IOException(message);

    /// Room for the rest of the pack and a gigabyte to spare.
    public static void CheckSpace(VoicePack pack)
    {
        long needed = pack.Bytes - VoicePacks.BytesPresent(pack) + 1_000_000_000;
        try
        {
            Directory.CreateDirectory(VoicePacks.Folder);
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(VoicePacks.Folder))!).AvailableFreeSpace;
            if (free < needed) throw new NotEnoughSpaceException($"Not enough free space: {needed / 1e9:0.0} GB needed.");
        }
        catch (NotEnoughSpaceException) { throw; }
        catch (Exception) { /* can't tell: try anyway */ }
    }

    public static async Task Download(VoicePack pack, HttpClient http, IProgress<VoicePackProgress>? progress, CancellationToken ct)
    {
        CheckSpace(pack);
        long total = pack.Bytes, done = 0;
        foreach (var f in pack.Files)
        {
            var destination = VoicePacks.PathOf(f);
            if (File.Exists(destination) && new FileInfo(destination).Length == f.Bytes) { done += f.Bytes; continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            long before = done;
            await DownloadFile(f, destination, http, b => progress?.Report(new((double)(before + b) / total, before + b, false)), ct).ConfigureAwait(false);
            done += f.Bytes;
            progress?.Report(new((double)done / total, done, false));
        }
        VoicePacks.Forget();
    }

    static async Task DownloadFile(VoicePackFile f, string destination, HttpClient http, Action<long> progress, CancellationToken ct)
    {
        var part = destination + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.Bytes) { File.Delete(part); have = 0; }
        if (have < f.Bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, f.Url);
            if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // the server ignored the range
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
            var buffer = new byte[1 << 20];
            long bytes = have;
            var last = DateTime.MinValue;
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                bytes += n;
                if ((DateTime.UtcNow - last).TotalMilliseconds > 250) { last = DateTime.UtcNow; progress(bytes); }
            }
        }
        string hash;
        await using (var s = File.OpenRead(part)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct).ConfigureAwait(false));
        if (hash != f.Sha256)
        {
            File.Delete(part);
            throw new IOException($"The download of {System.IO.Path.GetFileName(f.Path)} was damaged (its checksum didn’t match). Try again.");
        }
        File.Move(part, destination, overwrite: true);
    }
}
