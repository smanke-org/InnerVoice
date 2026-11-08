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
        CatalogModel(id: "qwen3-4b", name: "Qwen 3 4B Instruct", repo: "unsloth/Qwen3-4B-Instruct-2507-GGUF",
                     file: "Qwen3-4B-Instruct-2507-UD-Q4_K_XL.gguf", bytes: 2_546_340_960,
                     sha256: "4bbe1f2f8ebe69fad3be8e15d69f220b06448a9dd26f82d7d81cce88ebfc39fd",
                     quality: 2, speed: 5, blurb: "Small and quick, and good at following the answer format.", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-e2b", name: "Gemma 4 E2B", repo: "unsloth/gemma-4-E2B-it-GGUF",
                     file: "gemma-4-E2B-it-UD-Q4_K_XL.gguf", bytes: 3_184_496_736,
                     sha256: "b52f438017efaec5debf1c0d8be690571e212a07c312f1102bbce927258cfc32",
                     quality: 2, speed: 5, blurb: "Light and fast; reads plain dialogue well, struggles with long back-and-forths.", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-e4b", name: "Gemma 4 E4B", repo: "unsloth/gemma-4-E4B-it-GGUF",
                     file: "gemma-4-E4B-it-UD-Q4_K_XL.gguf", bytes: 5_126_306_944,
                     sha256: "3cf61de12daa015ee0f7b68e7b7c541405bf220e1e942bad8b47cab827d7df80",
                     quality: 3, speed: 4, blurb: "A strong all-rounder: follows who is speaking through most scenes.", license: "Apache 2.0"),
        CatalogModel(id: "qwen3-8b", name: "Qwen 3 8B", repo: "unsloth/Qwen3-8B-GGUF",
                     file: "Qwen3-8B-UD-Q4_K_XL.gguf", bytes: 5_135_722_304,
                     sha256: "34a514d08f7449cb4a694a707aaa2eedccb7bb68290121bf5e5a569b2abe71c3",
                     quality: 3, speed: 4, blurb: "Careful reader, good with books in other languages.", license: "Apache 2.0"),
        CatalogModel(id: "qwen3-14b", name: "Qwen 3 14B", repo: "unsloth/Qwen3-14B-GGUF",
                     file: "Qwen3-14B-UD-Q4_K_XL.gguf", bytes: 9_159_818_624,
                     sha256: "7f371adeaaf51dc97da927c61e6573e75e3ea33d76ec09dddb8cad00d0d5b6cb",
                     quality: 4, speed: 3, blurb: "Keeps track of who's who in busy scenes; builds fuller summaries.", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-26b-a4b", name: "Gemma 4 26B A4B", repo: "unsloth/gemma-4-26B-A4B-it-GGUF",
                     file: "gemma-4-26B-A4B-it-UD-Q4_K_XL.gguf", bytes: 17_010_980_576,
                     sha256: "ef728c8e0c337fd1067b947af006e38a9ef2419e56feced4fd29b4bf0636e30c",
                     quality: 4, speed: 4, blurb: "Large-model accuracy at mid-size speed (it only wakes part of itself per word).", license: "Apache 2.0"),
        CatalogModel(id: "qwen3-30b-a3b", name: "Qwen 3 30B A3B Instruct", repo: "unsloth/Qwen3-30B-A3B-Instruct-2507-GGUF",
                     file: "Qwen3-30B-A3B-Instruct-2507-UD-Q4_K_XL.gguf", bytes: 17_690_497_440,
                     sha256: "535f831bf3034a7fcfdcc8f0277a57cbad3a36c650c6068bf1d0894f25fb4383",
                     quality: 4, speed: 4, blurb: "Fast for its size and very good at the answer format.", license: "Apache 2.0"),
        CatalogModel(id: "gemma-4-31b", name: "Gemma 4 31B", repo: "unsloth/gemma-4-31B-it-GGUF",
                     file: "gemma-4-31B-it-UD-Q4_K_XL.gguf", bytes: 18_822_970_304,
                     sha256: "9e92cb6236044c6a9870af406029c74a76e0571c157a6f95df724dcc8c7a1575",
                     quality: 5, speed: 2, blurb: "The most accurate reader here, best for tangled ensemble scenes. Slow.", license: "Apache 2.0"),
    ]

    public static func model(id: String) -> CatalogModel? { all.first { $0.id == id } }

    /// The model to suggest for this much unified memory.
    public static func recommendedID(memoryGB: Int) -> String {
        switch memoryGB {
        case ..<12: "qwen3-4b"
        case ..<24: "gemma-4-e4b"
        case ..<40: "qwen3-14b"
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
