using System.Security.Cryptography;
using Avalonia.Threading;
using InnerVoice.Core.AI;

namespace InnerVoice.App.AI;

/// A model ready to answer prompts. One at a time: the engine has one context.
public sealed class TextModel(LlamaEngine engine, string displayName)
{
    static readonly SemaphoreSlim Gate = new(1, 1);
    public string DisplayName { get; } = displayName;
    /// Words per passage the model takes in comfortably.
    public int PassageWords => 900;

    public async Task<string> Complete(IEnumerable<ChatMessage> messages, string? grammar, int maxTokens, Action<double>? progress = null,
                                       CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);
        try
        {
            using var reg = ct.Register(engine.Cancel);
            var text = await Task.Run(() =>
            {
                var prompt = engine.Template.Render(messages, "", engine.CloseThinking);
                return engine.Generate(prompt, maxTokens, grammar: grammar, progress: progress) ?? "";
            }, ct);
            ct.ThrowIfCancellationRequested();
            foreach (var stop in engine.Template.StopStrings())
            {
                int at = text.IndexOf(stop, StringComparison.Ordinal);
                if (at >= 0) text = text[..at];
            }
            return text;
        }
        finally { Gate.Release(); }
    }
}

/// Downloads, lists and loads models. The choice is saved as "catalog:<id>" or "file:<path>".
public sealed class ModelManager
{
    public static ModelManager Shared { get; } = new();

    public abstract record DownloadState
    {
        public sealed record Downloading(double Fraction, long Bytes) : DownloadState;
        public sealed record Verifying : DownloadState;
        public sealed record Failed(string Message) : DownloadState;
    }

    public enum LoadState { None, Loading, Ready, Failed }

    public event Action? Changed;
    public Dictionary<string, DownloadState> Downloads { get; } = [];
    public HashSet<string> Downloaded { get; } = [];
    public LoadState Load { get; private set; }
    public string? LoadError { get; private set; }

    readonly LlamaEngine engine = new();
    readonly Dictionary<string, CancellationTokenSource> downloaders = [];
    string? loadedPath;
    Hardware? hardware;

    ModelManager() => Refresh();

    public string Selection
    {
        get => Environment.GetEnvironmentVariable("INNERVOICE_MODEL") is { Length: > 0 } m ? m : AppSettings.Current.ModelID ?? "";
        set
        {
            AppSettings.Update(s => s.ModelID = value);
            Unload();
            Notify();
        }
    }

    public static string ModelsFolder
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("INNERVOICE_MODELS") is { Length: > 0 } env ? env
                : AppSettings.Current.ModelsFolder is { Length: > 0 } custom ? custom : AppPaths.Models;
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    /// System memory and the best graphics card, read once.
    public Hardware Hardware
    {
        get
        {
            if (hardware is not null) return hardware;
            long bytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            int gb = (int)Math.Round(bytes / 1_073_741_824.0);
            var gpus = LlamaLibrary.IsAvailable ? LlamaEngine.Gpus() : [];
            var best = gpus.OrderByDescending(g => g.TotalGB).FirstOrDefault();
            return hardware = new Hardware(gb, best.TotalGB, best.Name);
        }
    }

    public string RecommendedID => ModelCatalog.RecommendedID(Hardware);

    public void Refresh()
    {
        Downloaded.Clear();
        foreach (var m in ModelCatalog.All)
            if (File.Exists(Path.Combine(ModelsFolder, m.File))) Downloaded.Add(m.Id);
    }

    void Notify() => Dispatcher.UIThread.Post(() => Changed?.Invoke());

    public CatalogModel? CatalogModel => Selection.StartsWith("catalog:") ? ModelCatalog.Model(Selection["catalog:".Length..]) : null;

    public string SelectionName =>
        CatalogModel?.Name ?? (Selection.StartsWith("file:") ? Path.GetFileNameWithoutExtension(Selection["file:".Length..]) : "No model");

    /// True when Tag Speakers can run without asking the narrator to pick a model.
    public bool IsReady =>
        CatalogModel is { } m ? Downloaded.Contains(m.Id) : Selection.StartsWith("file:") && File.Exists(Selection["file:".Length..]);

    public bool SelectionRunsOnGpu => CatalogModel is { } m ? ModelCatalog.FitsGpu(m, Hardware) : Hardware.HasGpu;

    // MARK: Loading

    public sealed class ModelException(string message) : Exception(message);

    /// Loads the chosen model (once) and returns it.
    public async Task<TextModel> Model()
    {
        if (!LlamaLibrary.IsAvailable) throw new ModelException("The AI engine (llama.cpp) is missing from this copy of InnerVoice. Reinstall InnerVoice.");
        string? path = null;
        if (CatalogModel is { } m)
        {
            if (!Downloaded.Contains(m.Id)) throw new ModelException("The chosen model isn't downloaded yet. Download it in Settings › AI.");
            path = Path.Combine(ModelsFolder, m.File);
        }
        else if (Selection.StartsWith("file:")) path = Selection["file:".Length..];
        if (path is null) throw new ModelException("Choose a model in Settings › AI first.");
        if (loadedPath == path && Load == LoadState.Ready) return new TextModel(engine, SelectionName);
        Load = LoadState.Loading;
        Notify();
        try
        {
            var p = path;
            await Task.Run(() => engine.Load(p, new LlamaEngine.Options(ContextLength: 8192)));
            loadedPath = path;
            Load = LoadState.Ready;
            Notify();
            return new TextModel(engine, SelectionName);
        }
        catch (Exception e)
        {
            Load = LoadState.Failed;
            LoadError = e.Message;
            Notify();
            throw new ModelException(e.Message);
        }
    }

    public void Unload()
    {
        engine.Cancel();
        loadedPath = null;
        Load = LoadState.None;
        _ = Task.Run(engine.Unload);
    }

    /// Frees the model before the app quits; ggml aborts if its GPU memory is freed during exit.
    public void UnloadNow()
    {
        engine.Cancel();
        engine.Unload();
        loadedPath = null;
        Load = LoadState.None;
    }

    public void ChooseFile(string path) => Selection = "file:" + path;

    public void Delete(CatalogModel m)
    {
        if (Selection == "catalog:" + m.Id) Unload();
        try { File.Delete(Path.Combine(ModelsFolder, m.File)); } catch { }
        Refresh();
        Notify();
    }

    // MARK: Downloading

    static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public void Download(CatalogModel m)
    {
        if (downloaders.ContainsKey(m.Id)) return;
        try
        {
            var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(ModelsFolder))!).AvailableFreeSpace;
            if (free < m.Bytes + 1_000_000_000)
            {
                Downloads[m.Id] = new DownloadState.Failed($"Not enough free space: {m.SizeDescription} needed.");
                Notify();
                return;
            }
        }
        catch { }
        var cts = new CancellationTokenSource();
        downloaders[m.Id] = cts;
        Downloads[m.Id] = new DownloadState.Downloading(0, 0);
        Notify();
        _ = Task.Run(() => Run(m, cts.Token));
    }

    public void CancelDownload(CatalogModel m)
    {
        if (downloaders.Remove(m.Id, out var cts)) cts.Cancel();
        Downloads.Remove(m.Id);
        Notify();
    }

    /// Downloads to a .part file (resuming one left by a cancel or a dropped connection),
    /// checks the SHA-256, then moves it into place.
    async Task Run(CatalogModel m, CancellationToken ct)
    {
        var destination = Path.Combine(ModelsFolder, m.File);
        var part = destination + ".part";
        try
        {
            long have = File.Exists(part) ? new FileInfo(part).Length : 0;
            if (have < m.Bytes)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, m.DownloadUrl);
                if (have > 0) request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
                using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                if (have > 0 && response.StatusCode != System.Net.HttpStatusCode.PartialContent) have = 0; // server ignored the range
                response.EnsureSuccessStatusCode();
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
                var buffer = new byte[1 << 20];
                long bytes = have;
                var last = DateTime.MinValue;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, n), ct);
                    bytes += n;
                    if ((DateTime.UtcNow - last).TotalMilliseconds > 250)
                    {
                        last = DateTime.UtcNow;
                        long b = bytes;
                        Dispatcher.UIThread.Post(() => { if (downloaders.ContainsKey(m.Id)) { Downloads[m.Id] = new DownloadState.Downloading((double)b / m.Bytes, b); Changed?.Invoke(); } });
                    }
                }
            }
            Dispatcher.UIThread.Post(() => { Downloads[m.Id] = new DownloadState.Verifying(); Changed?.Invoke(); });
            string hash;
            await using (var f = File.OpenRead(part)) hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(f, ct));
            if (hash != m.Sha256)
            {
                File.Delete(part);
                throw new IOException("The download was damaged (its checksum didn't match). Try again.");
            }
            File.Move(part, destination, overwrite: true);
            Dispatcher.UIThread.Post(() =>
            {
                Downloads.Remove(m.Id);
                downloaders.Remove(m.Id);
                Refresh();
                if (!IsReady) Selection = "catalog:" + m.Id;
                Changed?.Invoke();
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            var message = e is HttpRequestException ? "Couldn't reach Hugging Face. Check the connection, then try again; the download picks up where it stopped." : e.Message;
            Dispatcher.UIThread.Post(() =>
            {
                Downloads[m.Id] = new DownloadState.Failed(message);
                downloaders.Remove(m.Id);
                Changed?.Invoke();
            });
        }
    }
}
