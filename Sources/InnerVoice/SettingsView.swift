import InnerVoiceKit
import SwiftUI

enum SettingsTab: String, CaseIterable, Identifiable {
    case general, manuscript, ai, voices, recording, appearance

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: "General"
        case .manuscript: "Manuscript"
        case .ai: "AI"
        case .voices: "Voices"
        case .recording: "Recording"
        case .appearance: "Accent"
        }
    }

    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .manuscript: "book.pages"
        case .ai: "sparkles"
        case .voices: "person.wave.2"
        case .recording: "mic"
        case .appearance: "paintpalette"
        }
    }
}

struct GeneralSettings: View {
    var body: some View {
        SystemGeneralSettings()
    }
}

struct ManuscriptSettings: View {
    @AppStorage("reading.fontSize", store: ReadingStyle.store) private var fontSize = ReadingStyle.standardSize
    @AppStorage("reading.serif", store: ReadingStyle.store) private var serif = true
    @AppStorage("reading.lineSpacing", store: ReadingStyle.store) private var spacing = LineSpacing.single.rawValue
    @AppStorage("reading.marginNames", store: ReadingStyle.store) private var marginNames = MarginNames.initials.rawValue

    var body: some View {
        Section("Reading") {
            Picker("Typeface", selection: $serif) {
                Text("Serif (New York)").tag(true)
                Text("Sans Serif (San Francisco)").tag(false)
            }
            LabeledContent("Text size") {
                HStack {
                    Slider(value: $fontSize, in: ReadingStyle.sizes, step: 1)
                        .frame(width: 200)
                    Text("\(Int(fontSize)) pt").monospacedDigit().frame(width: 44, alignment: .trailing)
                }
            }
            Picker("Line spacing", selection: $spacing) {
                ForEach(LineSpacing.allCases) { Text($0.title).tag($0.rawValue) }
            }
            .pickerStyle(.segmented)
        }
        Section {
            Picker("Speakers in the margin", selection: $marginNames) {
                ForEach(MarginNames.allCases) { Text($0.title).tag($0.rawValue) }
            }
            .pickerStyle(.segmented)
        } footer: {
            Text("Initials take the least room; long names are shortened with “…”. To hide them in a project, turn off View › Show Speakers in Margin.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        Section {
            Text("Highlight, Outline or Underline, the Unsure level and showing speakers in the margin are saved with each project. Change them in the View menu or the toolbar.")
                .font(.callout)
                .foregroundStyle(.secondary)
        }
    }
}

struct RecordingSettings: View {
    @AppStorage("record.fontSize") private var fontSize = 30.0
    @AppStorage("record.markOnAdvance") private var markOnAdvance = true
    @AppStorage("record.nextKey") private var nextKey = "space"

    var body: some View {
        Section("Record") {
            LabeledContent("Text size") {
                HStack {
                    Slider(value: $fontSize, in: 18...64, step: 2).frame(width: 200)
                    Text("\(Int(fontSize)) pt").monospacedDigit().frame(width: 44, alignment: .trailing)
                }
            }
            Toggle("Mark a paragraph recorded when you move on", isOn: $markOnAdvance)
        }
        Section {
            Picker("Next paragraph", selection: $nextKey) {
                Text("Space").tag("space")
                Text("Down Arrow").tag("down")
                Text("Page Down").tag("pagedown")
                Text("Return").tag("return")
            }
            LabeledContent("Previous paragraph") { Text("Left Arrow, Up Arrow or Page Up").foregroundStyle(.secondary) }
            LabeledContent("Mark retake") { Text("R").foregroundStyle(.secondary) }
            LabeledContent("Mark recorded") { Text("M").foregroundStyle(.secondary) }
        } header: {
            Text("Keys")
        } footer: {
            Text("Most foot pedals send Page Down or an arrow key. Set your pedal to one of these, then pick it here. Right Arrow always moves on.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
    }
}

/// A downloadable speech engine: what it does, its size, and Download, progress or Remove.
struct VoicePackRow: View {
    let pack: VoicePack
    private var manager: VoiceModelManager { .shared }

    var body: some View {
        LabeledContent {
            HStack(spacing: Space.sm) {
                if EngineSwitches.shared.isOn(pack.engine) || manager.states[pack.engine] != nil { controls }
                EngineSwitch(engine: pack.engine)
            }
        } label: {
            Text(pack.engine.title)
            Text(pack.summary)
        }
        .foregroundStyle(EngineSwitches.shared.isOn(pack.engine) ? .primary : .secondary)
    }

    @ViewBuilder
    private var controls: some View {
            switch manager.states[pack.engine] {
            case .downloading(let p)?:
                HStack(spacing: Space.sm) {
                    ProgressView(value: p).frame(width: 120)
                    Text("\(Int(p * 100))% of \(pack.sizeDescription)").font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                    Button("Cancel") { manager.cancel(pack.engine) }.controlSize(.small)
                }
            case .failed(let message)?:
                HStack(spacing: Space.sm) {
                    Text(message).font(.caption).foregroundStyle(.orange).lineLimit(2)
                    Button("Download") { manager.download(pack.engine) }.controlSize(.small)
                }
            case nil:
                if manager.isInstalled(pack.engine) {
                    HStack(spacing: Space.sm) {
                        Label("Installed", systemImage: "checkmark.circle.fill").labelStyle(.titleAndIcon).foregroundStyle(.secondary)
                        Button("Remove") { manager.remove(pack.engine) }
                            .controlSize(.small)
                            .help("Move the \(pack.engine.title) models to the Trash (\(pack.sizeDescription))")
                    }
                } else {
                    Button("Download (\(pack.sizeDescription))") { manager.download(pack.engine) }
                        .help("Download \(pack.engine.title) from Hugging Face")
                }
            }
    }
}

/// Turns an engine on or off (the last one that's on stays on).
struct EngineSwitch: View {
    let engine: VoiceEngine
    private var switches: EngineSwitches { .shared }

    var body: some View {
        Toggle("Use \(engine.title)", isOn: switches.binding(engine))
            .labelsHidden()
            .toggleStyle(.switch)
            .controlSize(.small)
            .disabled(switches.isOn(engine) && !switches.canTurnOff(engine))
            .help(switches.isOn(engine)
                  ? (switches.canTurnOff(engine) ? "Turn \(engine.title) off: it won’t be offered on the Voices page" : "At least one engine stays on")
                  : "Turn \(engine.title) on")
    }
}

struct VoicesSettings: View {
    @AppStorage("voices.readNarration") private var readNarration = true
    @AppStorage("voices.followAlong") private var followAlong = true
    @AppStorage("voices.writtenByNarrator") private var writtenByNarrator = true
    @State private var cacheSize = 0

    var body: some View {
        Section {
            Picker("Play from Here reads", selection: $readNarration) {
                Text("Narration and dialogue").tag(true)
                Text("Dialogue only").tag(false)
            }
            .pickerStyle(.radioGroup)
            Toggle("Follow along in the manuscript", isOn: $followAlong)
            Toggle("Read written text in the narrator’s voice", isOn: $writtenByNarrator)
                .help("Letters, notes and signs a character wrote are read by the narrator. Off: by the character who wrote them.")
        } header: {
            Text("Table Read")
        } footer: {
            Text("Narration is read in the narrator’s voice, chosen on the Voices page; in a first-person book, the narrator character’s voice. Export Audio asks each time.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        Section {
            LabeledContent("Apple Voices") {
                HStack {
                    if EngineSwitches.shared.isOn(.apple) {
                        Text("Built in").foregroundStyle(.secondary)
                        Button("Get More Voices…") {
                            NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.universalaccess?SpokenContent")!)
                        }
                        .help("Download free Premium voices in System Settings › Accessibility › Spoken Content › System Voice › Manage Voices")
                    }
                    EngineSwitch(engine: .apple)
                }
            }
            VoicePackRow(pack: .qwen)
            VoicePackRow(pack: .orpheus)
        } header: {
            Text("Engines")
        } footer: {
            Text("Qwen3-TTS and Orpheus run on this Mac. Downloading them is the only time they use the internet; your book never leaves the Mac. Choose the engine for each project on its Voices page; turn an engine off to hide it there.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        Section {
            LabeledContent("Saved audio") {
                HStack {
                    Text(ByteCountFormatter.string(fromByteCount: Int64(cacheSize), countStyle: .file)).foregroundStyle(.secondary)
                    Button("Clear") {
                        SpeechRenderer.shared.clearCache()
                        cacheSize = SpeechRenderer.shared.cacheSize
                    }
                    .disabled(cacheSize == 0)
                    .help("Remove the saved audio. Lines are made again the next time they play.")
                }
            }
        } footer: {
            Text("Lines are saved after they’re first made, so they play at once next time. The oldest go when it passes 1 GB.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        .onAppear { cacheSize = SpeechRenderer.shared.cacheSize }
    }
}
