import InnerVoiceKit
import SwiftUI

/// Casting Style › First-Person Narrator: Narrator + Two Voices plus the "I" character,
/// who can sound the same as the narrator, a variant of them, or entirely different. The
/// narrator chooses who that is, what they read, and who tells each chapter.
struct FirstPersonOptions: View {
    @ObservedObject var document: InnerVoiceDocument
    /// Narration Tools: people read, so the voice choice is a cue.
    var narration = false
    @Environment(\.undoManager) private var undoManager
    @State private var showChapters = false

    private var settings: ProjectSettings { document.project.settings }
    private var fp: FirstPersonSettings { settings.firstPerson ?? FirstPersonSettings() }
    private var characters: [BookCharacter] {
        document.project.characters.sorted { (document.index.stats[$0.id]?.lines ?? 0) > (document.index.stats[$1.id]?.lines ?? 0) }
    }

    private func update(_ name: String, _ body: @escaping (inout FirstPersonSettings) -> Void) {
        document.updateSettings(name, undoManager: undoManager) { s in
            var f = s.firstPerson ?? FirstPersonSettings()
            body(&f)
            s.firstPerson = f
        }
    }

    var body: some View {
        Picker("“I” Character", selection: Binding(get: { settings.povCharacterID },
                                                     set: { id in document.updateSettings("Set Point of View", undoManager: undoManager) { $0.povCharacterID = id } })) {
            Text("Not Chosen").tag(UUID?.none)
            ForEach(characters) { Text($0.name).tag(UUID?.some($0.id)) }
        }
        .help("The character who tells the story in the first person")

        Picker("Their Voice", selection: Binding(get: { fp.voiceMode }, set: { v in update("Change First-Person Voice") { $0.voice = v } })) {
            ForEach(FirstPersonVoice.allCases) { Text($0.title).tag($0) }
        }
        .help(narration
              ? "Read them in your narrator voice, a shaded version of it, or a different voice"
              : "The narrator’s voice, a variant of it (shaded below), or a voice of their own chosen on the Voices page")

        if fp.voiceMode == .variant {
            HStack(spacing: Space.md) {
                Stepper("Pitch \(fp.variantShade.pitch)", value: Binding(get: { fp.variantShade.pitch }, set: { p in
                    update("Change First-Person Variant") { $0.shade = Shade(pitch: p, pace: $0.variantShade.pace, words: $0.variantShade.words) }
                }), in: Shade.pitchRange)
                Stepper("Pace \(fp.variantShade.pace)", value: Binding(get: { fp.variantShade.pace }, set: { p in
                    update("Change First-Person Variant") { $0.shade = Shade(pitch: $0.variantShade.pitch, pace: p, words: $0.variantShade.words) }
                }), in: Shade.paceRange)
            }
            .help("How far their voice moves from the narrator’s: pitch in semitones, pace slower or quicker")
        }

        Toggle("Their voice reads the narration", isOn: Binding(get: { fp.readsNarration ?? false }, set: { on in update("Change Who Reads the Narration") { $0.readsNarration = on ? true : nil } }))
            .help("On: the whole book is in their voice, as they tell it. Off: the narrator reads the narration and they speak only their own lines.")
        Toggle("Their voice reads their thoughts", isOn: Binding(get: { fp.voicesThoughts ?? true }, set: { on in update("Change Who Reads Thoughts") { $0.voicesThoughts = on ? nil : false } }))
            .help("Off: the narrator reads their thoughts")

        Button(chapterButtonTitle) { showChapters = true }
            .help("For books with more than one first-person narrator: choose who tells each chapter")
            .popover(isPresented: $showChapters) { chapterList.padding(Space.lg).frame(width: 360, height: 420) }

        Text(narration
             ? "Men and women are read by their narrators, as with Three Narrators. The “I” character is read as chosen here."
             : "Men and women share a voice each, as with Narrator + Two Voices. The “I” character sounds as chosen here.")
            .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }

    private var chapterButtonTitle: String {
        let n = fp.chapterNarrators?.count ?? 0
        return n == 0 ? "Narrator by Chapter…" : "Narrator by Chapter (\(n))…"
    }

    private var chapterList: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Who Tells Each Chapter").font(.headline)
            Text("Chapters set to the book’s “I” character follow the choice above.")
                .font(.caption).foregroundStyle(.secondary)
            List(document.project.chapters) { ch in
                Picker(ch.title.isEmpty ? "Opening" : ch.title, selection: Binding(get: { fp.chapterNarrators?[ch.id] }, set: { id in
                    update("Change Chapter Narrator") { f in
                        var map = f.chapterNarrators ?? [:]
                        map[ch.id] = id
                        f.chapterNarrators = map.isEmpty ? nil : map
                    }
                })) {
                    Text("Book’s “I” Character").tag(UUID?.none)
                    ForEach(characters) { Text($0.name).tag(UUID?.some($0.id)) }
                }
            }
        }
    }
}
