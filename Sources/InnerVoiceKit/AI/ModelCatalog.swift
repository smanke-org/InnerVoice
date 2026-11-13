import Foundation

/// One downloadable GGUF model. Files come straight from their Hugging Face repos; the
/// SHA-256 is the repo's LFS object id, checked after download.
public struct CatalogModel: Identifiable, Hashable, Sendable {
    public let id: String
    public let name: String
    public let repo: String
    public let file: String
    public let bytes: Int64
    public let sha256: String
    /// Rough quality for reading who speaks in fiction, 1 (basic) to 5 (best).
    public let quality: Int
    /// Rough speed on Apple silicon, 1 (slow) to 5 (fast).
    public let speed: Int
    /// What it is good at, in a narrator's words.
    public let blurb: String
    public let license: String

    public var downloadURL: URL { URL(string: "https://huggingface.co/\(repo)/resolve/main/\(file)")! }
    public var sizeDescription: String { ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file) }
    /// Memory the model needs while it reads: the weights plus its working memory.
    public var workingGB: Double { Double(bytes) / 1_073_741_824 + 1.5 }
}

/// How well a model suits this Mac's memory.
public struct ModelAdvice: Hashable, Sendable {
    public enum Fit: Int, Comparable, Sendable {
        case recommended, good, tight, tooLarge
        public static func < (a: Fit, b: Fit) -> Bool { a.rawValue < b.rawValue }
        public var title: String {
            switch self {
            case .recommended: "Best for this Mac"
            case .good: "Good fit"
            case .tight: "Tight fit"
            case .tooLarge: "Too large"
            }
        }
    }
    public var fit: Fit
    public var note: String
}

public enum ModelCatalog {
    public static let all: [CatalogModel] = [
        CatalogModel(id: "gemma-4-e2b", name: "Gemma 4 E2B", repo: "unsloth/gemma-4-E2B-it-GGUF",
                     file: "gemma-4-E2B-it-UD-Q4_K_XL.gguf", bytes: 3_184_496_736,
                     sha256: "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32",
                     quality: 2, speed: 5, blurb: "Light and fast; reads plain dialogue well, struggles with long back-and-forths.", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-e4b", name: "Gemma 4 E4B", repo: "unsloth/gemma-4-E4B-it-GGUF",
                     file: "gemma-4-E4B-it-UD-Q4_K_XL.gguf", bytes: 5_126_306_944,
                     sha256: "3cf61de12daa015ee0f7b68e7b7c541405bf220e1e942bad8b47cab827d7df80",
                     quality: 3, speed: 4, blurb: "A strong all-rounder: follows who is speaking through most scenes.", license: "Apache 2.0"),
        CatalogModel(id: "llama-3.1-8b", name: "Llama 3.1 8B Instruct", repo: "unsloth/Llama-3.1-8B-Instruct-GGUF",
                     file: "Llama-3.1-8B-Instruct-Q4_K_M.gguf", bytes: 4_920_739_200,
                     sha256: "b3bdbf23b47d7e6bb791c99b206deb169cd5a96362a9e3399028df2faacdc506",
                     quality: 3, speed: 5, blurb: "Quick and careful; builds full character summaries for its size.", license: "Llama 3.1 Community License"),
        CatalogModel(id: "phi-4", name: "Phi-4 14B", repo: "unsloth/phi-4-GGUF",
                     file: "phi-4-Q4_K_M.gguf", bytes: 8_890_306_112,
                     sha256: "01e1f25b3e6931054c6c2227b06f4969828434eebc299e8e171f55dab6814485",
                     quality: 4, speed: 3, blurb: "Keeps track of who's who in busy scenes, on a mid-size Mac.", license: "MIT"),
        CatalogModel(id: "gpt-oss-20b", name: "gpt-oss 20B", repo: "unsloth/gpt-oss-20b-GGUF",
                     file: "gpt-oss-20b-Q4_K_M.gguf", bytes: 11_624_759_488,
                     sha256: "c27536640e410032865dc68781d80a08b98f8db5e93575919af8ccc0568aeb4f",
                     quality: 4, speed: 5, blurb: "Very fast at tagging speakers; writes thinner character summaries.", license: "Apache 2.0"),
        CatalogModel(id: "mistral-small-3.2", name: "Mistral Small 3.2 24B", repo: "unsloth/Mistral-Small-3.2-24B-Instruct-2506-GGUF",
                     file: "Mistral-Small-3.2-24B-Instruct-2506-UD-Q4_K_XL.gguf", bytes: 14_548_880_928,
                     sha256: "bcf82c1d4963f91d744d202efeee0724c0987625cd97a737ea2af68b49f141cf",
                     quality: 4, speed: 3, blurb: "The fullest character summaries here, and a steady reader of dialogue.", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-26b-a4b", name: "Gemma 4 26B A4B", repo: "unsloth/gemma-4-26B-A4B-it-GGUF",
                     file: "gemma-4-26B-A4B-it-UD-Q4_K_XL.gguf", bytes: 17_010_980_576,
                     sha256: "ef728c8e0c337fd1067b947af006e38a9ef2419e56feced4fd29b4bf0636e30c",
                     quality: 4, speed: 4, blurb: "Large-model accuracy at mid-size speed (it only wakes part of itself per word).", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-31b", name: "Gemma 4 31B", repo: "unsloth/gemma-4-31B-it-GGUF",
                     file: "gemma-4-31B-it-UD-Q4_K_XL.gguf", bytes: 18_822_970_304,
                     sha256: "9e92cb6236044c6a9870af406029c74a76e0571c157a6f95df724dcc8c7a1575",
                     quality: 5, speed: 2, blurb: "The most accurate reader here, best for tangled ensemble scenes. Slow.", license: "Apache 2.0"),
    ]

    public static func model(id: String) -> CatalogModel? { all.first { $0.id == id } }

    /// Models no longer offered (the Qwen 3 family, removed 2026-11-13). A saved choice of
    /// one of these is cleared so the narrator picks again; the file stays until removed.
    public static let retiredIDs: Set<String> = ["qwen3-4b", "qwen3-8b", "qwen3-14b", "qwen3-30b-a3b"]

    /// The model to suggest for this much unified memory.
    public static func recommendedID(memoryGB: Int) -> String {
        switch memoryGB {
        case ..<12: "gemma-4-e2b"
        case ..<24: "gemma-4-e4b"
        case ..<40: "phi-4"
        case ..<56: "gemma-4-26b-a4b"
        default: "gemma-4-31b"
        }
    }

    /// Why a model is a good or bad choice for a Mac with this much memory. The GPU can use
    /// about two thirds of unified memory before macOS starts swapping, and the narrator
    /// usually has a DAW, a browser and the manuscript open too.
    public static func advice(for model: CatalogModel, memoryGB: Int) -> ModelAdvice {
        let mem = Double(memoryGB)
        let usable = mem * 0.66
        let need = model.workingGB
        let size = String(format: "%.0f", need.rounded(.up))
        if model.id == recommendedID(memoryGB: memoryGB) {
            return ModelAdvice(fit: .recommended, note: "Uses about \(size) GB of your \(memoryGB) GB, leaving room for your recording software. The best balance of accuracy and speed for this Mac.")
        }
        if need > usable {
            return ModelAdvice(fit: .tooLarge, note: "Needs about \(size) GB, more than this Mac can give it (\(memoryGB) GB in all). macOS would swap to disk, and tagging a book could take many hours or fail.")
        }
        if need > mem * 0.45 {
            return ModelAdvice(fit: .tight, note: "Fits, using about \(size) GB of your \(memoryGB) GB. Quit other large apps while it tags; your Mac may feel slow until it finishes.")
        }
        let best = all.filter { $0.workingGB <= mem * 0.45 }.map(\.quality).max() ?? model.quality
        if model.quality < best {
            return ModelAdvice(fit: .good, note: "Uses only about \(size) GB, so it's quick and light, but this Mac can run a more accurate model. Expect more red lines to check.")
        }
        return ModelAdvice(fit: .good, note: "Fits comfortably in about \(size) GB of your \(memoryGB) GB.")
    }

    /// Rough minutes to tag a book of this many words, for the progress text.
    public static func estimatedMinutes(words: Int, model: CatalogModel) -> Int {
        let wordsPerMinute: Double = [1: 2500, 2: 5000, 3: 9000, 4: 14000, 5: 20000][model.speed] ?? 8000
        return max(1, Int((Double(words) / wordsPerMinute).rounded(.up)))
    }
}
