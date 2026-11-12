@preconcurrency import AVFoundation
import CryptoKit
import Foundation
import MLX
import MLXAudioCore
import MLXAudioTTS

// Phase 0 of docs/CASTING-STYLE.md: can Qwen3-TTS shade one narrator voice for a character
// without turning it into a different person?
//
// Same path as InnerVoice: VoiceDesign records a sample from a description, the Base model
// clones every line from that sample. Shades are (a) shade words appended to the
// narrator's description (the Voice Notes path) and (b) the narrator's sample pitch-shifted
// before cloning. Similarity = cosine of the Base model's speaker-encoder embeddings.

let modelsRoot = FileManager.default.homeDirectoryForCurrentUser
    .appending(path: "Library/Application Support/Inner Voice/Voice Models/mlx-audio")
let designDir = modelsRoot.appending(path: "mlx-community_Qwen3-TTS-12Hz-1.7B-VoiceDesign-8bit").path
let cloneDir = modelsRoot.appending(path: "mlx-community_Qwen3-TTS-12Hz-1.7B-Base-8bit").path
let out = URL(fileURLWithPath: CommandLine.arguments.count > 1 ? CommandLine.arguments[1] : "out")
try FileManager.default.createDirectory(at: out, withIntermediateDirectories: true)
let rate = 24_000.0
let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: rate, channels: 1, interleaved: false)!

func log(_ s: String) { FileHandle.standardError.write((s + "\n").data(using: .utf8)!) }
func seed(_ s: String) -> UInt64 { SHA256.hash(data: Data(s.utf8)).prefix(8).reduce(0) { $0 << 8 | UInt64($1) } }

// What InnerVoice's samples say, and the lines every voice reads.
let sampleText = "I have lived in this town for a long time, and I know every road that leads out of it."
let lines = [
    "The tide was wrong, so I waited for the second ferry.",
    "Nobody leaves this dock until I say so.",
    "If you want the truth, ask me tomorrow, when I've slept.",
    "I counted the boxes twice, and one of them is missing.",
    "Keep your voice down. The walls in this place are thin.",
    "We go tonight, before the weather turns again.",
]

// Narrators and other voices: InnerVoice's ready-made Qwen descriptions.
let storyteller = "A woman in her forties with a warm, low, unhurried voice."
let baritone = "A man in his forties with a warm, easygoing baritone voice."
let others: [(String, String)] = [
    ("other-bright-young-woman", "A young woman with a bright, clear, lively voice."),
    ("other-kind-grandmother", "An older woman with a soft, kind, slightly creaky voice."),
    ("other-gravelly-elder", "An older man with a deep, gravelly voice."),
]
// VoiceDescription.design joins notes as "<base> Voice notes: <notes>".
func shaded(_ base: String, _ notes: String) -> String { base + " Voice notes: " + notes }

enum Kind { case design(String, seedKey: String), shiftedSample(of: String, semitones: Double) }
struct Condition { var name: String; var narrator: String; var kind: Kind; var expect: String }

var conditions: [Condition] = [
    .init(name: "F-narrator", narrator: "F", kind: .design(storyteller, seedKey: storyteller), expect: "baseline"),
    .init(name: "F-narrator-reseeded", narrator: "F", kind: .design(storyteller, seedKey: storyteller + "#2"), expect: "same words, new seed"),
    .init(name: "F-man-subtle", narrator: "F", kind: .design(shaded(storyteller, "Voicing a man: the same voice, a little lower and a touch rougher."), seedKey: "s1"), expect: "lower"),
    .init(name: "F-man-clear", narrator: "F", kind: .design(shaded(storyteller, "Voicing a man: the same narrator, noticeably lower and rougher, a bit slower."), seedKey: "s2"), expect: "lower"),
    .init(name: "F-man-strong", narrator: "F", kind: .design(shaded(storyteller, "Voicing a man: the same narrator dropped deep and gravelly, slow and heavy."), seedKey: "s3"), expect: "lower"),
    .init(name: "F-young-woman-clear", narrator: "F", kind: .design(shaded(storyteller, "Voicing a young woman: the same narrator, a little higher, lighter and quicker."), seedKey: "s4"), expect: "higher"),
    .init(name: "F-sample-shift-minus2", narrator: "F", kind: .shiftedSample(of: "F-narrator", semitones: -2), expect: "lower"),
    .init(name: "F-sample-shift-minus4", narrator: "F", kind: .shiftedSample(of: "F-narrator", semitones: -4), expect: "lower"),
    .init(name: "F-sample-shift-plus2", narrator: "F", kind: .shiftedSample(of: "F-narrator", semitones: 2), expect: "higher"),
    .init(name: "M-narrator", narrator: "M", kind: .design(baritone, seedKey: baritone), expect: "baseline"),
    .init(name: "M-woman-subtle", narrator: "M", kind: .design(shaded(baritone, "Voicing a woman: the same voice, a little higher and softer."), seedKey: "m1"), expect: "higher"),
    .init(name: "M-woman-clear", narrator: "M", kind: .design(shaded(baritone, "Voicing a woman: the same narrator, noticeably higher, lighter and softer."), seedKey: "m2"), expect: "higher"),
    .init(name: "M-woman-strong", narrator: "M", kind: .design(shaded(baritone, "Voicing a woman: the same narrator, much higher and breathier, light and quick."), seedKey: "m3"), expect: "higher"),
    .init(name: "M-sample-shift-plus2", narrator: "M", kind: .shiftedSample(of: "M-narrator", semitones: 2), expect: "higher"),
]
for (n, d) in others { conditions.append(.init(name: n, narrator: "-", kind: .design(d, seedKey: d), expect: "different character")) }
// Seed test: the same shade words with different seeds (how repeatable is a design shade?).
if ProcessInfo.processInfo.environment["SPIKE_SEEDS"] != nil {
    conditions = [conditions[0]]
    for (label, notes) in [("subtle", "Voicing a man: the same voice, a little lower and a touch rougher."),
                           ("clear", "Voicing a man: the same narrator, noticeably lower and rougher, a bit slower.")] {
        for s in ["a", "b", "c"] {
            conditions.append(.init(name: "F-man-\(label)-seed-\(s)", narrator: "F", kind: .design(shaded(storyteller, notes), seedKey: label + s), expect: "lower"))
        }
    }
}
if let only = ProcessInfo.processInfo.environment["SPIKE_ONLY"] {
    let keep = Set(only.split(separator: ",").map(String.init))
    conditions = conditions.filter { keep.contains($0.name) }
}

// MARK: Audio helpers

func write(_ samples: [Float], _ name: String) {
    let b = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(samples.count))!
    b.frameLength = AVAudioFrameCount(samples.count)
    samples.withUnsafeBufferPointer { b.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
    let settings: [String: Any] = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: rate, AVNumberOfChannelsKey: 1,
                                   AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false]
    let f = try! AVAudioFile(forWriting: out.appending(path: name + ".wav"), settings: settings, commonFormat: .pcmFormatFloat32, interleaved: false)
    try! f.write(from: b)
}

/// Offline pitch shift with AVAudioUnitTimePitch (what InnerVoice already links).
func pitchShift(_ input: [Float], semitones: Double) throws -> [Float] {
    let engine = AVAudioEngine()
    let player = AVAudioPlayerNode()
    let tp = AVAudioUnitTimePitch()
    tp.pitch = Float(semitones * 100)
    engine.attach(player); engine.attach(tp)
    engine.connect(player, to: tp, format: format)
    engine.connect(tp, to: engine.mainMixerNode, format: format)
    try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
    try engine.start()
    let b = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(input.count))!
    b.frameLength = AVAudioFrameCount(input.count)
    input.withUnsafeBufferPointer { b.floatChannelData![0].update(from: $0.baseAddress!, count: input.count) }
    player.scheduleBuffer(b, completionHandler: nil)
    player.play()
    var result: [Float] = []
    let chunk = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 4096)!
    let total = input.count + 4096 * 2
    while result.count < total {
        let n = min(4096, total - result.count)
        let status = try engine.renderOffline(AVAudioFrameCount(n), to: chunk)
        guard status == .success else { break }
        result += Array(UnsafeBufferPointer(start: chunk.floatChannelData![0], count: Int(chunk.frameLength)))
    }
    engine.stop()
    // Trim the latency padding at the start (find first non-silent sample).
    if let first = result.firstIndex(where: { abs($0) > 1e-4 }) { result.removeFirst(max(0, first - 240)) }
    return result
}

/// Median F0 of voiced frames (normalized autocorrelation, 60–450 Hz).
func medianF0(_ x: [Float]) -> Double {
    let frame = 1024, hop = 480
    var f0s: [Double] = []
    var i = 0
    let minLag = Int(rate / 450), maxLag = Int(rate / 60)
    while i + frame + maxLag < x.count {
        let seg = Array(x[i ..< i + frame + maxLag])
        var energy: Float = 0
        for k in 0 ..< frame { energy += seg[k] * seg[k] }
        if energy / Float(frame) > 1e-4 {
            var best = 0.0, bestLag = 0
            for lag in minLag ... maxLag {
                var num: Float = 0, e2: Float = 0
                for k in 0 ..< frame { num += seg[k] * seg[k + lag]; e2 += seg[k + lag] * seg[k + lag] }
                let r = Double(num / sqrt(energy * e2 + 1e-9))
                if r > best { best = r; bestLag = lag }
            }
            if best > 0.75 { f0s.append(rate / Double(bestLag)) }
        }
        i += hop
    }
    guard !f0s.isEmpty else { return 0 }
    return f0s.sorted()[f0s.count / 2]
}

func level(_ s: [Float]) -> [Float] {
    let rms = sqrt(s.reduce(0) { $0 + $1 * $1 } / Float(max(1, s.count)))
    guard rms > 0 else { return s }
    let g = pow(10, -20.0 / 20) / Double(rms)
    return s.map { max(-1, min(1, $0 * Float(g))) }
}

func cosine(_ a: [Float], _ b: [Float]) -> Double {
    var d: Float = 0, na: Float = 0, nb: Float = 0
    for i in 0 ..< min(a.count, b.count) { d += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i] }
    return Double(d / (sqrt(na) * sqrt(nb) + 1e-9))
}
func mean(_ vs: [[Float]]) -> [Float] {
    var m = [Float](repeating: 0, count: vs[0].count)
    for v in vs { for i in 0 ..< m.count { m[i] += v[i] } }
    return m.map { $0 / Float(vs.count) }
}

// MARK: Run

Memory.cacheLimit = 512 * 1024 * 1024
log("loading models…")
guard let design = try await TTS.loadModel(modelRepo: designDir) as? Qwen3TTSModel,
      let clone = try await TTS.loadModel(modelRepo: cloneDir) as? Qwen3TTSModel else { fatalError("models") }

func designSample(_ description: String, seedKey: String) async throws -> [Float] {
    var p = design.defaultGenerationParameters
    p.seed = seed(seedKey); p.maxTokens = 1200
    MLXRandom.seed(seed(seedKey))
    let a = try await design.generate(text: sampleText, voice: description, refAudio: nil, refText: nil, language: "English", generationParameters: p)
    return a.asArray(Float.self)
}
func cloneLine(_ text: String, ref: [Float]) async throws -> [Float] {
    var p = clone.defaultGenerationParameters
    p.seed = seed(text); p.maxTokens = 2400
    MLXRandom.seed(seed(text))
    let a = try await clone.generate(text: text, voice: nil, refAudio: MLXArray(ref).materialized(), refText: sampleText, language: "English", generationParameters: p)
    return a.asArray(Float.self)
}
func embedding(_ audio: [Float]) -> [Float] {
    guard let e = clone.extractSpeakerEmbedding(MLXArray(audio)) else { fatalError("no speaker encoder") }
    return e.reshaped(-1).asArray(Float.self)
}

struct Result { var name: String; var narrator: String; var expect: String; var lineEmb: [[Float]]; var f0: Double; var sampleF0: Double; var seconds: Double }
var samples: [String: [Float]] = [:]
var results: [Result] = []

for c in conditions {
    let t0 = Date()
    let sample: [Float]
    switch c.kind {
    case .design(let d, let k):
        sample = level(try await designSample(d, seedKey: k))
    case .shiftedSample(let of, let st):
        guard let base = samples[of] else { log("skip \(c.name): no \(of)"); continue }
        sample = level(try pitchShift(base, semitones: st))
    }
    samples[c.name] = sample
    write(sample, c.name + "-sample")
    var embs: [[Float]] = []
    var all: [Float] = []
    for l in lines {
        let a = level(try await cloneLine(l, ref: sample))
        embs.append(embedding(a))
        all += a + [Float](repeating: 0, count: Int(rate * 0.4))
    }
    write(all, c.name)
    let r = Result(name: c.name, narrator: c.narrator, expect: c.expect, lineEmb: embs, f0: medianF0(all), sampleF0: medianF0(sample), seconds: Date().timeIntervalSince(t0))
    results.append(r)
    log(String(format: "%@ done in %.0f s, F0 %.0f Hz", c.name, r.seconds, r.f0))
    Memory.clearCache()
}

// Output-shift comparison (the Orpheus approach) on the F narrator's lines: re-render not
// needed, shift the finished lines and embed.
var report = ""
@MainActor func p(_ s: String) { report += s + "\n"; print(s) }

@MainActor func baseline(_ narrator: String) -> Result? { results.first { $0.narrator == narrator && $0.expect == "baseline" } }
p("Similarity to the narrator (speaker-encoder cosine, mean over \(lines.count) lines). F0 = median pitch.")
p("")
for n in ["F", "M"] {
    guard let b = baseline(n) else { continue }
    let ref = mean(b.lineEmb)
    // Same-voice ceiling: each baseline line vs the mean of the other baseline lines.
    let loo = b.lineEmb.indices.map { i in cosine(b.lineEmb[i], mean(b.lineEmb.enumerated().filter { $0.offset != i }.map(\.element))) }
    p("Narrator \(n) (\(n == "F" ? "Warm Storyteller" : "Easy Baritone")): F0 \(Int(b.f0)) Hz; its own lines vs each other: \(String(format: "%.3f", loo.reduce(0, +) / Double(loo.count)))")
    for r in results where r.narrator == n && r.expect != "baseline" {
        let s = r.lineEmb.map { cosine($0, ref) }.reduce(0, +) / Double(r.lineEmb.count)
        let semis = 12 * log2(r.f0 / max(1, b.f0))
        p(String(format: "  %-24@ sim %.3f   F0 %3.0f Hz (%+.1f st)   expect %@", r.name, s, r.f0, semis, r.expect))
    }
    for r in results where r.narrator == "-" {
        let s = r.lineEmb.map { cosine($0, ref) }.reduce(0, +) / Double(r.lineEmb.count)
        p(String(format: "  %-24@ sim %.3f   F0 %3.0f Hz   (a different character)", r.name, s, r.f0))
    }
    p("")
}
if let b = baseline("F"), let fr = try? Data(contentsOf: out.appending(path: "F-narrator.wav")) {
    _ = fr
    // Shift each finished baseline line by -2 st and embed (Orpheus-style output shift).
    var embs: [[Float]] = []
    var all: [Float] = []
    for l in lines {
        let a = level(try await cloneLine(l, ref: samples["F-narrator"]!))
        let s = level(try pitchShift(a, semitones: -2))
        embs.append(embedding(s)); all += s + [Float](repeating: 0, count: Int(rate * 0.4))
    }
    write(all, "F-output-shift-minus2")
    let ref = mean(b.lineEmb)
    let s = embs.map { cosine($0, ref) }.reduce(0, +) / Double(embs.count)
    p(String(format: "Output shift (F narrator lines shifted -2 st after cloning): sim %.3f, F0 %.0f Hz", s, medianF0(all)))
}
try report.write(to: out.appending(path: "results.txt"), atomically: true, encoding: .utf8)
log("wrote \(out.path)")
