// The model list from Sources/InnerVoiceKit/AI/ModelCatalog.swift (same files, same SHA-256),
// with advice rewritten for PCs: a Mac shares its memory with the GPU; a PC has system
// memory plus, on many machines, a graphics card with its own memory.

namespace InnerVoice.Core.AI;

/// One downloadable GGUF model. The SHA-256 is the Hugging Face repo's LFS object id.
public sealed record CatalogModel(string Id, string Name, string Repo, string File, long Bytes, string Sha256,
                                  int Quality, int Speed, string Blurb, string License)
{
    public string DownloadUrl => $"https://huggingface.co/{Repo}/resolve/main/{File}";
    public string SizeDescription => $"{Bytes / 1_000_000_000.0:0.0} GB";
    /// Memory the model needs while it reads: the weights plus its working memory.
    public double WorkingGB => Bytes / 1_073_741_824.0 + 1.5;
}

/// This PC, for model advice: system memory and the best graphics card's own memory.
public sealed record Hardware(int MemoryGB, double GpuGB, string? GpuName)
{
    public bool HasGpu => GpuGB >= 3;
}

public enum Fit { Recommended, Good, Tight, TooLarge }

public sealed record ModelAdvice(Fit Fit, string Note)
{
    public string Title => Fit switch
    {
        Fit.Recommended => "Best for this PC",
        Fit.Good => "Good fit",
        Fit.Tight => "Tight fit",
        _ => "Too large",
    };
}

public static class ModelCatalog
{
    public static readonly IReadOnlyList<CatalogModel> All =
    [
        new("gemma-4-e2b", "Gemma 4 E2B", "unsloth/gemma-4-E2B-it-GGUF", "gemma-4-E2B-it-UD-Q4_K_XL.gguf", 3_184_496_736,
            "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32", 2, 5, "Light and fast; reads plain dialogue well, struggles with long back-and-forths.", "Apache 2.0"),
        new("gemma-4-e4b", "Gemma 4 E4B", "unsloth/gemma-4-E4B-it-GGUF", "gemma-4-E4B-it-UD-Q4_K_XL.gguf", 5_126_306_944,
            "3cf61de12daa015ee0f7b68e7b7c541405bf220e1e942bad8b47cab827d7df80", 3, 4, "A strong all-rounder: follows who is speaking through most scenes.", "Apache 2.0"),
        new("llama-3.1-8b", "Llama 3.1 8B Instruct", "unsloth/Llama-3.1-8B-Instruct-GGUF", "Llama-3.1-8B-Instruct-Q4_K_M.gguf", 4_920_739_200,
            "b3bdbf23b47d7e6bb791c99b206deb169cd5a96362a9e3399028df2faacdc506", 3, 5, "Quick and careful; builds full character summaries for its size.", "Llama 3.1 Community License"),
        new("phi-4", "Phi-4 14B", "unsloth/phi-4-GGUF", "phi-4-Q4_K_M.gguf", 8_890_306_112,
            "01e1f25b3e6931054c6c2227b06f4969828434eebc299e8e171f55dab6814485", 4, 3, "Keeps track of who's who in busy scenes, on a mid-size PC.", "MIT"),
        new("gpt-oss-20b", "gpt-oss 20B", "unsloth/gpt-oss-20b-GGUF", "gpt-oss-20b-Q4_K_M.gguf", 11_624_759_488,
            "c27536640e410032865dc68781d80a08b98f8db5e93575919af8ccc0568aeb4f", 4, 5, "Very fast at tagging speakers; writes thinner character summaries.", "Apache 2.0"),
        new("mistral-small-3.2", "Mistral Small 3.2 24B", "unsloth/Mistral-Small-3.2-24B-Instruct-2506-GGUF", "Mistral-Small-3.2-24B-Instruct-2506-UD-Q4_K_XL.gguf", 14_548_880_928,
            "bcf82c1d4963f91d744d202efeee0724c0987625cd97a737ea2af68b49f141cf", 4, 3, "The fullest character summaries here, and a steady reader of dialogue.", "Apache 2.0"),
        new("gemma-4-26b-a4b", "Gemma 4 26B A4B", "unsloth/gemma-4-26B-A4B-it-GGUF", "gemma-4-26B-A4B-it-UD-Q4_K_XL.gguf", 17_010_980_576,
            "ef728c8e0c337fd1067b947af006e38a9ef2419e56feced4fd29b4bf0636e30c", 4, 4, "Large-model accuracy at mid-size speed (it only wakes part of itself per word).", "Apache 2.0"),
        new("gemma-4-31b", "Gemma 4 31B", "unsloth/gemma-4-31B-it-GGUF", "gemma-4-31B-it-UD-Q4_K_XL.gguf", 18_822_970_304,
            "9e92cb6236044c6a9870af406029c74a76e0571c157a6f95df724dcc8c7a1575", 5, 2, "The most accurate reader here, best for tangled ensemble scenes. Slow.", "Apache 2.0"),
    ];

    public static CatalogModel? Model(string id) => All.FirstOrDefault(m => m.Id == id);

    /// Models no longer offered (the Qwen 3 family, removed 2026-11-13). A saved choice of
    /// one of these is cleared so the narrator picks again; the file stays until removed.
    public static readonly IReadOnlySet<string> RetiredIDs = new HashSet<string> { "qwen3-4b", "qwen3-8b", "qwen3-14b", "qwen3-30b-a3b" };

    /// Whether a saved selection ("catalog:<id>") names a retired model.
    public static bool IsRetiredSelection(string? selection) =>
        selection is { } s && s.StartsWith("catalog:") && RetiredIDs.Contains(s["catalog:".Length..]);

    /// The models the Mac suggests at some memory size (its `recommendedID`). gpt-oss and
    /// Mistral Small stay choices, never the suggestion: the Mac doesn't suggest them either
    /// (gpt-oss writes thinner summaries, and Phi-4 or Gemma 26B do as well at their sizes).
    static readonly HashSet<string> Suggested = ["gemma-4-e2b", "gemma-4-e4b", "phi-4", "gemma-4-26b-a4b", "gemma-4-31b"];

    /// Fits entirely in the graphics card's own memory.
    public static bool FitsGpu(CatalogModel m, Hardware hw) => hw.HasGpu && m.WorkingGB <= hw.GpuGB * 0.92;

    /// The model to suggest. With a graphics card: the most accurate model that fits in its
    /// memory. Without one the processor does the work, which is much slower, so a small model.
    public static string RecommendedID(Hardware hw)
    {
        var onGpu = All.Where(m => Suggested.Contains(m.Id) && FitsGpu(m, hw) && m.WorkingGB <= hw.MemoryGB * 0.66).ToList();
        if (onGpu.Count > 0) return onGpu.OrderByDescending(m => m.Quality).ThenByDescending(m => m.Speed).ThenBy(m => m.Bytes).First().Id;
        return hw.MemoryGB >= 16 ? "gemma-4-e4b" : "gemma-4-e2b";
    }

    /// Why a model is a good or bad choice for this PC. The narrator usually has recording
    /// software, a browser and the manuscript open too.
    public static ModelAdvice Advice(CatalogModel model, Hardware hw)
    {
        double ram = hw.MemoryGB;
        double need = model.WorkingGB;
        var size = Math.Ceiling(need).ToString("0");
        bool gpu = FitsGpu(model, hw);
        var where = gpu ? $"your graphics card's {hw.GpuGB:0} GB" : $"your {hw.MemoryGB} GB";
        if (model.Id == RecommendedID(hw))
            return new(Fit.Recommended, gpu
                ? $"Runs on your graphics card ({size} of its {hw.GpuGB:0} GB). The best balance of accuracy and speed for this PC."
                : $"Uses about {size} GB of your {hw.MemoryGB} GB, leaving room for your recording software. Small enough to run on the processor at a usable speed.");
        if (need > ram * 0.66 && !gpu)
            return new(Fit.TooLarge, $"Needs about {size} GB, more than this PC can give it ({hw.MemoryGB} GB in all). Windows would swap to disk, and tagging a book could take many hours or fail.");
        if (!gpu && need > ram * 0.45)
            return new(Fit.Tight, $"Fits, using about {size} GB of your {hw.MemoryGB} GB, but runs on the processor. Quit other large apps while it tags; it may take hours for a novel.");
        if (!gpu && hw.HasGpu)
            return new(Fit.Tight, $"Too big for your graphics card's {hw.GpuGB:0} GB, so most of it runs on the processor: expect it to be slow.");
        if (!gpu)
            return new(Fit.Good, $"Runs on the processor in about {size} GB. Slower than on a graphics card; a smaller model finishes sooner.");
        var best = All.Where(m => FitsGpu(m, hw)).Select(m => m.Quality).DefaultIfEmpty(model.Quality).Max();
        if (model.Quality < best)
            return new(Fit.Good, $"Uses only about {size} GB of {where}, so it's quick, but this PC can run a more accurate model. Expect more red lines to check.");
        return new(Fit.Good, $"Fits comfortably in about {size} GB of {where}.");
    }

    /// Rough minutes to tag a book of this many words, for the progress text.
    public static int EstimatedMinutes(int words, CatalogModel model, bool onGpu)
    {
        double wpm = model.Speed switch { 1 => 2500, 2 => 5000, 3 => 9000, 4 => 14000, 5 => 20000, _ => 8000 };
        if (!onGpu) wpm /= 5;
        return Math.Max(1, (int)Math.Ceiling(words / wpm));
    }
}
