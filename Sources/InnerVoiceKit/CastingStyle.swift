import Foundation

// Casting Style (docs/CASTING-STYLE.md): how a book's voices are shared. One Voice (one
// narrator plays everyone, shading each character), Narrator + Two Voices (a voice for the
// narration, one for the men, one for the women, one for anyone else), or Full Cast (a voice
// per character). The style only decides who reads each character: every voice, Voice Notes
// and narrator assignment stays in the file, so changing styles loses nothing.

/// How voices are shared in a book.
public enum CastingStyle: String, Codable, CaseIterable, Identifiable, Sendable {
    case oneVoice, shared, fullCast

    public var id: String { rawValue }

    /// The name in each project type: AI voices share voices, Narration Tools shares people.
    public func title(for purpose: ProjectPurpose?) -> String {
        switch (self, purpose) {
        case (.oneVoice, .narration): "Just Me"
        case (.oneVoice, _): "One Voice"
        case (.shared, .narration): "Three Narrators"
        case (.shared, _): "Narrator + Two Voices"
        case (.fullCast, _): "Full Cast"
        }
    }
}

/// Who reads a character under the Casting Style.
public enum VoiceRole: String, Codable, CaseIterable, Identifiable, Sendable {
    /// The narrator's voice (shaded in One Voice).
    case narrator
    /// The voice every male character shares (Narrator + Two Voices).
    case men
    /// The voice every female character shares.
    case women
    /// The voice for characters who are neither, or whose gender isn't known.
    case other
    /// A voice of the character's own.
    case own

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .narrator: "Narrator"
        case .men: "Men"
        case .women: "Women"
        case .other: "Other"
        case .own: "Own Voice"
        }
    }

    /// The voice's name in lists and menus: "Men’s Voice", "Other Voice".
    public var voiceTitle: String {
        switch self {
        case .narrator: "Narrator’s Voice"
        case .men: "Men’s Voice"
        case .women: "Women’s Voice"
        case .other: "Other Voice"
        case .own: "Own Voice"
        }
    }

    /// The same in a caption: "Men’s voice".
    public var voiceCaption: String {
        switch self {
        case .narrator: "Narrator’s voice"
        case .men: "Men’s voice"
        case .women: "Women’s voice"
        case .other: "Other voice"
        case .own: "Own voice"
        }
    }

    /// Narration Tools: who reads under this role, in menus: "Men’s Narrator".
    public var narratorTitle: String {
        switch self {
        case .narrator: "The Narrator"
        case .men: "Men’s Narrator"
        case .women: "Women’s Narrator"
        case .other: "Other Narrator"
        case .own: "A Narrator of Their Own"
        }
    }

    /// The roles one shared voice reads (they have a voice in `roleVoices`).
    public static let sharedRoles: [VoiceRole] = [.men, .women, .other]
}

/// A character's gender as the narrator chose it.
public enum CharacterGender: String, Codable, CaseIterable, Identifiable, Sendable {
    case male, female, other

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .male: "Male"
        case .female: "Female"
        case .other: "Other"
        }
    }

    /// Other counts as neutral for colors and casting.
    public var voiceGender: VoiceGender {
        switch self {
        case .male: .male
        case .female: .female
        case .other: .neutral
        }
    }

    public init(_ g: VoiceGender) {
        switch g {
        case .male: self = .male
        case .female: self = .female
        case .neutral: self = .other
        }
    }

    /// The shared role for this gender.
    public var role: VoiceRole {
        switch self {
        case .male: .men
        case .female: .women
        case .other: .other
        }
    }
}

/// Full Cast: who reads characters the narrator didn't mark main.
public enum MinorVoices: String, Codable, CaseIterable, Identifiable, Sendable {
    /// The narrator's voice.
    case narrator
    /// The men's, women's or other voice.
    case byGender
    /// A voice of their own, like the main characters.
    case own

    public var id: String { rawValue }
}

/// One Voice: how far characters move from the narrator's voice.
public enum ShadeStrength: String, Codable, CaseIterable, Identifiable, Sendable {
    case subtle, clear, strong

    public var id: String { rawValue }

    public var title: String {
        switch self {
        case .subtle: "Subtle"
        case .clear: "Clear"
        case .strong: "Strong"
        }
    }

    /// Pitch steps for a character of another gender than the narrator.
    public var steps: Int {
        switch self {
        case .subtle: 1
        case .clear: 2
        case .strong: 3
        }
    }
}

/// How one voice changes for a character: pitch and pace steps, plus a few words of texture.
/// A pitch step is a semitone for AI voices (Chatterbox shifts the clip it clones from; with
/// Qwen3-TTS that kept the narrator's voice within ±2–3). Narration Tools shows it as a cue.
public struct Shade: Codable, Hashable, Sendable {
    public static let pitchRange = -4...4
    public static let paceRange = -2...2

    /// -4 (much lower) … 4 (much higher).
    public var pitch: Int
    /// -2 (slower) … 2 (quicker).
    public var pace: Int
    /// Texture: "rough", "breathy". Engines that read descriptions use it; for a narrator
    /// it's part of the cue.
    public var words: String?

    public init(pitch: Int = 0, pace: Int = 0, words: String? = nil) {
        self.pitch = min(Self.pitchRange.upperBound, max(Self.pitchRange.lowerBound, pitch))
        self.pace = min(Self.paceRange.upperBound, max(Self.paceRange.lowerBound, pace))
        let w = words?.trimmingCharacters(in: .whitespacesAndNewlines)
        self.words = (w?.isEmpty ?? true) ? nil : w
    }

    /// No change from the narrator's voice.
    public var isNeutral: Bool { pitch == 0 && pace == 0 && words == nil }

    /// What a narrator reads beside the line: "lower, slower, rough". nil: their own voice.
    public var cue: String? {
        var parts: [String] = []
        switch pitch {
        case ...(-3): parts.append("much lower")
        case -2 ... -1: parts.append("lower")
        case 1...2: parts.append("higher")
        case 3...: parts.append("much higher")
        default: break
        }
        if pace < 0 { parts.append("slower") } else if pace > 0 { parts.append("quicker") }
        if let words { parts.append(words) }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }

    /// Words for engines that take a description (ElevenLabs remixing):
    /// "a little lower, a bit quicker, rough".
    public var directions: String? {
        var parts: [String] = []
        switch pitch {
        case ...(-3): parts.append("much lower")
        case -2 ... -1: parts.append("a little lower")
        case 1...2: parts.append("a little higher")
        case 3...: parts.append("much higher")
        default: break
        }
        if pace < 0 { parts.append("a bit slower") } else if pace > 0 { parts.append("a bit quicker") }
        if let words { parts.append(words) }
        return parts.isEmpty ? nil : parts.joined(separator: ", ")
    }
}

/// Decides who reads each character. Pure: the app passes in what it has already worked
/// out (genders come from `VoiceCasting.profile`, whose pronoun scan is cached), so these are
/// cheap enough for any redraw.
public enum CastingStyleRules {
    /// Which role reads a character.
    /// - `gender`: the character's gender (the narrator's choice, else the book's), nil if
    ///   unknown. `anyMainMarked`: whether the narrator marked anyone main (Full Cast).
    public static func role(for c: BookCharacter, settings: ProjectSettings, gender: CharacterGender?,
                            anyMainMarked: Bool) -> VoiceRole {
        guard let style = settings.castingStyle else { return .own }  // not chosen yet: as today
        switch style {
        case .oneVoice:
            // One voice plays everyone, unless the narrator gave someone a voice of their own.
            return c.role == .own ? .own : .narrator
        case .shared:
            if let r = c.role { return r }
            return (gender ?? .other).role
        case .fullCast:
            if let r = c.role { return r }
            if c.isMain == true || !anyMainMarked { return .own }
            switch settings.minorVoices ?? .narrator {
            case .narrator: return .narrator
            case .byGender: return (gender ?? .other).role
            case .own: return .own
            }
        }
    }

    public static func role(for c: BookCharacter, in project: Project, gender: CharacterGender?) -> VoiceRole {
        role(for: c, settings: project.settings, gender: gender, anyMainMarked: anyMainMarked(project))
    }

    /// The shared roles the style reads characters in, so each needs a voice: all three in
    /// Narrator + Two Voices, and in Full Cast when minor characters go by gender.
    public static func sharedRolesNeeded(_ s: ProjectSettings) -> [VoiceRole] {
        switch s.castingStyle {
        case .shared: VoiceRole.sharedRoles
        case .fullCast where s.minorVoices == .byGender: VoiceRole.sharedRoles
        default: []
        }
    }

    /// Whether the style sorts anyone by gender (so genders need reading).
    public static func usesGender(_ s: ProjectSettings) -> Bool { !sharedRolesNeeded(s).isEmpty }

    /// Every character's gender for casting: the narrator's choice, else the book's. Reads
    /// the book for pronouns (cached after the first time), so the app warms it off the main
    /// thread when a book opens.
    public static func genders(_ project: Project, index: BookIndex) -> [UUID: CharacterGender] {
        var out: [UUID: CharacterGender] = [:]
        for c in project.characters {
            if let g = c.gender ?? VoiceCasting.profile(c, project: project, index: index).gender.map(CharacterGender.init) { out[c.id] = g }
        }
        return out
    }

    /// Who reads each character, keyed by character: the role whose voice speaks (after
    /// `reader(of:)`). Genders are read only when the style needs them (with `index`, or a new
    /// one when none is given).
    public static func readers(_ project: Project, index: BookIndex?) -> [UUID: VoiceRole] {
        let genders = usesGender(project.settings) ? genders(project, index: index ?? BookIndex(project)) : [:]
        let anyMain = anyMainMarked(project)
        var out: [UUID: VoiceRole] = [:]
        for c in project.characters {
            let r = role(for: c, settings: project.settings, gender: c.gender ?? genders[c.id], anyMainMarked: anyMain)
            out[c.id] = reader(of: r, settings: project.settings)
        }
        return out
    }

    public static func anyMainMarked(_ project: Project) -> Bool {
        project.characters.contains { $0.isMain == true }
    }

    /// The role whose voice actually speaks. In Narrator + Two Voices, Narrator means
    /// whoever reads the narration, so a two-person book reads it in the men's or women's
    /// voice.
    public static func reader(of role: VoiceRole, settings: ProjectSettings) -> VoiceRole {
        guard role == .narrator, settings.castingStyle == .shared, let n = settings.narrationRole, n != .own else { return role }
        return n
    }

    /// The character's gender for casting: the narrator's choice, else what the book says.
    public static func gender(of c: BookCharacter, profile: CastingProfile) -> CharacterGender? {
        c.gender ?? profile.gender.map(CharacterGender.init)
    }

    /// The book doesn't settle the gender (nothing found, or a guess from one pronoun) and the
    /// narrator hasn't chosen: listed under Gender to review.
    public static func needsGenderReview(_ c: BookCharacter, profile: CastingProfile) -> Bool {
        c.gender == nil && (profile.gender == nil || profile.genderGuessed)
    }

    /// Characters with lines whose gender needs the narrator, most lines first.
    public static func genderToReview(_ project: Project, index: BookIndex) -> [UUID] {
        project.characters
            .filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .filter { needsGenderReview($0, profile: VoiceCasting.profile($0, project: project, index: index)) }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
            .map(\.id)
    }

    /// Characters InnerVoice suggests marking main (over 15% of the lines, the Index's rule),
    /// leaving out anyone the narrator already marked either way. Only a suggestion.
    public static func suggestedMain(_ project: Project, index: BookIndex) -> [UUID] {
        project.characters
            .filter { $0.isMain == nil && (index.stats[$0.id]?.lines ?? 0) > 0 && !index.showsInIndexByDefault($0.id) }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
            .map(\.id)
    }

    /// One Voice: how the narrator's voice changes for a character. The narrator's own shade
    /// wins; otherwise:
    /// - another gender than the narrator moves 1/2/3 steps (Subtle/Clear/Strong): down for
    ///   men, up for women. The same gender, or Other, stays put. With no narrator gender
    ///   known, men go down and women up.
    /// - a child is 2 higher and a little quicker; an older character 1 lower and slower.
    /// - the voice brief's pitch and pace add a step each way.
    /// - timbre words ("rough", "breathy") become the texture.
    public static func shade(for c: BookCharacter, profile: CastingProfile, gender: CharacterGender?,
                             strength: ShadeStrength?, narratorGender: CharacterGender?) -> Shade {
        if let s = c.shade { return s }
        let steps = (strength ?? .clear).steps
        var pitch = 0, pace = 0
        switch gender {
        case .male where narratorGender != .male: pitch -= steps
        case .female where narratorGender != .female: pitch += steps
        default: break
        }
        switch profile.age {
        case .child: pitch += 2; pace += 1
        case .older: pitch -= 1; pace -= 1
        default: break
        }
        pitch += profile.pitchLean
        pace += profile.paceLean
        // Timbre words that only say the pitch again ("deep", "high") are left out.
        let pitchWords: Set<String> = ["low", "lower", "deep", "deeper", "high", "higher", "bass", "baritone"]
        let texture = profile.timbre.filter { !pitchWords.contains($0.lowercased()) }.prefix(2)
        return Shade(pitch: pitch, pace: pace, words: texture.isEmpty ? nil : texture.joined(separator: ", "))
    }

    /// Narration Tools: the person who reads a character. The narrator's own assignment
    /// wins; otherwise the narrator whose part matches, and when nobody has that part (an
    /// Other part, say), the narration narrator (or the only narrator). Characters with a
    /// voice of their own need an assignment.
    public static func narrator(for c: BookCharacter, in project: Project, gender: CharacterGender?) -> Narrator? {
        if let id = c.narratorID { return project.narrators.first { $0.id == id } }
        let r = role(for: c, in: project, gender: gender)
        guard r != .own else { return nil }
        let reads = reader(of: r, settings: project.settings)
        if let n = project.narrators.first(where: { $0.role == reads }) { return n }
        return narrationNarrator(project)
    }

    /// Narration Tools: the person who reads the narration (whose part is the narration,
    /// or the men's or women's part in a two-person book), else the only narrator.
    public static func narrationNarrator(_ project: Project) -> Narrator? {
        let reads = reader(of: .narrator, settings: project.settings)
        return project.narrators.first { $0.role == reads } ?? project.narrators.first { $0.role == .narrator }
            ?? (project.narrators.count == 1 ? project.narrators.first : nil)
    }

    /// Shades for every character read in the narrator's voice (One Voice, minor
    /// characters in Full Cast, anyone the narrator gave to the narrator). Neutral ones are
    /// left out. `narratorGender`: the voice's or the person's own.
    public static func shades(_ project: Project, index: BookIndex, narratorGender: CharacterGender?) -> [UUID: Shade] {
        guard project.settings.castingStyle != nil else { return [:] }
        var out: [UUID: Shade] = [:]
        for (id, r) in readers(project, index: index) where r == .narrator {
            guard let c = project.character(id) else { continue }
            let p = VoiceCasting.profile(c, project: project, index: index)
            let s = shade(for: c, profile: p, gender: gender(of: c, profile: p), strength: project.settings.shadeStrength, narratorGender: narratorGender)
            if !s.isNeutral { out[id] = s }
        }
        return out
    }
}
