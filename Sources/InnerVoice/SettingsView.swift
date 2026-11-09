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
            ElevenLabsRow()
        } header: {
            Text("Engines")
        } footer: {
            Text("Apple Voices, Qwen3-TTS and Orpheus run on this Mac; downloading them is the only time they use the internet. ElevenLabs is different: it makes its voices in the cloud, so the text it speaks is sent to ElevenLabs, and each book asks first. Choose the engine for each project on its Voices page; turn an engine off to hide it there.")
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

/// ElevenLabs in Settings › Voices: marked Cloud, with Set Up… until it has a key and the
/// narrator's yes.
struct ElevenLabsRow: View {
    @State private var showSetup = false
    private var eleven: ElevenLabs { .shared }

    var body: some View {
        LabeledContent {
            HStack(spacing: Space.sm) {
                if eleven.isSetUp {
                    Label("Connected", systemImage: "checkmark.circle.fill").foregroundStyle(.secondary)
                    Button("Change…") { showSetup = true }.controlSize(.small)
                } else {
                    Button("Set Up…") { showSetup = true }
                        .help("Connect your ElevenLabs account. Its voices are made in the cloud.")
                }
                EngineSwitch(engine: .elevenlabs).disabled(!eleven.isSetUp)
            }
        } label: {
            HStack(spacing: Space.xs) {
                Text("ElevenLabs")
                CloudBadge()
            }
            Text("Natural voices made on ElevenLabs’ servers. The text they speak is sent to ElevenLabs, and uses your plan’s credits.")
        }
        .sheet(isPresented: $showSetup) { ElevenLabsSetupSheet(isPresented: $showSetup) }
    }
}

/// "Cloud", wherever ElevenLabs is named: the one place the book's text leaves the Mac.
struct CloudBadge: View {
    var body: some View {
        Label("Cloud", systemImage: "cloud")
            .font(.caption2.weight(.semibold))
            .labelStyle(.titleAndIcon)
            .padding(.horizontal, 6)
            .padding(.vertical, 2)
            .foregroundStyle(.orange)
            .background(.orange.opacity(0.12), in: Capsule())
            .help("Uses the internet: text is sent to ElevenLabs")
    }
}

/// Setting up ElevenLabs: what it means for the book's privacy, getting a key, checking it,
/// and the narrator's yes. Nothing from a book is sent here; checking the key reads only the
/// account's plan.
struct ElevenLabsSetupSheet: View {
    @Binding var isPresented: Bool
    @State private var key = ""
    @State private var agreed = false
    @State private var checking = false
    @State private var account: ElevenLabs.Account?
    @State private var error: String?
    private var eleven: ElevenLabs { .shared }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            HStack(spacing: Space.sm) {
                Text("Set Up ElevenLabs").font(.title2.weight(.semibold))
                CloudBadge()
            }
            warning
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("1. Get an API key").font(.headline)
                Text("In ElevenLabs, create an API key (Text to Speech, and read access to Voices and User), then copy it. A free account works to try it.")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    Button("Open API Keys Page") { NSWorkspace.shared.open(ElevenLabs.keysPage) }
                    Button("Create a Free Account") { NSWorkspace.shared.open(ElevenLabs.signUpPage) }
                }
            }
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("2. Paste it here").font(.headline)
                HStack {
                    SecureField("API key", text: $key)
                        .textFieldStyle(.roundedBorder)
                        .onChange(of: key) { account = nil; error = nil }
                    Button("Check Key", action: check)
                        .disabled(key.trimmingCharacters(in: .whitespaces).isEmpty || checking)
                }
                if checking {
                    HStack(spacing: Space.sm) { ProgressView().controlSize(.small); Text("Checking…").font(.callout).foregroundStyle(.secondary) }
                } else if let account {
                    Label(accountText(account), systemImage: "checkmark.circle.fill")
                        .font(.callout).foregroundStyle(.green)
                } else if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill").font(.callout).foregroundStyle(.orange)
                }
                Text("The key is kept in your Mac’s keychain.").font(.caption).foregroundStyle(.secondary)
            }
            Toggle(isOn: $agreed) {
                Text("I understand that when I use ElevenLabs voices, the text they speak from my books is sent to ElevenLabs.")
                    .fixedSize(horizontal: false, vertical: true)
            }
            .toggleStyle(.checkbox)
            HStack {
                if eleven.isSetUp {
                    Button("Disconnect", role: .destructive) {
                        eleven.disconnect()
                        isPresented = false
                    }
                    .help("Forget the key. Nothing more is sent to ElevenLabs.")
                }
                HelpButton(topic: .voices)
                Spacer()
                Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Turn On ElevenLabs") { turnOn() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .disabled(account == nil || !agreed)
            }
        }
        .padding(Space.xl)
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
        .noInitialFocus()
        .onAppear {
            key = eleven.apiKey ?? ""
            agreed = eleven.consented != nil
            if !key.isEmpty { check() }
        }
    }

    private var warning: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Label("ElevenLabs sends your book to the cloud", systemImage: "icloud.and.arrow.up")
                .font(.headline)
            Text("Everything else in InnerVoice happens on this Mac. ElevenLabs doesn’t: its voices are made on ElevenLabs’ servers. Every line you play or export with an ElevenLabs voice is sent there as text, so a table read or an exported book sends the whole manuscript to ElevenLabs, a third party, under its own terms.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Text("Each book asks before its text is sent the first time. Use it only for books you have the right to share.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Link("ElevenLabs Privacy Policy", destination: ElevenLabs.privacyPage).font(.callout)
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.orange.opacity(0.35)))
    }

    private func accountText(_ a: ElevenLabs.Account) -> String {
        let plan = a.tier.isEmpty ? "Connected" : "Connected: \(a.tier.replacingOccurrences(of: "_", with: " ").capitalized) plan"
        guard a.limit > 0 else { return plan }
        return plan + ", \(a.used.formatted()) of \(a.limit.formatted()) credits used this month"
    }

    private func check() {
        checking = true
        error = nil
        let k = key
        Task {
            defer { checking = false }
            do { account = try await eleven.check(key: k) } catch { self.error = error.localizedDescription; account = nil }
        }
    }

    private func turnOn() {
        eleven.save(key: key, consent: true)
        EngineSwitches.shared.set(.elevenlabs, on: true)
        isPresented = false
        Task { try? await eleven.refreshVoices() }
    }
}
