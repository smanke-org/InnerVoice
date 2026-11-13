import Foundation
import Synchronization

/// How a line is said. Speech engines that can act (Pocket TTS, Orpheus, ElevenLabs) use it;
/// Apple's voices only change speed, pitch and loudness a little.
public enum Tone: String, Codable, CaseIterable, Identifiable, Sendable {
    case neutral, excited, confused, sad, angry, whispered, scared, tender, sarcastic

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .neutral: "Neutral"
        case .excited: "Excited"
        case .confused: "Confused"
        case .sad: "Sad"
        case .angry: "Angry"
        case .whispered: "Whispered"
        case .scared: "Scared"
        case .tender: "Tender"
        case .sarcastic: "Sarcastic"
        }
    }

    public var symbol: String {
        switch self {
        case .neutral: "minus"
        case .excited: "exclamationmark"
        case .confused: "questionmark"
        case .sad: "cloud.rain"
        case .angry: "flame"
        case .whispered: "speaker.wave.1"
        case .scared: "bolt.heart"
        case .tender: "heart"
        case .sarcastic: "theatermasks"
        }
    }

    /// 1…9: the key that picks the tone in Review.
    public var key: Int { (Self.allCases.firstIndex(of: self) ?? 0) + 1 }

    /// ElevenLabs' audio tag for the tone, put before the line ("[whispers] Not here.").
    public var audioTag: String? {
        switch self {
        case .neutral: nil
        case .excited: "[excited]"
        case .confused: "[confused]"
        case .sad: "[sad]"
        case .angry: "[angry]"
        case .whispered: "[whispers]"
        case .scared: "[scared]"
        case .tender: "[tenderly]"
        case .sarcastic: "[sarcastic]"
        }
    }

    /// How a speech engine that takes directions is told to say it.
    public var direction: String {
        switch self {
        case .neutral: "in a natural, even tone"
        case .excited: "excited and full of energy"
        case .confused: "confused and unsure, puzzling it out"
        case .sad: "sad and subdued, close to tears"
        case .angry: "angry and sharp"
        case .whispered: "in a quiet whisper"
        case .scared: "frightened, voice shaking"
        case .tender: "tender and warm, speaking gently"
        case .sarcastic: "dry and sarcastic"
        }
    }
}

/// A sound a speech tag asks for ("she sighed"). Orpheus makes these; other engines leave
/// them out.
public enum VocalSound: String, Codable, CaseIterable, Sendable {
    case laugh, chuckle, sigh, gasp, groan, sniffle, yawn, cough

    /// ElevenLabs' audio tag for the sound ("[sighs]").
    public var audioTag: String { "[\(rawValue)s]" }
}

/// Who decided a line's tone. The narrator's choice is never changed by the rules or the AI.
public enum ToneSource: String, Codable, Sendable {
    case rule, ai, user
}

/// A text-to-speech engine. All but ElevenLabs run on the Mac.
///
/// Projects made before Pocket TTS may say "qwen" (Qwen3-TTS, retired 2026-11-13): it reads as
/// `.pocket`, and `LegacyVoices.migrate` turns that engine's voices into Pocket TTS ones
/// when the project is read. Any other engine this version doesn't know (a project saved by a
/// newer InnerVoice) reads as `.mixed`, which names no voices: those characters fall back to
/// the narrator's voice instead of the whole project failing to open.
public enum VoiceEngine: String, Codable, CaseIterable, Identifiable, Sendable {
    case apple, pocket, orpheus
    /// ElevenLabs, in the cloud: the only engine that sends text off the Mac. Off until the
    /// narrator sets it up, and each book must allow it.
    case elevenlabs
    /// Not an engine itself: each character can have a voice from any engine that's on.
    /// A voice's own `VoiceChoice.engine` is always a real one.
    case mixed

    public var id: String { rawValue }

    /// The raw value older projects use for Qwen3-TTS, the engine Pocket TTS replaced.
    public static let retiredQwen = "qwen"
    /// A test build briefly saved its on-device engine as "chatterbox" (never released): it
    /// reads as Pocket TTS too.
    static let unreleasedTestEngine = "chatterbox"

    public init(from decoder: Decoder) throws {
        let raw = try decoder.singleValueContainer().decode(String.self)
        if let known = VoiceEngine(rawValue: raw) {
            self = known
        } else {
            self = raw == Self.retiredQwen || raw == Self.unreleasedTestEngine ? .pocket : .mixed
        }
    }

    /// The engines that make sound (everything but `.mixed`).
    public static let engines: [VoiceEngine] = [.apple, .pocket, .orpheus, .elevenlabs]

    /// Sends the book's text to a company's servers to make the audio.
    public var isCloud: Bool { self == .elevenlabs }

    public var title: String {
        switch self {
        case .apple: "Apple Voices"
        case .pocket: "Pocket TTS"
        case .orpheus: "Orpheus"
        case .elevenlabs: "ElevenLabs"
        case .mixed: "Mix Engines"
        }
    }

    /// Whether the engine acts out a line's tone (Apple's voices can't).
    public var actsTones: Bool { self != .apple }
}

/// A voice on one engine, with the narrator's adjustments.
public struct VoiceChoice: Codable, Hashable, Sendable {
    public var engine: VoiceEngine
    /// Apple: the voice identifier. Orpheus: the voice name. Pocket TTS: "stock:<name>" or
    /// "clone:<id>". ElevenLabs: its voice ID.
    public var voiceID: String
    /// A description of the voice ("A woman in her sixties…"). Written by Qwen3-TTS (retired)
    /// and kept in the file format; no engine speaks from it now.
    public var design: String?
    /// Speed and pitch, 1 is the voice's own.
    public var rate: Double?
    public var pitch: Double?

    /// "Same as Narrator": the character speaks in whatever voice the narrator has.
    public static let narratorID = "narrator"
    public var isNarrator: Bool { voiceID == Self.narratorID }

    public init(engine: VoiceEngine, voiceID: String, design: String? = nil, rate: Double? = nil, pitch: Double? = nil) {
        self.engine = engine
        self.voiceID = voiceID
        self.design = design
        self.rate = rate
        self.pitch = pitch
    }
}

public enum VoiceSource: String, Codable, Sendable {
    /// Picked by casting from the character's summary.
    case auto
    /// Chosen or confirmed by the narrator; casting never changes it.
    case user
}

/// A character's (or the narrator's) voice on one engine.
public struct CastVoice: Codable, Hashable, Sendable {
    public var choice: VoiceChoice
    public var confidence: Double
    public var source: VoiceSource

    public init(choice: VoiceChoice, confidence: Double, source: VoiceSource) {
        self.choice = choice
        self.confidence = confidence
        self.source = source
    }
}

public enum VoiceGender: String, Codable, CaseIterable, Sendable {
    case female, male, neutral

    public var title: String {
        switch self {
        case .female: "Female"
        case .male: "Male"
        case .neutral: "Neutral"
        }
    }
}

public enum VoiceAge: String, Codable, CaseIterable, Comparable, Sendable {
    case child, young, adult, older

    public var title: String {
        switch self {
        case .child: "Child"
        case .young: "Young"
        case .adult: "Adult"
        case .older: "Older"
        }
    }

    var rank: Int { Self.allCases.firstIndex(of: self)! }
    public static func < (a: VoiceAge, b: VoiceAge) -> Bool { a.rank < b.rank }
}

/// One voice an engine offers.
public struct VoiceInfo: Hashable, Identifiable, Sendable {
    public var engine: VoiceEngine
    /// What `VoiceChoice.voiceID` stores.
    public var voiceID: String
    public var name: String
    public var gender: VoiceGender
    public var age: VoiceAge
    /// "American", "British", "Scottish"…
    public var accent: String
    /// Higher sounds better: Apple's are 0 classic, 1 standard, 2 enhanced, 3 premium.
    public var quality: Int
    /// A short description for the voice list ("Bright, conversational").
    public var detail: String
    /// A description of the voice, when there is one (kept in the file format).
    public var design: String?

    public var id: String { engine.rawValue + ":" + voiceID }

    /// What choosing this voice stores.
    public var choice: VoiceChoice { VoiceChoice(engine: engine, voiceID: voiceID, design: design) }

    public init(engine: VoiceEngine, voiceID: String, name: String, gender: VoiceGender, age: VoiceAge = .adult,
                accent: String, quality: Int = 0, detail: String = "", design: String? = nil) {
        self.engine = engine
        self.voiceID = voiceID
        self.name = name
        self.gender = gender
        self.age = age
        self.accent = accent
        self.quality = quality
        self.detail = detail
        self.design = design
    }

    /// Orpheus's eight voices (its model card's names; all young adult, American).
    public static let orpheus: [VoiceInfo] = [
        ("tara", VoiceGender.female, "Warm, conversational"), ("leah", .female, "Bright, friendly"),
        ("jess", .female, "Lively, expressive"), ("mia", .female, "Soft, light"), ("zoe", .female, "Clear, youthful"),
        ("leo", .male, "Steady, relaxed"), ("dan", .male, "Deep, easygoing"), ("zac", .male, "Energetic, youthful"),
    ].map { VoiceInfo(engine: .orpheus, voiceID: $0.0, name: $0.0.capitalized, gender: $0.1, age: .young, accent: "American", quality: 2, detail: $0.2) }

    /// Pocket TTS's stock voices. Pocket TTS can't design a voice from a description: it
    /// clones one from a few seconds of speech. The clips ship with the app
    /// (Resources/StockVoices): Orpheus's eight voices (Apache 2.0) said one plain passage,
    /// and Alba is Pocket TTS's own default voice (Alba MacKenna, CC BY 4.0, from Kyutai's
    /// voice collection; credited in About and Help). Orpheus's are young adult and
    /// American: casting shifts a clip's pitch for a child or an older character, as it fakes
    /// age with pitch for Apple's voices.
    public static let pocket: [VoiceInfo] = [
        ("alba", "Alba", VoiceGender.female, VoiceAge.adult, 3, "The engine’s own voice; natural, easygoing"),
        ("tara", "Tara", .female, .young, 2, "Warm, conversational"), ("leah", "Leah", .female, .young, 2, "Bright, friendly"),
        ("jess", "Jess", .female, .young, 2, "Lively, expressive"), ("mia", "Mia", .female, .young, 2, "Soft, light"),
        ("zoe", "Zoe", .female, .young, 2, "Clear, youthful"),
        ("leo", "Leo", .male, .young, 2, "Steady, relaxed"), ("dan", "Dan", .male, .young, 2, "Deep, easygoing"),
        ("zac", "Zac", .male, .young, 2, "Energetic, youthful"),
    ].map { VoiceInfo(engine: .pocket, voiceID: PocketVoice.stockPrefix + $0.0, name: $0.1, gender: $0.2, age: $0.3,
                      accent: "American", quality: $0.4, detail: $0.5) }
}

/// What the book says a character should sound like, read from the summary and the lines.
public struct CastingProfile: Hashable, Sendable {
    public var gender: VoiceGender?
    /// The gender rests on a single pronoun.
    public var genderGuessed = false
    public var age: VoiceAge?
    public var accent: String?
    /// Words about the voice itself: "deep", "gravelly", "soft"…
    public var timbre: [String] = []
    /// From the voice brief: -1 low or slow, 0 neither, 1 high or quick.
    public var pitchLean = 0
    public var paceLean = 0
    public var lines = 0
    /// Where each piece came from, for the Voices page ("Gender: the summary").
    public var evidence: [String] = []

    /// How much casting has to go on: 0 nothing, up to 1.
    public var completeness: Double {
        var n = 0.0
        if gender != nil { n += 0.5 }
        if age != nil { n += 0.25 }
        if accent != nil || !timbre.isEmpty { n += 0.25 }
        return n
    }
}

/// Picks a voice for each character from what the book says about them: gender, age and
/// accent from the summary (or, failing that, titles and "she said" tags), main characters
/// first so they get the most fitting and most distinct voices.
public enum VoiceCasting {
    static let femaleWords = ["woman", "female", "girl", "lady", "mother", "daughter", "wife", "sister", "queen", "princess",
                              "aunt", "grandmother", "granny", "widow", "mrs", "miss", "ms", "madam", "she", "her", "mum", "mom",
                              "niece", "duchess", "countess", "nun", "actress", "waitress", "maid", "matron", "dame"]
    static let maleWords = ["man", "male", "boy", "gentleman", "father", "son", "husband", "brother", "king", "prince", "uncle",
                            "grandfather", "grandpa", "widower", "mr", "sir", "lord", "he", "his", "him", "dad", "nephew",
                            "duke", "earl", "monk", "priest", "waiter", "lad", "fellow", "chap", "da"]
    static let femaleTitles: Set<String> = ["Mrs.", "Mrs", "Miss", "Ms.", "Ms", "Lady", "Dame", "Aunt", "Queen", "Princess", "Madam",
                                            "Madame", "Señora", "Mother", "Sister", "Granny", "Grandma"]
    static let maleTitles: Set<String> = ["Mr.", "Mr", "Mister", "Sir", "Lord", "Uncle", "King", "Prince", "Señor", "Father",
                                          "Brother", "Grandpa"]
    static let accents: [(String, [String])] = [
        ("Scottish", ["scottish", "scots", "glaswegian", "highland", "edinburgh", "glasgow", "aberdeen", "brogue"]),
        ("Irish", ["irish", "dublin", "belfast", "cork"]),
        ("Welsh", ["welsh", "cardiff"]),
        ("British", ["british", "english", "london", "cockney", "yorkshire", "cornish", "cornwall", "oxford", "posh",
                     "received pronunciation", "liverpool", "scouse", "manchester", "devon"]),
        ("American", ["american", "southern", "texas", "texan", "boston", "new york", "brooklyn", "midwest", "california", "drawl"]),
        ("Australian", ["australian", "aussie", "sydney", "melbourne"]),
        ("New Zealand", ["new zealand", "kiwi"]),
        ("Canadian", ["canadian", "toronto"]),
        ("Indian", ["indian", "delhi", "mumbai", "bombay"]),
        ("South African", ["south african", "johannesburg", "cape town"]),
        ("French", ["french", "paris", "parisian"]),
        ("German", ["german", "berlin", "bavarian"]),
        ("Italian", ["italian", "rome", "naples", "sicilian"]),
        ("Spanish", ["spanish", "madrid", "castilian"]),
        ("Russian", ["russian", "moscow"]),
    ]
    static let timbreWords = ["deep", "low", "gravelly", "rough", "husky", "hoarse", "raspy", "gruff", "soft", "high", "shrill",
                              "booming", "warm", "bright", "thin", "smooth", "quiet", "loud", "lilting", "melodic", "nasal",
                              "breathy", "rich", "reedy", "silky", "throaty", "sing-song", "clipped", "measured"]

    static func words(_ s: String) -> [String] {
        s.lowercased().components(separatedBy: CharacterSet.letters.inverted).filter { !$0.isEmpty }
    }

    // MARK: Profile

    public static func profile(_ character: BookCharacter, project: Project, index: BookIndex) -> CastingProfile {
        var p = CastingProfile()
        p.lines = index.stats[character.id]?.lines ?? 0
        let facts = character.facts
        func factText(_ kinds: [FactKind]) -> String { facts.filter { kinds.contains($0.kind) }.map(\.text).joined(separator: " ") }

        // Gender: the narrator's choice, then the summary, then a title, then "she said"
        // after their lines, then other facts (relationships often name other people, so
        // they come last).
        if let g = character.gender {
            p.gender = g.voiceGender
            p.evidence.append("Gender: you chose")
        } else if let g = gender(in: factText([.gender])) {
            p.gender = g
            p.evidence.append("Gender: the summary")
        } else if let first = character.name.split(separator: " ").first.map(String.init),
                  femaleTitles.contains(first) || maleTitles.contains(first) {
            p.gender = femaleTitles.contains(first) ? .female : .male
            p.evidence.append("Gender: “\(first)”")
        } else if let g = pronounGender(character.id, project: project, index: index) {
            p.gender = g.gender
            p.genderGuessed = !g.sure
            p.evidence.append(g.sure ? "Gender: “\(g.gender == .female ? "she" : "he")” in the book" : "Gender: a guess from one “\(g.gender == .female ? "her" : "his")”")
        } else if let g = gender(in: character.voiceNotes) {
            p.gender = g
            p.evidence.append("Gender: your voice notes")
        } else if let g = gender(in: factText([.relationships, .appearance, .age, .history, .other])) {
            p.gender = g
            p.evidence.append("Gender: the summary")
        }

        // Age: a number, or words like "elderly" or "teenage".
        if let a = age(in: factText([.age]), strict: false) ?? age(in: factText([.appearance]), strict: true) {
            p.age = a
            p.evidence.append("Age: the summary")
        } else if let a = age(in: character.voiceNotes, strict: true) {
            p.age = a
            p.evidence.append("Age: your voice notes")
        }

        let voiceText = (factText([.accent, .voice]) + " " + character.voiceNotes).lowercased()
        if let a = accents.first(where: { $0.1.contains { voiceText.contains($0) } })?.0 {
            p.accent = a
            p.evidence.append("Accent: \(a)")
        }
        let w = Set(words(factText([.voice]) + " " + character.voiceNotes))
        p.timbre = timbreWords.filter { w.contains($0) }
        // The voice brief's pitch and pace (the AI's, or the narrator's edit of it).
        let brief = VoiceBrief.read(from: character.voiceNotes)
        p.pitchLean = lean(brief.pitch, low: ["low", "deep", "bass", "baritone", "lower"], high: ["high", "bright", "light", "higher", "squeaky", "thin"])
        p.paceLean = lean(brief.pace, low: ["slow", "deliberate", "measured", "unhurried", "drawling", "slower"], high: ["quick", "fast", "brisk", "rapid", "clipped", "hurried", "faster"])
        if p.pitchLean != 0 || p.paceLean != 0 { p.evidence.append("Pitch and pace: the voice brief") }
        return p
    }

    static func lean(_ text: String?, low: [String], high: [String]) -> Int {
        guard let text else { return 0 }
        let w = Set(words(text))
        let l = low.filter { w.contains($0) }.count, h = high.filter { w.contains($0) }.count
        return l > h ? -1 : h > l ? 1 : 0
    }

    /// "Mrs. Penrose" is a woman and "Sir Alec" a man, before the book says anything else.
    public static func titleGender(_ name: String) -> VoiceGender? {
        guard let first = name.split(separator: " ").first.map(String.init) else { return nil }
        if femaleTitles.contains(first) { return .female }
        if maleTitles.contains(first) { return .male }
        return nil
    }

    /// The gender a voice description speaks of ("A woman in her forties…"), for a designed
    /// voice with no catalog entry.
    public static func gender(ofDescription text: String) -> VoiceGender? { gender(in: text) }

    static func gender(in text: String) -> VoiceGender? {
        let w = words(text)
        guard !w.isEmpty else { return nil }
        let f = w.filter { femaleWords.contains($0) }.count
        let m = w.filter { maleWords.contains($0) }.count
        if f > m { return .female }
        if m > f { return .male }
        return nil
    }

    /// `strict` skips words like "boy" and "little" that describe someone else as often as
    /// the character (outside the age fact itself).
    static func age(in text: String, strict: Bool = false) -> VoiceAge? {
        let lower = text.lowercased()
        guard !lower.isEmpty else { return nil }
        // "About 60", "sixty-two", "in her forties".
        if let n = lower.range(of: #"\b(\d{1,3})\b"#, options: .regularExpression).flatMap({ Int(lower[$0]) }), n > 0, n < 120 {
            return n < 13 ? .child : n < 25 ? .young : n < 55 ? .adult : .older
        }
        let decades: [(String, VoiceAge)] = [("teen", .young), ("twent", .young), ("thirt", .adult), ("fort", .adult),
                                             ("fift", .older), ("sixt", .older), ("sevent", .older), ("eight", .older), ("ninet", .older)]
        let w = words(lower)
        for word in w {
            if !strict, ["child", "kid", "little", "toddler", "baby", "infant", "boy", "girl", "small"].contains(word), !w.contains("old") { return .child }
            if ["elderly", "aged", "grey", "gray", "white-haired", "grandmother", "grandfather", "granny", "grandpa", "ancient", "retired", "widow", "widower"].contains(word) { return .older }
            if ["teenage", "teenager", "adolescent", "youth", "young", "student", "lad", "lass"].contains(word) { return .young }
            if ["middle-aged", "middle"].contains(word) { return .adult }
            if word == "old" { return .older }
            // "in her sixties", "forty", "seventy-two".
            if let d = decades.first(where: { word.hasPrefix($0.0) && (word.hasSuffix("ies") || word.hasSuffix("s") || (word.hasSuffix("y") && word.count <= 7)) }) { return d.1 }
        }
        return nil
    }

    /// "she said" or "he asked" right after the character's lines (worth two), and the
    /// first "she", "her", "he" or "his" after the character's name in a sentence that
    /// names nobody else in the cast ("Doctor Edda Voss arrived with her bag").
    /// Pronoun results by what they depend on (the book's text, the character's lines and
    /// everyone's names), so redrawing a page while the narrator types costs nothing: the
    /// scan reads the whole manuscript.
    private static let pronounCache = Mutex<[Int: PronounResult]>([:])
    private struct PronounResult: Sendable { var gender: VoiceGender; var sure: Bool }

    static func pronounGender(_ id: UUID, project: Project, index: BookIndex) -> (gender: VoiceGender, sure: Bool)? {
        var h = Hasher()
        h.combine(id)
        for c in project.characters { h.combine(c.id); h.combine(c.allNames) }
        for line in project.lines where line.speakerID == id { h.combine(line.paragraphID); h.combine(line.range.location); h.combine(line.range.length) }
        for chapter in project.chapters { h.combine(chapter.paragraphs.count); for p in chapter.paragraphs { h.combine(p.text.utf8.count) } }
        let key = h.finalize()
        if let hit = pronounCache.withLock({ $0[key] }) { return hit.sure || hit.gender != .neutral ? (hit.gender, hit.sure) : nil }
        let result = scanPronouns(id, project: project, index: index)
        pronounCache.withLock { cache in
            if cache.count > 2000 { cache.removeAll() }
            // "Nothing found" is kept as neutral, not sure.
            cache[key] = result.map { PronounResult(gender: $0.gender, sure: $0.sure) } ?? PronounResult(gender: .neutral, sure: false)
        }
        return result
    }

    private static func scanPronouns(_ id: UUID, project: Project, index: BookIndex) -> (gender: VoiceGender, sure: Bool)? {
        var she = 0, he = 0
        for line in project.lines where line.speakerID == id {
            guard let p = project.paragraph(line.paragraphID, index: index) else { continue }
            let ns = p.text as NSString
            let start = line.range.end
            guard start < ns.length else { continue }
            let after = ns.substring(with: NSRange(location: start, length: min(30, ns.length - start))).lowercased()
            if after.range(of: #"^[\s,.!?…—–”"’']*she\s+[a-z]+"#, options: .regularExpression) != nil { she += 2 }
            if after.range(of: #"^[\s,.!?…—–”"’']*he\s+[a-z]+"#, options: .regularExpression) != nil { he += 2 }
        }
        guard let me = project.character(id) else { return nil }
        let mine = me.allNames
        let others = project.characters.filter { $0.id != id }.flatMap(\.allNames).filter { o in !mine.contains(o) }
        for chapter in project.chapters {
            for p in chapter.paragraphs where mine.contains(where: { RuleAttribution.containsWord(p.text, $0) }) {
                for sentence in p.text.components(separatedBy: CharacterSet(charactersIn: ".!?")) {
                    guard let name = mine.first(where: { RuleAttribution.containsWord(sentence, $0) }),
                          !others.contains(where: { RuleAttribution.containsWord(sentence, $0) }),
                          let r = sentence.range(of: name) else { continue }
                    let rest = words(String(sentence[r.upperBound...]))
                    guard let first = rest.first(where: { ["she", "her", "hers", "herself", "he", "his", "him", "himself"].contains($0) }) else { continue }
                    if first.hasPrefix("h") && first != "her" && first != "hers" && first != "herself" { he += 1 } else { she += 1 }
                }
            }
        }
        if she >= 2, she > he * 2 { return (.female, true) }
        if he >= 2, he > she * 2 { return (.male, true) }
        // One pronoun and nothing against it: a guess.
        if she == 1, he == 0 { return (.female, false) }
        if he == 1, she == 0 { return (.male, false) }
        return nil
    }

    // MARK: Matching

    /// How well a voice fits a profile; below 0 it doesn't fit at all (wrong gender).
    public static func score(_ v: VoiceInfo, _ p: CastingProfile) -> Double {
        var s = 0.12 * Double(v.quality)
        if let g = p.gender {
            if v.gender == g { s += 0.5 } else if v.gender == .neutral { s += 0.15 } else { return -1 }
        }
        if let a = p.age {
            let gap = abs(a.rank - v.age.rank)
            s += gap == 0 ? 0.2 : gap == 1 ? 0.08 : -0.1
        }
        if let accent = p.accent {
            if v.accent == accent { s += 0.25 } else if accent == "Scottish" || accent == "Irish" || accent == "Welsh", v.accent == "British" { s += 0.1 }
        }
        return s
    }

    /// A description of the voice the profile asks for: ElevenLabs' Voice Design starts from it.
    public static func describe(_ p: CastingProfile, name: String) -> String {
        let who: String
        switch (p.gender, p.age) {
        case (.female?, .child?): who = "A young girl"
        case (.male?, .child?): who = "A young boy"
        case (.female?, .young?): who = "A young woman"
        case (.male?, .young?): who = "A young man"
        case (.female?, .older?): who = "An older woman"
        case (.male?, .older?): who = "An older man"
        case (.female?, _): who = "A woman"
        case (.male?, _): who = "A man"
        case (_, .child?): who = "A child"
        case (_, .older?): who = "An older adult"
        default: who = "An adult"
        }
        var parts = [who]
        // Something about the voice itself, so the designed voice isn't a thin one.
        let pitch = p.pitchLean < 0 ? "low-pitched" : p.pitchLean > 0 ? "high-pitched" : nil
        // Timbre words that only repeat the pitch ("low" after "low-pitched") are left out.
        let timbre = p.timbre.filter { pitch == nil || !["low", "deep", "high", "higher", "lower"].contains($0.lowercased()) }
        let quality = [pitch].compactMap { $0 } + (timbre.isEmpty && pitch == nil ? ["clear, natural"] : Array(timbre.prefix(3)))
        parts.append("with a \(quality.joined(separator: ", ")) voice")
        if p.paceLean != 0 { parts.append(p.paceLean < 0 ? "speaking slowly" : "speaking quickly") }
        if let a = p.accent { parts.append("speaking English with \("AEIOU".contains(a.prefix(1)) ? "an" : "a") \(a) accent") }
        return parts.joined(separator: " ") + "."
    }

    /// How sure casting is of a voice: how much the book says, less when the voice is
    /// shared with someone in the same chapters.
    static func confidence(_ p: CastingProfile, sharedOverlap: Double) -> Double {
        let base = p.gender == nil ? 0.35 : p.genderGuessed ? 0.5 : 0.6 + 0.3 * (p.completeness - 0.5) / 0.5
        return max(0.2, min(0.95, base - 0.35 * sharedOverlap))
    }

    /// The best voices for one character, best first, with a fit score.
    public static func suggestions(for character: BookCharacter, project: Project, index: BookIndex,
                                   voices: [VoiceInfo], limit: Int = 3) -> [(voice: VoiceInfo, score: Double)] {
        let p = profile(character, project: project, index: index)
        let used = usedVoices(project: project, engine: voices.first?.engine ?? project.settings.engine, except: character.id)
        let myChapters = index.stats[character.id]?.chapters ?? []
        return voices.map { v -> (VoiceInfo, Double) in
            var s = score(v, p)
            if s >= 0, let others = used[v.voiceID] {
                s -= 0.4 * overlap(myChapters, others.map { index.stats[$0]?.chapters ?? [] })
            }
            return (v, s)
        }
        .filter { $0.1 >= 0 }
        .sorted { $0.1 > $1.1 }
        .prefix(limit)
        .map { (voice: $0.0, score: $0.1) }
    }

    static func usedVoices(project: Project, engine: VoiceEngine, except: UUID? = nil) -> [String: [UUID]] {
        var used: [String: [UUID]] = [:]
        for c in project.characters where c.id != except {
            if let v = c.voice(for: engine) { used[v.choice.voiceID, default: []].append(c.id) }
        }
        return used
    }

    /// The share of my chapters someone else with this voice also speaks in, 0…1.
    static func overlap(_ mine: Set<Int>, _ others: [Set<Int>]) -> Double {
        guard !mine.isEmpty else { return others.isEmpty ? 0 : 0.5 }
        let theirs = others.reduce(into: Set<Int>()) { $0.formUnion($1) }
        return Double(mine.intersection(theirs).count) / Double(mine.count)
    }

    /// Casts every character with lines who has no voice the narrator chose, and the
    /// narrator if they have none. Main characters choose first. Characters in `keep` keep
    /// the voice they have, like the narrator's choices; `keepExisting` keeps every voice
    /// there is (the narrator's and the roles' too) and only fills what's missing. Returns how
    /// many characters got a voice.
    @discardableResult
    public static func cast(_ project: inout Project, index: BookIndex, engine: VoiceEngine, voices: [VoiceInfo],
                            keep: Set<UUID> = [], keepExisting: Bool = false) -> Int {
        let keeping = keepExisting || !keep.isEmpty
        func kept(_ c: BookCharacter) -> Bool {
            guard let v = c.voice(for: engine) else { return false }
            return v.source == .user || keepExisting || keep.contains(c.id)
        }
        let speaking = project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
        // Start from the voices the narrator chose; every automatic one is decided again.
        var taken: [String: [Set<Int>]] = [:]
        for c in project.characters where kept(c) {
            if let v = c.voice(for: engine) { taken[v.choice.voiceID, default: []].append(index.stats[c.id]?.chapters ?? []) }
        }
        // The narrator first: heard most of all, and nobody should share their voice.
        var narratorID: String?
        if let n = project.settings.narratorVoice(for: engine), n.source == .user || keeping {
            narratorID = n.choice.voiceID
        } else if let best = voices.sorted(by: { ($0.quality, $0.gender == .neutral ? 1 : 0) > ($1.quality, $1.gender == .neutral ? 1 : 0) }).first {
            narratorID = best.voiceID
            project.settings.setNarratorVoice(CastVoice(choice: best.choice, confidence: 0.9, source: .auto), for: engine)
        }

        var count = 0
        for c in speaking {
            guard let i = project.characterIndex(c.id) else { continue }
            if kept(project.characters[i]) { continue }
            let p = profile(project.characters[i], project: project, index: index)
            let chapters = index.stats[c.id]?.chapters ?? []
            var best: (VoiceInfo, Double, Double)?
            for v in voices {
                var s = score(v, p)
                guard s >= 0 else { continue }
                let shared = taken[v.voiceID].map { overlap(chapters, $0) } ?? 0
                s -= 0.45 * shared + (v.voiceID == narratorID ? 0.5 : 0) + 0.05 * Double(taken[v.voiceID]?.count ?? 0)
                if best == nil || s > best!.1 { best = (v, s, shared) }
            }
            guard let (voice, _, shared) = best else {
                project.characters[i].setVoice(nil, for: engine)
                continue
            }
            var choice = ageAdjusted(voice, for: p.age)
            // Apple and Pocket TTS voices can't take a description: the brief's pitch and pace
            // nudge them (for Pocket TTS, about a semitone and 6% a step).
            if voice.engine == .apple || voice.engine == .pocket, p.pitchLean != 0 || p.paceLean != 0 {
                choice.pitch = ((choice.pitch ?? 1) * (1 + 0.06 * Double(p.pitchLean)) * 100).rounded() / 100
                choice.rate = ((choice.rate ?? 1) * (1 + 0.06 * Double(p.paceLean)) * 100).rounded() / 100
            }
            let sharedNarrator = voice.voiceID == narratorID ? 0.5 : 0
            project.characters[i].setVoice(CastVoice(choice: choice, confidence: confidence(p, sharedOverlap: max(shared, sharedNarrator)), source: .auto), for: engine)
            taken[voice.voiceID, default: []].append(chapters)
            count += 1
        }
        castRoles(&project, engine: engine, voices: voices, keepAutomatic: keeping)
        project.voicesCast = .now
        return count
    }

    /// A voice made to sound the character's age when it doesn't. Apple's voices are all
    /// adults: a child sounds higher, an older voice lower and slower. Pocket TTS's stock
    /// voices are young adults: its pitch is a shift of the clip it clones from, so the steps
    /// stay within what keeps the voice (docs/CASTING-STYLE.md §6: ±2 semitones kept it, −4 began
    /// to lose it): a child +3 semitones (1.19) and a little quicker, an older voice −2 (0.89)
    /// and slower. Young and adult characters keep the voice as it is.
    public static func ageAdjusted(_ voice: VoiceInfo, for age: VoiceAge?) -> VoiceChoice {
        var choice = voice.choice
        guard let a = age, a != voice.age else { return choice }
        switch voice.engine {
        case .apple:
            switch a {
            case .child: choice.pitch = 1.3; choice.rate = 1.05
            case .young: choice.pitch = 1.08
            case .older: choice.pitch = 0.88; choice.rate = 0.92
            case .adult: break
            }
        case .pocket where !voice.voiceID.hasPrefix(PocketVoice.clonePrefix):
            switch a {
            case .child: choice.pitch = 1.19; choice.rate = 1.05
            case .older: choice.pitch = 0.89; choice.rate = 0.92
            case .young, .adult: break
            }
        default: break
        }
        return choice
    }

    /// Casting Style: a voice for each shared role the style uses (Men, Women, Other) that
    /// the narrator hasn't chosen. Men get a man's voice, Women a woman's, Other a neutral
    /// one when the engine has it. Never the narrator's voice or another role's. Callers pass
    /// castable voices, so clones are never picked. `keepAutomatic` keeps voices casting
    /// already picked (casting that happens on its own fills only what's missing). Returns
    /// how many roles got a voice.
    @discardableResult
    public static func castRoles(_ project: inout Project, engine: VoiceEngine, voices: [VoiceInfo], keepAutomatic: Bool = false) -> Int {
        let roles = CastingStyleRules.sharedRolesNeeded(project.settings)
        guard !roles.isEmpty else { return 0 }
        var used = Set([project.settings.narratorVoice(for: engine)?.choice.voiceID].compactMap { $0 })
        for r in roles {
            if let v = project.settings.roleVoice(r, for: engine), v.source == .user || keepAutomatic { used.insert(v.choice.voiceID) }
        }
        var count = 0
        for r in roles {
            if let v = project.settings.roleVoice(r, for: engine), v.source == .user || keepAutomatic { continue }
            let want: VoiceGender = r == .men ? .male : r == .women ? .female : .neutral
            // Best first: the gender wanted (Other: neutral, else anyone), an adult, quality.
            func rank(_ v: VoiceInfo) -> Double {
                (v.gender == want ? 10 : 0) + (v.age == .adult ? 2 : v.age == .young ? 1 : 0) + Double(v.quality)
            }
            let pool = voices.filter { !used.contains($0.voiceID) && (r == .other || $0.gender == want) }
            guard let best = pool.max(by: { rank($0) < rank($1) }) else {
                project.settings.setRoleVoice(nil, r, for: engine)
                continue
            }
            project.settings.setRoleVoice(CastVoice(choice: best.choice, confidence: best.gender == want ? 0.9 : 0.6, source: .auto), r, for: engine)
            used.insert(best.voiceID)
            count += 1
        }
        return count
    }
}
