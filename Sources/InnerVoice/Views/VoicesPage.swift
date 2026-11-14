import AppKit
import InnerVoiceKit
import SwiftUI

/// Who reads what: the narrator and every character with lines on the left, and on the
/// right what the book says they sound like and the voices to choose from, each one
/// playable with that character's own words.
struct VoicesPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager
    @State private var query = ""
    /// Show only characters whose gender the book doesn't settle.
    @State private var genderFilter = false
    /// The characters picked, in list order.
    private var picked: Set<VoiceTarget> {
        get { workspace.pickedVoices }
        nonmutating set { workspace.pickedVoices = newValue }
    }

    private var pickedCharacters: [UUID] {
        let shown = groups.flatMap(\.characters).map(\.id)
        return shown.filter { picked.contains(.character($0)) }
    }

    private var engine: VoiceEngine { document.project.engineInUse }
    private var style: CastingStyle? { document.project.settings.castingStyle }

    private var cast: [BookCharacter] {
        let index = document.index
        return document.project.characters.filter { (index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }
    }

    var body: some View {
        HStack(spacing: 0) {
            VStack(spacing: 0) {
                CastingStyleHeader(document: document, genderFilter: $genderFilter)
                Divider()
                list
            }
            .frame(width: 280)
            Divider()
            SwiftUI.Group {
                if picked.count > 1, !pickedCharacters.isEmpty {
                    ManyCharactersPanel(document: document, ids: pickedCharacters, changed: {
                        // Settled genders leave the Genders to Review list.
                        if genderFilter { picked = [] }
                    }, clear: { picked = [] })
                } else {
                    VoiceTargetDetail(document: document, workspace: workspace, target: workspace.voiceTarget)
                }
            }
            .frame(maxWidth: .infinity, maxHeight: .infinity)
        }
        .navigationTitle("Voices")
        .pageSubtitle(subtitle)
        .toolbar {
            ToolbarItem {
                Picker("Engine", selection: Binding(get: { engine }, set: { e in
                    // The first time a book uses ElevenLabs, ask before any of its text is sent.
                    if e.isCloud, document.project.settings.cloudVoicesAllowed == nil {
                        workspace.askCloudEngine = e
                    } else {
                        document.setVoiceEngine(e, undoManager: undoManager)
                    }
                })) {
                    ForEach(EngineSwitches.shared.choices) { e in
                        Text(e.title + (e.isCloud ? " (Cloud)" : ModelSpeech.isInstalled(e) ? "" : " (Not Downloaded)")).tag(e)
                    }
                }
                .help("The speech engine the project’s voices use. Each engine keeps its own casting.")
            }
            ToolbarItem {
                Button("Cast Voices Again", systemImage: "wand.and.stars") { document.castVoicesAgain(undoManager: undoManager) }
                    .help("Choose every voice again from the character summaries. Voices you chose stay.")
            }
            ToolbarItem {
                Button("Use Voices from Another Project", systemImage: "books.vertical") { workspace.showSeriesVoices = true }
                    .help("Give characters the voices they had in an earlier book in the series. You match the characters first.")
            }
            ToolbarItem {
                PlayStopButton(document: document, workspace: workspace)
            }
        }
        .onAppear {
            VoiceCastingRun.castIfNeeded(document: document)
            ModelSpeech.prepare(engine)
            if ElevenLabs.shared.isSetUp, ElevenLabs.shared.voices.isEmpty, engine == .elevenlabs || engine == .mixed {
                Task { try? await ElevenLabs.shared.refreshVoices() }
            }
        }
        .onChange(of: style) { if !CastingStyleRules.usesGender(document.project.settings) { genderFilter = false } }
        .cloudPermissionAlert(document: document, workspace: workspace)
    }

    private var subtitle: String {
        let readers = CastingStyleRules.readers(document.project, index: document.index)
        let unsure = cast.filter { (readers[$0.id] ?? .own) == .own && document.project.voiceNeedsYou($0, engine: engine) }.count
        return unsure == 1 ? "1 voice needs you" : unsure > 1 ? "\(unsure) voices need you" : engine.title
    }

    // MARK: The list

    /// A group in the list: the characters one role reads, with the role's own voice first
    /// for Men, Women and Other.
    private struct Group: Identifiable {
        var id: String
        var title: String
        var role: VoiceRole?
        var characters: [BookCharacter]
    }

    private var groups: [Group] {
        let project = document.project
        let readers = CastingStyleRules.readers(project, index: document.index)
        let review = genderFilter ? Set(CastingStyleRules.genderToReview(project, index: document.index)) : nil
        let shown = cast.filter { (query.isEmpty || $0.searchMatches(query)) && (review?.contains($0.id) ?? true) }
        func reading(_ r: VoiceRole) -> [BookCharacter] { shown.filter { (readers[$0.id] ?? .own) == r } }
        guard let style else { return [Group(id: "cast", title: "Cast", role: nil, characters: shown)] }
        let anyMain = CastingStyleRules.anyMainMarked(project)
        var out: [Group] = []
        switch style {
        case .oneVoice:
            out.append(Group(id: "narrator", title: "Played by the Narrator", role: nil, characters: reading(.narrator)))
            out.append(Group(id: "own", title: "Own Voice", role: nil, characters: reading(.own)))
        case .shared:
            for r in VoiceRole.sharedRoles { out.append(Group(id: r.rawValue, title: r.title, role: r, characters: reading(r))) }
            out.append(Group(id: "own", title: "Own Voice", role: nil, characters: reading(.own)))
            out.append(Group(id: "narrator", title: "Read by the Narrator", role: nil, characters: reading(.narrator)))
        case .fullCast:
            out.append(Group(id: "own", title: anyMain ? "Main Characters" : "Cast", role: nil, characters: reading(.own)))
            out.append(Group(id: "narrator", title: "Read by the Narrator", role: nil, characters: reading(.narrator)))
            if CastingStyleRules.usesGender(project.settings) {
                for r in VoiceRole.sharedRoles { out.append(Group(id: r.rawValue, title: r.title, role: r, characters: reading(r))) }
            }
        }
        // A shared voice stays listed even with nobody in it, so it can be chosen ahead.
        return out.filter { !$0.characters.isEmpty || ($0.role != nil && !genderFilter && query.isEmpty) }
    }

    private var list: some View {
        let readers = CastingStyleRules.readers(document.project, index: document.index)
        let shades = document.project.settings.castingStyle == nil ? [:] : VoiceResolver(project: document.project, index: document.index).shades
        func caption(_ c: BookCharacter, _ reader: VoiceRole) -> String? {
            switch reader {
            case .own: nil
            case .narrator: shades[c.id]?.cue.map { "Narrator, \($0)" } ?? "Narrator’s voice"
            default: reader.voiceCaption
            }
        }
        let narrationReader = CastingStyleRules.reader(of: .narrator, settings: document.project.settings)
        return List(selection: Binding(get: { picked.count > 1 ? picked : [workspace.voiceTarget] }, set: { new in
            if new.count > 1 {
                picked = new
            } else {
                picked = []
                workspace.voiceTarget = new.first ?? .narrator
            }
        })) {
            Section("Narration") {
                row(name: "Narrator", color: nil, voice: document.project.settings.narratorVoice(for: engine), needs: false,
                    pov: VoiceResolver.povNarrator(project: document.project, engine: engine),
                    caption: narrationReader == .narrator ? nil : "Read in the " + narrationReader.voiceCaption.lowercased())
                    .tag(VoiceTarget.narrator)
            }
            ForEach(groups) { g in
                Section(g.title) {
                    if let r = g.role {
                        row(name: r.voiceTitle, color: nil, voice: document.project.settings.roleVoice(r, for: engine), needs: false,
                            symbol: "person.2", caption: nil)
                            .tag(VoiceTarget.role(r))
                    }
                    ForEach(g.characters) { c in
                        let reader = readers[c.id] ?? .own
                        row(name: c.name, color: Color(hexString: c.colorHex), voice: c.voice(for: engine),
                            needs: reader == .own && document.project.voiceNeedsYou(c, engine: engine),
                            caption: caption(c, reader),
                            isMain: style == .fullCast && c.isMain == true)
                            .tag(VoiceTarget.character(c.id))
                    }
                }
            }
        }
        .listStyle(.inset)
        // Right-click: one character's menu, or the same choices for every picked character.
        .contextMenu(forSelectionType: VoiceTarget.self) { targets in
            let ids = targets.compactMap(\.characterID)
            if ids.count > 1 {
                ManyCharactersMenu(document: document, ids: ids)
            } else if let id = ids.first, let c = document.project.character(id) {
                CastingStyleMenu(document: document, character: c)
            }
        }
        .searchable(text: $query, placement: .sidebar, prompt: "Find a character")
    }

    private func row(name: String, color: Color?, voice: CastVoice?, needs: Bool, pov: BookCharacter? = nil,
                     symbol: String = "book.closed", caption: String?, isMain: Bool = false) -> some View {
        HStack(spacing: Space.sm) {
            if let color {
                Circle().fill(color).frame(width: 10, height: 10)
            } else {
                Image(systemName: symbol).foregroundStyle(.secondary).frame(width: 10)
            }
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: Space.xs) {
                    Text(name).lineLimit(1)
                    if isMain {
                        Image(systemName: "star.fill").font(.caption2).foregroundStyle(.secondary)
                            .help("Main character: always has a voice of their own")
                    }
                }
                Text(caption ?? pov.map { "\($0.firstName)’s voice (first person)" } ?? voice.map { VoiceCatalog.name(of: $0.choice) } ?? "No voice yet")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
            if needs {
                Text("?")
                    .font(.caption.weight(.bold))
                    .foregroundStyle(Color(nsColor: .attention))
                    .help("Casting isn’t sure of this voice. Listen, then confirm it or choose another.")
            } else if caption == nil, voice?.source == .user {
                Image(systemName: "checkmark").font(.caption).foregroundStyle(.tertiary)
                    .help("You chose this voice")
            }
        }
        .padding(.vertical, 2)
    }
}

/// The Casting Style above the Voices list: the style, its one follow-up choice, and the
/// Gender to Review filter.
struct CastingStyleHeader: View {
    @ObservedObject var document: InnerVoiceDocument
    @Binding var genderFilter: Bool
    /// Narration Tools (the Cast page): people read the parts, not voices.
    var narration = false
    @Environment(\.undoManager) private var undoManager

    private var settings: ProjectSettings { document.project.settings }

    var body: some View {
        let review = CastingStyleRules.genderToReview(document.project, index: document.index).count
        VStack(alignment: .leading, spacing: Space.sm) {
            Picker("Casting Style", selection: Binding(get: { settings.castingStyle }, set: { document.setCastingStyle($0, undoManager: undoManager) })) {
                if settings.castingStyle == nil { Text("Not Chosen").tag(CastingStyle?.none) }
                ForEach(CastingStyle.allCases) { Text($0.title(for: narration ? .narration : .aiVoices)).tag(CastingStyle?.some($0)) }
            }
            .help(narration
                  ? "How the parts are shared: you read everyone, three narrators (narration, men, women), or a narrator per character. Changing it keeps every assignment."
                  : "How voices are shared: one voice for everyone, a narrator plus voices for men, women and others, or a voice per character. Changing it keeps every voice.")
            switch settings.castingStyle {
            case .shared:
                Picker("Narration", selection: Binding(get: { settings.narrationRole ?? .narrator }, set: { document.setNarrationRole($0, undoManager: undoManager) })) {
                    Text(narration ? "A Narrator of Its Own" : "Narrator’s Own Voice").tag(VoiceRole.narrator)
                    Text(narration ? "The Men’s Narrator" : "Men’s Voice").tag(VoiceRole.men)
                    Text(narration ? "The Women’s Narrator" : "Women’s Voice").tag(VoiceRole.women)
                }
                .help(narration ? "Who reads the narration. Choose the men’s or women’s narrator for a book read by two people."
                                : "Who reads the narration. Choose the men’s or women’s voice for a book read by two people.")
                if narration, [VoiceRole.narrator, .men, .women].contains(where: { r in !document.project.narrators.contains { $0.role == r } }) {
                    Button("Set Up Narrators") { document.setUpThreeNarrators(undoManager: undoManager) }
                        .help("Add a narrator for the narration, the men and the women. Rename them in Narrators.")
                }
            case .fullCast:
                Picker("Minor", selection: Binding(get: { settings.minorVoices ?? .narrator }, set: { document.setMinorVoices($0, undoManager: undoManager) })) {
                    Text(narration ? "The Narrator" : "Narrator’s Voice").tag(MinorVoices.narrator)
                    Text(narration ? "Men’s, Women’s or Other Narrator" : "Men’s, Women’s or Other Voice").tag(MinorVoices.byGender)
                    Text(narration ? "A Narrator of Their Own" : "Their Own Voice").tag(MinorVoices.own)
                }
                .help("Who reads characters you haven’t marked as main. Right-click a character to mark them main.")
                if (settings.minorVoices ?? .narrator) == .narrator { shading }
                mainCharacters
            case .oneVoice:
                if narration { yourVoice }
                shading
                Text(narration
                     ? "You play every character, shading your voice a little lower for men and higher for women. The cues show in the margin and the Teleprompter."
                     : "The narrator plays every character, shading their voice a little lower for men and higher for women. Right-click a character to give them a voice of their own.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            case nil:
                Text(narration
                     ? "Choose how the parts are shared. Until you do, cast characters to narrators one by one."
                     : "Choose how this book shares its voices. Every character has a voice of their own until you do.")
                    .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
            }
            if review > 0 || genderFilter {
                Toggle(isOn: $genderFilter) {
                    Label(review == 1 ? "1 Gender to Review" : "\(review) Genders to Review", systemImage: "person.fill.questionmark")
                }
                .toggleStyle(.button)
                .controlSize(.small)
                .help("Show only characters whose gender the book doesn’t settle. Right-click one to choose Male, Female or Other.")
            }
        }
        .controlSize(.small)
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
    }

    /// Just Me: the narrator's own voice, so characters of the same gender aren't shaded.
    private var yourVoice: some View {
        Picker("Your Voice", selection: Binding(get: { settings.narratorGender }, set: { document.setNarratorGender($0, undoManager: undoManager) })) {
            Text("Not Set").tag(CharacterGender?.none)
            Text("Male").tag(CharacterGender?.some(.male))
            Text("Female").tag(CharacterGender?.some(.female))
            Text("Other").tag(CharacterGender?.some(.other))
        }
        .help("Your own voice: characters of the same gender are read in your voice, others shaded lower or higher")
    }

    /// How far the narrator's voice moves for the characters it plays.
    private var shading: some View {
        Picker("Shading", selection: Binding(get: { settings.shadeStrength ?? .clear }, set: { document.setShadeStrength($0, undoManager: undoManager) })) {
            ForEach(ShadeStrength.allCases) { Text($0.title).tag($0) }
        }
        .help("How much the narrator’s voice changes for a character of another gender: Subtle is about a semitone, Clear two, Strong three. Age and the voice brief add to it.")
    }

    /// Full Cast: nobody marked main yet, so offer InnerVoice's suggestion (only a suggestion).
    @ViewBuilder
    private var mainCharacters: some View {
        if !CastingStyleRules.anyMainMarked(document.project) {
            let suggested = CastingStyleRules.suggestedMain(document.project, index: document.index)
            let names = suggested.compactMap { document.project.character($0)?.firstName }
            if !names.isEmpty {
                VStack(alignment: .leading, spacing: Space.xs) {
                    Text("Main characters always have a voice of their own. \(ListFormatter.localizedString(byJoining: names)) \(names.count == 1 ? "has" : "have") the most lines.")
                        .font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                    Button(names.count == 1 ? "Mark as Main" : "Mark These \(names.count) as Main") {
                        document.setMain(true, for: suggested, undoManager: undoManager)
                    }
                    .help("Mark them as main characters. You can change anyone with a right-click.")
                }
            }
        }
    }
}

/// Right-click on a character (Voices page): who reads them, main character, gender.
struct CastingStyleMenu: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter
    /// Narration Tools: the parts are narrators, not voices.
    var narration = false
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let style = document.project.settings.castingStyle
        if let style {
            Picker("Read By", selection: Binding(get: { character.role }, set: { document.setRole($0, for: character.id, undoManager: undoManager) })) {
                Text("Automatic").tag(VoiceRole?.none)
                ForEach(style == .oneVoice ? [VoiceRole.narrator, .own] : VoiceRole.allCases) { r in
                    Text(narration ? r.narratorTitle : r == .own ? r.title : r.voiceTitle).tag(VoiceRole?.some(r))
                }
            }
            if style == .fullCast {
                Toggle("Main Character", isOn: Binding(get: { character.isMain == true },
                                                       set: { document.setMain($0 ? true : false, for: [character.id], undoManager: undoManager) }))
            }
        }
        Picker("Gender", selection: Binding(get: { character.gender }, set: { document.setGender($0, for: [character.id], undoManager: undoManager) })) {
            Text("From the Book").tag(CharacterGender?.none)
            ForEach(CharacterGender.allCases) { Text($0.title).tag(CharacterGender?.some($0)) }
        }
    }
}

/// The narrator, a shared role's voice, or a character, on the Voices page.
enum VoiceTarget: Hashable {
    case narrator
    case role(VoiceRole)
    case character(UUID)

    var characterID: UUID? {
        if case .character(let id) = self { return id }
        return nil
    }

    var role: VoiceRole? {
        if case .role(let r) = self { return r }
        return nil
    }
}

/// One character's (or the narrator's, or a shared role's) voice: what the book says, the
/// voice now, the best fits and every voice.
struct VoiceTargetDetail: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    let target: VoiceTarget
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let character = target.characterID.flatMap { document.project.character($0) }
        let readers = CastingStyleRules.readers(document.project, index: document.index)
        let reader = character.flatMap { readers[$0.id] } ?? .own
        ScrollView {
            VStack(alignment: .leading, spacing: Space.xl) {
                if let character {
                    HStack(spacing: Space.md) {
                        Avatar(character: character, size: 44)
                        VStack(alignment: .leading, spacing: 2) {
                            Text(character.name).font(.title2.weight(.semibold))
                            Text("\(document.index.stats[character.id]?.lines ?? 0) lines")
                                .foregroundStyle(.secondary)
                        }
                        Spacer()
                        Button("Open \(character.firstName)") { workspace.showCharacter(character.id) }
                            .help("Open \(character.name)’s page in Cast")
                    }
                    CastingProfileView(document: document, character: character)
                    if document.project.settings.castingStyle != nil { readBy(character, reader: reader) }
                    CloudNotice(document: document, workspace: workspace)
                } else if let role = target.role {
                    roleHeader(role, readers: readers)
                    CloudNotice(document: document, workspace: workspace)
                } else {
                    VStack(alignment: .leading, spacing: Space.xs) {
                        Text("Narrator").font(.title2.weight(.semibold))
                        Text("Reads the narration in a table read, lines nobody is cast for, and characters set to Same as Narrator. Letters, signs and other written text too, unless Settings › Voices says otherwise.")
                            .foregroundStyle(.secondary)
                        let narration = CastingStyleRules.reader(of: .narrator, settings: document.project.settings)
                        if narration != .narrator {
                            Label("In this book the \(narration.voiceCaption.lowercased()) reads the narration (Casting Style). The voice below is used only if you change that.", systemImage: "person.2")
                                .font(.callout)
                                .padding(.top, Space.xs)
                        } else if let pov = VoiceResolver.povNarrator(project: document.project) {
                            Label("This book is told in the first person by \(pov.name), so the narration uses \(pov.firstName)’s voice. Choose a voice below to give the narrator a voice of its own.", systemImage: "person.wave.2")
                                .font(.callout)
                                .padding(.top, Space.xs)
                        }
                    }
                    CloudNotice(document: document, workspace: workspace)
                }
                // A character read by the narrator or a shared voice has no voice to choose here.
                if character == nil || reader == .own {
                    VoiceChooser(document: document, characterID: character?.id, role: target.role, compact: false,
                                 onClone: { workspace.cloneVoice(for: character?.id) })
                }
            }
            .padding(Space.margin)
            .frame(maxWidth: 760, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
        .id(target)
    }

    private func roleHeader(_ role: VoiceRole, readers: [UUID: VoiceRole]) -> some View {
        let names = document.project.characters.filter { readers[$0.id] == role && (document.index.stats[$0.id]?.lines ?? 0) > 0 }
            .sorted { (document.index.stats[$0.id]?.lines ?? 0) > (document.index.stats[$1.id]?.lines ?? 0) }
            .map(\.name)
        let who: String = switch role {
        case .men: "every male character"
        case .women: "every female character"
        default: "characters who are neither male nor female, or whose gender the book doesn’t say"
        }
        return VStack(alignment: .leading, spacing: Space.xs) {
            Text(role.voiceTitle).font(.title2.weight(.semibold))
            Text("One voice for \(who), unless you give someone a voice of their own.")
                .foregroundStyle(.secondary)
            Text(names.isEmpty ? "Nobody yet." : "Reads " + ListFormatter.localizedString(byJoining: names) + ".")
                .font(.callout)
                .padding(.top, Space.xs)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    /// Who reads this character under the Casting Style, and how to change it.
    @ViewBuilder
    private func readBy(_ c: BookCharacter, reader: VoiceRole) -> some View {
        let style = document.project.settings.castingStyle
        VStack(alignment: .leading, spacing: Space.sm) {
            if reader == .own {
                Label("\(c.firstName) has a voice of their own\(c.role == .own ? ", as you chose" : "").", systemImage: "person.wave.2")
            } else {
                Label("Read in the \(reader.voiceCaption.lowercased())\(c.role == nil ? "" : ", as you chose").", systemImage: "person.2")
            }
            HStack(spacing: Space.sm) {
                if reader != .own {
                    Button("Give \(c.firstName) a Voice of Their Own") { document.setRole(.own, for: c.id, undoManager: undoManager) }
                        .help("\(c.firstName) keeps their own voice whatever the Casting Style. Their voice is chosen below.")
                    if VoiceRole.sharedRoles.contains(reader) {
                        Button("Show \(reader.voiceTitle)") { workspace.voiceTarget = .role(reader) }
                    }
                } else if c.role != nil {
                    Button("Use the Casting Style") { document.setRole(nil, for: c.id, undoManager: undoManager) }
                        .help("Let the Casting Style decide who reads \(c.firstName) again")
                }
                if style == .fullCast {
                    Toggle("Main Character", isOn: Binding(get: { c.isMain == true },
                                                           set: { document.setMain($0 ? true : false, for: [c.id], undoManager: undoManager) }))
                        .toggleStyle(.checkbox)
                        .help("Main characters always have a voice of their own")
                }
            }
            .controlSize(.small)
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
        if reader == .narrator { ShadeEditor(document: document, character: c) }
    }
}

/// How the narrator's voice changes for a character it plays: automatic (from gender, age
/// and the voice brief) or the narrator's own pitch and pace. Every change can be heard.
struct ShadeEditor: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let resolver = VoiceResolver(project: document.project, index: document.index)
        let shade = character.shade ?? resolver.shades[character.id] ?? Shade()
        let engine = resolver.narrator?.engine ?? document.project.engineInUse
        VStack(alignment: .leading, spacing: Space.md) {
            HStack {
                Text("Shade").font(.headline)
                Spacer()
                Toggle("Automatic", isOn: Binding(get: { character.shade == nil },
                                                  set: { document.setShade($0 ? nil : shade, for: character.id, undoManager: undoManager) }))
                    .toggleStyle(.checkbox)
                    .help("Work out \(character.firstName)’s shade from their gender, age and voice brief")
            }
            Text(shade.cue.map { "The narrator’s voice, \($0)." } ?? "The narrator’s own voice, unchanged.")
                .foregroundStyle(.secondary)
            HStack(spacing: Space.xl) {
                Stepper(value: Binding(get: { shade.pitch }, set: { set(pitch: $0, pace: shade.pace, shade) }), in: Shade.pitchRange) {
                    Text("Pitch: \(steps(shade.pitch, low: "lower", high: "higher"))")
                }
                .help("Semitones lower or higher than the narrator’s voice. Within 2 or 3 it still sounds like the narrator.")
                Stepper(value: Binding(get: { shade.pace }, set: { set(pitch: shade.pitch, pace: $0, shade) }), in: Shade.paceRange) {
                    Text("Pace: \(shade.pace == 0 ? "the narrator’s" : shade.pace < 0 ? "slower" : "quicker")")
                }
                .help("A little slower or quicker than the narrator")
            }
            HStack(spacing: Space.sm) {
                if let n = resolver.narrator {
                    Button("Hear \(character.firstName)", systemImage: "play.fill") {
                        PlayCommand.audition(n, character: character, document: document, shade: shade)
                    }
                    Button("Hear the Narrator") { PlayCommand.audition(n, character: character, document: document) }
                        .help("The same line in the narrator’s own voice, to compare")
                }
                Spacer()
                Text(note(engine)).font(.caption).foregroundStyle(.secondary).multilineTextAlignment(.trailing)
            }
            .controlSize(.small)
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
    }

    private func set(pitch: Int, pace: Int, _ old: Shade) {
        document.setShade(Shade(pitch: pitch, pace: pace, words: old.words), for: character.id, undoManager: undoManager)
    }

    private func steps(_ n: Int, low: String, high: String) -> String {
        n == 0 ? "the narrator’s" : "\(abs(n)) \(n < 0 ? low : high)"
    }

    private func note(_ engine: VoiceEngine) -> String {
        switch engine {
        case .pocket: "Pocket TTS shifts the clip it copies the narrator’s voice from, so it stays the same voice."
        case .apple: "Apple’s voices change pitch and speed."
        default: "\(engine.title) shifts the finished audio. Keep it within 2 to sound natural."
        }
    }
}

/// What casting read about a character: gender, age, accent and voice, and where from. The
/// gender can be chosen here (Male, Female, Other, or what the book says).
struct CastingProfileView: View {
    @ObservedObject var document: InnerVoiceDocument
    let character: BookCharacter
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let p = VoiceCasting.profile(character, project: document.project, index: document.index)
        var fromBook = character
        fromBook.gender = nil
        let book = character.gender == nil ? p : VoiceCasting.profile(fromBook, project: document.project, index: document.index)
        let review = CastingStyleRules.needsGenderReview(character, profile: book)
        return VStack(alignment: .leading, spacing: Space.sm) {
            HStack(alignment: .top, spacing: Space.xl) {
                VStack(alignment: .leading, spacing: 0) {
                    Text("Gender").font(.caption).foregroundStyle(.secondary)
                    Picker("Gender", selection: Binding(get: { character.gender },
                                                        set: { document.setGender($0, for: [character.id], undoManager: undoManager) })) {
                        Text(book.gender.map { "\(CharacterGender($0).title) (from the book)" } ?? "Unknown").tag(CharacterGender?.none)
                        ForEach(CharacterGender.allCases) { Text($0.title).tag(CharacterGender?.some($0)) }
                    }
                    .labelsHidden()
                    .fixedSize()
                    .controlSize(.small)
                    .help("Choose \(character.firstName)’s gender, or leave what the book says. Casting and Automatic colors follow it.")
                }
                trait("Age", p.age?.title)
                trait("Accent", p.accent)
                trait("Voice", p.timbre.isEmpty ? nil : p.timbre.joined(separator: ", "))
            }
            if review, character.gender == nil {
                Text(book.gender == nil
                     ? "The book hasn’t said whether \(character.firstName) is a man, a woman or neither. Choose above, or Build Summaries reads it for details."
                     : "This is a guess from a single pronoun. Choose above to settle it.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            } else if !p.evidence.isEmpty {
                Text(p.evidence.joined(separator: ". ") + ".")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 10))
    }

    private func trait(_ label: String, _ value: String?) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            Text(value ?? "Unknown").foregroundStyle(value == nil ? .tertiary : .primary)
        }
    }
}

/// The voice now, the best fits, and (when not compact) every voice of the engine. Every
/// voice can be heard with the character's own words before choosing it.
struct VoiceChooser: View {
    @ObservedObject var document: InnerVoiceDocument
    /// nil is the narrator (or, with `role`, a shared voice).
    let characterID: UUID?
    /// A shared Casting Style voice (Men, Women, Other) instead of a character or the narrator.
    var role: VoiceRole? = nil
    var compact = false
    /// Opens Clone a Voice for this character (only where a window's workspace is at hand).
    var onClone: (() -> Void)? = nil
    @Environment(\.undoManager) private var undoManager
    @State private var filter: VoiceGender?
    @State private var voiceQuery = ""
    @State private var cloudDesigning = false
    @State private var loadingCloudVoices = false
    @State private var cloudVoicesError: String?
    @State private var cloudDesignError: String?

    private var engine: VoiceEngine { document.project.engineInUse }
    private var character: BookCharacter? { characterID.flatMap { document.project.character($0) } }
    private var voice: CastVoice? {
        if let role { return document.project.settings.roleVoice(role, for: engine) }
        return characterID == nil ? document.project.settings.narratorVoice(for: engine) : character?.voice(for: engine)
    }

    /// Whose lines an audition says: the character, or for a shared voice the first
    /// character it reads.
    private var sampleCharacter: BookCharacter? {
        if let character { return character }
        guard let role else { return nil }
        let readers = CastingStyleRules.readers(document.project, index: document.index)
        return document.project.characters.filter { readers[$0.id] == role }
            .max { (document.index.stats[$0.id]?.lines ?? 0) < (document.index.stats[$1.id]?.lines ?? 0) }
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            current
            if document.project.settings.engine != engine {
                Label("\(document.project.settings.engine.title) is turned off in Settings › Voices, so this project uses \(engine.title).", systemImage: "info.circle")
                    .font(.callout)
                    .foregroundStyle(.secondary)
            }
            if !ModelSpeech.isInstalled(engine), let pack = VoicePack.pack(for: engine) {
                VStack(alignment: .leading, spacing: Space.sm) {
                    Label("\(engine.title) isn’t downloaded yet, so these voices can’t be heard.", systemImage: "arrow.down.circle")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    VoicePackRow(pack: pack)
                }
            }
            if ElevenLabs.shared.isSetUp, ElevenLabs.shared.voices.isEmpty, engine == .elevenlabs || engine == .mixed { cloudVoicesMissing }
            suggestions
            if let onClone { cloneOffer(onClone) }
            if engine == .pocket || (engine == .mixed && EngineSwitches.shared.usable(.pocket)) { descriptionNote }
            if let c = character, showsCloudDesign { cloudDesign(c) }
            if !compact { allVoices }
        }
    }

    // MARK: The voice now

    private var current: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack(spacing: Space.md) {
                VStack(alignment: .leading, spacing: 2) {
                    Text("Voice").font(.headline)
                    if let v = voice {
                        HStack(spacing: Space.sm) {
                            Text(VoiceCatalog.name(of: v.choice))
                            if let info = VoiceCatalog.info(for: v.choice) {
                                Text(describe(info)).foregroundStyle(.secondary)
                            }
                        }
                        if v.source == .auto {
                            Text(document.project.settings.unsureBelow > v.confidence
                                 ? "Chosen by casting, \(Int(v.confidence * 100))% sure. Listen, then confirm it or choose another."
                                 : "Chosen by casting, \(Int(v.confidence * 100))% sure.")
                                .font(.caption)
                                .foregroundStyle(v.confidence < document.project.settings.unsureBelow ? Color(nsColor: .attention) : .secondary)
                        }
                    } else {
                        Text("No voice yet. Choose one below.").foregroundStyle(.secondary)
                    }
                }
                Spacer()
                if let v = voice {
                    Button("Play", systemImage: "play.fill") { audition(v.choice) }
                        .help("Hear this voice say one of \(character?.firstName ?? "the narrator")’s lines")
                    if v.source == .auto, let id = characterID {
                        Button("Confirm") { document.confirmVoice(for: id, undoManager: undoManager) }
                            .buttonStyle(.borderedProminent)
                            .help("Keep this voice. Casting won’t change it.")
                    } else if v.source == .auto, let role {
                        Button("Confirm") { document.setRoleVoice(v.choice, for: role, undoManager: undoManager) }
                            .buttonStyle(.borderedProminent)
                            .help("Keep this voice. Casting won’t change it.")
                    }
                }
            }
            if let v = voice, engine == .apple {
                adjustments(v.choice)
            }
        }
    }

    /// Speed and pitch for Apple's voices: an older character a little lower and slower.
    private func adjustments(_ choice: VoiceChoice) -> some View {
        HStack(spacing: Space.xl) {
            slider("Speed", value: choice.rate ?? 1, range: 0.7...1.3) { var c = choice; c.rate = $0 == 1 ? nil : $0; choose(c) }
            slider("Pitch", value: choice.pitch ?? 1, range: 0.7...1.4) { var c = choice; c.pitch = $0 == 1 ? nil : $0; choose(c) }
        }
        .controlSize(.small)
    }

    private func slider(_ label: String, value: Double, range: ClosedRange<Double>, set: @escaping (Double) -> Void) -> some View {
        HStack(spacing: Space.sm) {
            Text(label).font(.callout).foregroundStyle(.secondary)
            Slider(value: Binding(get: { value }, set: { set(($0 * 20).rounded() / 20) }), in: range)
                .frame(width: 140)
            Text(String(format: "%.2f×", value)).font(.caption.monospacedDigit()).foregroundStyle(.secondary).frame(width: 40)
        }
    }

    // MARK: Best fits

    @ViewBuilder
    private var suggestions: some View {
        // Clones are never suggested: a real person's voice goes only where it's put.
        let voices = ElevenLabs.shared.castable(VoiceCatalog.voices(for: engine))
        if let c = character {
            let best = VoiceCasting.suggestions(for: c, project: document.project, index: document.index, voices: voices, limit: 3)
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("Best Fits").font(.headline)
                ForEach(best, id: \.voice.id) { item in
                    voiceRow(item.voice)
                }
                narratorRow
            }
        } else if let role {
            // A shared voice: the engine's best voices of that gender (Other: any), not the narrator's.
            let want: VoiceGender = role == .men ? .male : role == .women ? .female : .neutral
            let narratorID = document.project.settings.narratorVoice(for: engine)?.choice.voiceID
            let best = voices.filter { $0.voiceID != narratorID && (role == .other || $0.gender == want) }
                .sorted { ($0.gender == want ? 1 : 0, $0.quality) > ($1.gender == want ? 1 : 0, $1.quality) }
                .prefix(3)
            if !best.isEmpty {
                VStack(alignment: .leading, spacing: Space.sm) {
                    Text("Best Fits").font(.headline)
                    ForEach(Array(best), id: \.id) { voiceRow($0) }
                }
            }
        }
    }

    /// "Same as Narrator": the character speaks in the narrator's voice, whatever it is.
    private var narratorRow: some View {
        let choice = VoiceChoice(engine: engine, voiceID: VoiceChoice.narratorID)
        let isCurrent = voice?.choice.isNarrator == true
        let narrator = VoiceResolver(project: document.project, index: document.index).narrator
        return HStack(spacing: Space.md) {
            Button { audition(choice) } label: {
                Image(systemName: "play.circle").font(.title3)
            }
            .buttonStyle(.plain)
            .help("Hear the narrator’s voice say one of \(character?.firstName ?? "")’s lines")
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: Space.sm) {
                    Image(systemName: "book.closed").foregroundStyle(.secondary)
                    Text("Same as Narrator")
                }
                Text("Uses the narrator’s voice" + (narrator.map { ", now " + VoiceCatalog.name(of: $0) } ?? "") + ", and follows it if it changes")
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer()
            if isCurrent {
                Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
            } else {
                Button("Use") { choose(choice) }
                    .controlSize(.small)
                    .help("Read \(character?.name ?? "")’s lines in the narrator’s voice")
            }
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .background(isCurrent ? Color.accentColor.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 6))
    }

    @ViewBuilder
    private var allVoices: some View {
        let voices = VoiceCatalog.voices(for: engine).filter { (filter == nil || $0.gender == filter) && $0.matches(voiceQuery) }
        VStack(alignment: .leading, spacing: Space.sm) {
            HStack {
                Text(engine == .mixed ? "All Voices" : "All \(engine.title)").font(.headline)
                Spacer()
                Picker("Show", selection: $filter) {
                    Text("All").tag(VoiceGender?.none)
                    ForEach(VoiceGender.allCases, id: \.self) { Text($0.title).tag(VoiceGender?.some($0)) }
                }
                .pickerStyle(.segmented)
                .fixedSize()
                .help("Show voices by how they sound")
            }
            TextField("Find a voice by name, accent or sound", text: $voiceQuery)
                .textFieldStyle(.roundedBorder)
                .help("Find a voice by its name, accent or description")
            if voices.isEmpty, !voiceQuery.isEmpty {
                Text("No voices match “\(voiceQuery)”.").foregroundStyle(.secondary)
            }
            if engine == .mixed {
                // Grouped by engine, so it's clear where each voice comes from.
                ForEach(VoiceEngine.engines.filter { e in voices.contains { $0.engine == e } }) { e in
                    Text(e.title).font(.subheadline.weight(.semibold)).foregroundStyle(.secondary).padding(.top, Space.xs)
                    ForEach(voices.filter { $0.engine == e }) { v in voiceRow(v) }
                }
            } else {
                ForEach(voices) { v in voiceRow(v) }
            }
            if engine == .apple || (engine == .mixed && EngineSwitches.shared.isOn(.apple)) {
                HStack(spacing: Space.sm) {
                    Text("More natural voices are free to download in System Settings.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                    Button("Get More Voices…") {
                        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.universalaccess?SpokenContent")!)
                    }
                    .help("Open System Settings › Accessibility › Spoken Content › System Voice › Manage Voices, and download Premium voices")
                }
                .padding(.top, Space.sm)
            }
        }
    }

    private func voiceRow(_ v: VoiceInfo) -> some View {
        let choice = v.choice
        let isCurrent = voice?.choice.voiceID == v.voiceID && voice?.choice.engine == v.engine
        let users = usedBy(v.voiceID)
        return HStack(spacing: Space.md) {
            Button { audition(choice) } label: {
                Image(systemName: "play.circle").font(.title3)
            }
            .buttonStyle(.plain)
            .help("Hear \(v.name) say one of \(character?.firstName ?? "the narrator")’s lines")
            VStack(alignment: .leading, spacing: 1) {
                HStack(spacing: Space.sm) {
                    Text(v.name)
                    if !v.detail.isEmpty { Text(v.detail).font(.caption).foregroundStyle(.secondary) }
                }
                Text(describe(v) + (users.isEmpty ? "" : " · Also " + users.joined(separator: ", ")))
                    .font(.caption)
                    .foregroundStyle(.secondary)
                    .lineLimit(1)
            }
            Spacer()
            if isCurrent {
                Image(systemName: "checkmark").foregroundStyle(Color.accentColor)
            } else {
                Button("Use") { choose(choice) }
                    .controlSize(.small)
                    .help("Give \(character?.name ?? "the narrator") this voice")
            }
        }
        .padding(.horizontal, Space.sm)
        .padding(.vertical, Space.xs)
        .background(isCurrent ? Color.accentColor.opacity(0.1) : .clear, in: RoundedRectangle(cornerRadius: 6))
    }

    private func describe(_ v: VoiceInfo) -> String {
        [engine == .mixed ? v.engine.title : nil, v.gender == .neutral ? nil : v.gender.title, v.age == .adult ? nil : v.age.title, v.accent]
            .compactMap { $0 }.joined(separator: " · ")
    }

    /// Who else has this voice, so two characters in a scene don't sound alike.
    private func usedBy(_ voiceID: String) -> [String] {
        var names = document.project.characters.filter { $0.id != characterID && $0.voice(for: engine)?.choice.voiceID == voiceID }.map(\.firstName)
        if characterID != nil, document.project.settings.narratorVoice(for: engine)?.choice.voiceID == voiceID { names.insert("the narrator", at: 0) }
        return names
    }

    // MARK: Descriptions

    /// Pocket TTS can't make a voice from words: say so where a description used to go, and
    /// point to what can.
    private var descriptionNote: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Voices from a Description").font(.headline)
            Text(character == nil
                 ? "Pocket TTS can’t make a voice from a description: it speaks in its stock voices and in voices you clone. To get a voice from a description, use Design a Voice from Voice Notes with ElevenLabs (Cloud; set it up in Settings › Voices)."
                 : "Pocket TTS can’t make a voice from a description: it speaks in its stock voices and in voices you clone. \(character!.firstName)’s Voice Notes help casting choose a voice (gender, age, pitch and pace), but they don’t change how a Pocket TTS voice sounds. To get a voice from \(character!.firstName)’s description, use Design a Voice from Voice Notes with ElevenLabs (Cloud; set it up in Settings › Voices).")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
        }
    }

    // MARK: ElevenLabs voices

    /// No ElevenLabs voices yet (the list never loaded, or loading failed): say so and why,
    /// rather than showing a missing voice.
    private var cloudVoicesMissing: some View {
        HStack(alignment: .top, spacing: Space.sm) {
            Image(systemName: "exclamationmark.triangle").foregroundStyle(.orange)
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("ElevenLabs voices haven’t loaded, so there’s no ElevenLabs voice to give.").font(.callout)
                if let cloudVoicesError {
                    Text(cloudVoicesError).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                }
            }
            Spacer(minLength: Space.sm)
            if loadingCloudVoices {
                ProgressView().controlSize(.small)
            } else {
                Button("Load Voices", action: loadCloudVoices)
                    .controlSize(.small)
                    .help("Get the list of voices in your ElevenLabs account (no book text is sent)")
            }
        }
        .padding(Space.md)
        .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        .task { if cloudVoicesError == nil { loadCloudVoices() } }
    }

    private func loadCloudVoices() {
        guard !loadingCloudVoices else { return }
        loadingCloudVoices = true
        Task {
            defer { loadingCloudVoices = false }
            do {
                let found = try await ElevenLabs.shared.refreshVoices()
                cloudVoicesError = found.isEmpty ? "Your ElevenLabs account has no voices. Add some in the Voice Library at elevenlabs.io." : nil
                if !found.isEmpty { VoiceCastingRun.castIfNeeded(document: document) }
            } catch {
                cloudVoicesError = error.localizedDescription
            }
        }
    }

    // MARK: ElevenLabs designs

    /// ElevenLabs can design a voice from a description, for a book that allows ElevenLabs.
    private var showsCloudDesign: Bool { ElevenLabs.cloningOffered(for: document.project) }

    // MARK: Clones

    private func cloneOffer(_ open: @escaping () -> Void) -> some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Text("Clone a Voice").font(.headline)
            Text("Make a voice from a recording of a real person, with their consent: on this Mac with Pocket TTS (free, nothing leaves the Mac), or with ElevenLabs. Every project can use it.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Button("Clone a Voice…", action: open)
                .help("Record or add a sample of a voice and clone it (Book › Clone a Voice…)")
        }
    }

    /// What ElevenLabs is sent: who they are, then their Voice Notes.
    private func cloudDescription(_ c: BookCharacter) -> String {
        let p = VoiceCasting.profile(c, project: document.project, index: document.index)
        return VoiceDescription.design(base: VoiceCasting.describe(p, name: c.name), character: c, limit: ElevenLabs.designLimit)
    }

    private func cloudDesign(_ c: BookCharacter) -> some View {
        let text = cloudDescription(c)
        return VStack(alignment: .leading, spacing: Space.sm) {
            HStack(spacing: Space.sm) {
                Text("Design a Voice from Voice Notes").font(.headline)
                CloudBadge()
            }
            Text("ElevenLabs designs a new voice for \(c.firstName) from this description, which includes \(c.firstName)’s Voice Notes, and adds it to your ElevenLabs voices. The description is sent to ElevenLabs; designing uses credits and one of your plan’s voice slots.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Text(text)
                .font(.callout)
                .textSelection(.enabled)
                .padding(Space.sm)
                .frame(maxWidth: .infinity, alignment: .leading)
                .background(.quaternary.opacity(0.5), in: RoundedRectangle(cornerRadius: 6))
            if cloudDesigning {
                ProgressView {
                    Text("Designing \(c.firstName)’s voice with ElevenLabs…").font(.callout)
                }
                .progressViewStyle(.linear)
            }
            if let cloudDesignError {
                Label(cloudDesignError, systemImage: "exclamationmark.triangle").font(.callout).foregroundStyle(.secondary)
            }
            HStack {
                Button("Design Voice") { designWithElevenLabs(c, description: text) }
                    .disabled(cloudDesigning || text.count < 20)
                    .help("Send \(c.firstName)’s description to ElevenLabs, design a voice from it, and give it to \(c.firstName)")
                Text("Edit Voice Notes to change what’s sent.").font(.caption).foregroundStyle(.secondary)
            }
        }
    }

    private func designWithElevenLabs(_ c: BookCharacter, description: String) {
        let p = VoiceCasting.profile(c, project: document.project, index: document.index)
        var labels: [String: String] = [:]
        if let g = p.gender, g != .neutral { labels["gender"] = g == .female ? "female" : "male" }
        if let a = p.age { labels["age"] = a == .older ? "old" : a == .adult ? "middle_aged" : "young" }
        if let accent = p.accent { labels["accent"] = accent.lowercased() }
        let allowed = document.project.settings.cloudVoicesAllowed != nil
        cloudDesigning = true
        cloudDesignError = nil
        Task {
            defer { cloudDesigning = false }
            do {
                let v = try await ElevenLabs.shared.designVoice(name: "\(c.name) (InnerVoice)", description: description, labels: labels, allowed: allowed)
                document.setVoice(v.choice, for: c.id, undoManager: undoManager)
            } catch is CancellationError {
            } catch {
                cloudDesignError = error.localizedDescription
            }
        }
    }

    // MARK: Actions

    private func choose(_ choice: VoiceChoice) {
        if let role {
            document.setRoleVoice(choice, for: role, undoManager: undoManager)
        } else {
            document.setVoice(choice, for: characterID, undoManager: undoManager)
        }
    }

    private func audition(_ choice: VoiceChoice) {
        PlayCommand.audition(choice, character: sampleCharacter, document: document)
    }
}

/// Play the selected line (or a table read from it), or Stop while something plays.
struct PlayStopButton: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    private var player: SpeechPlayer { SpeechPlayer.shared }

    var body: some View {
        if player.isPlaying {
            Button("Stop Playing", systemImage: "stop.fill") { player.stop() }
                .help("Stop reading aloud (⌥Esc)")
        } else {
            Menu("Play", systemImage: "play.fill") {
                Button("Play Line") { PlayCommand.playLine(document: document, workspace: workspace) }
                    .disabled(workspace.selectedLines.isEmpty)
                Button("Play from Here") { PlayCommand.playFromHere(document: document, workspace: workspace) }
            } primaryAction: {
                if workspace.selectedLines.isEmpty {
                    PlayCommand.playFromHere(document: document, workspace: workspace)
                } else {
                    PlayCommand.playLine(document: document, workspace: workspace)
                }
            }
            .help("Hear the selected line in its character’s voice (⌥Space). Click the arrow for Play from Here, a table read (⌥⇧Space).")
        }
    }
}

/// Asks before a book's text is first sent to ElevenLabs.
struct CloudPermissionAlert: ViewModifier {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    func body(content: Content) -> some View {
        content.alert("Send This Book to ElevenLabs?", isPresented: Binding(get: { workspace.askCloudEngine != nil }, set: { if !$0 { workspace.askCloudEngine = nil } })) {
            Button("Send to ElevenLabs") {
                let e = workspace.askCloudEngine ?? .elevenlabs
                document.setCloudVoicesAllowed(true, undoManager: undoManager)
                document.setVoiceEngine(e, undoManager: undoManager)
                workspace.askCloudEngine = nil
            }
            Button("Cancel", role: .cancel) { workspace.askCloudEngine = nil }
        } message: {
            Text("ElevenLabs makes its voices on its own servers. Every line of “\(document.project.title)” that an ElevenLabs voice speaks is sent there as text; a table read or an exported book sends the whole manuscript. Allow it only if you have the right to share this book. You can stop it at any time on the Voices page.")
        }
    }
}

extension View {
    func cloudPermissionAlert(document: InnerVoiceDocument, workspace: Workspace) -> some View {
        modifier(CloudPermissionAlert(document: document, workspace: workspace))
    }
}

/// On the Voices page while a book uses ElevenLabs: a plain reminder that its text goes to
/// the cloud, and a way to stop. When mixing engines without permission, a way to include it.
struct CloudNotice: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @Environment(\.undoManager) private var undoManager

    var body: some View {
        let project = document.project
        let engine = project.engineInUse
        let allowed = project.settings.cloudVoicesAllowed != nil
        let elevenOn = EngineSwitches.shared.isOn(.elevenlabs)
        if allowed, engine == .elevenlabs || (engine == .mixed && elevenOn) {
            HStack(alignment: .top, spacing: Space.sm) {
                Image(systemName: "icloud.and.arrow.up").foregroundStyle(.orange)
                Text(engine == .mixed
                     ? "ElevenLabs voices are made in the cloud: the lines they speak from this book are sent to ElevenLabs."
                     : "This book uses ElevenLabs, in the cloud: every line its voices speak is sent to ElevenLabs as text.")
                    .font(.callout)
                Spacer(minLength: Space.sm)
                Button("Stop Sending This Book") { document.setCloudVoicesAllowed(false, undoManager: undoManager) }
                    .controlSize(.small)
                    .help("Nothing more from this book goes to ElevenLabs. Its voices switch to an engine on this Mac.")
            }
            .padding(Space.md)
            .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        } else if engine == .mixed, elevenOn, !allowed {
            HStack(alignment: .top, spacing: Space.sm) {
                Image(systemName: "cloud").foregroundStyle(.secondary)
                Text("ElevenLabs voices aren’t in the mix: this book hasn’t allowed its text to be sent to ElevenLabs.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                Spacer(minLength: Space.sm)
                Button("Include ElevenLabs…") { workspace.askCloudEngine = .mixed }
                    .controlSize(.small)
            }
        }
    }
}
