import SwiftUI

/// The Settings window (Command-Comma). One tab per topic.
struct SettingsView: View {
    @AppStorage("settings.tab") private var tab: SettingsTab = .general

    var body: some View {
        TabView(selection: $tab) {
            ForEach(SettingsTab.allCases) { t in
                page(t)
                    .tabItem { Label(t.title, systemImage: t.symbol) }
                    .tag(t)
            }
        }
        .noInitialFocus()
    }

    @ViewBuilder
    private func page(_ tab: SettingsTab) -> some View {
        switch tab {
        case .ai:
            ScrollView {
                ModelPicker().padding(Space.xl)
            }
            .frame(minWidth: 620, idealWidth: 620, maxWidth: .infinity, minHeight: 280, idealHeight: 640, maxHeight: .infinity)
        default:
            Form {
                switch tab {
                case .general: GeneralSettings()
                case .manuscript: ManuscriptSettings()
                case .recording: RecordingSettings()
                case .appearance: AppearanceSettings()
                case .ai: EmptyView()
                }
            }
            .formStyle(.grouped)
            // Settings scroll and can be dragged bigger (house rule).
            .frame(minWidth: 560, idealWidth: 560, maxWidth: .infinity, minHeight: 280, idealHeight: 560, maxHeight: .infinity)
        }
    }
}

enum SettingsTab: String, CaseIterable, Identifiable {
    case general, manuscript, ai, recording, appearance

    var id: String { rawValue }

    var title: String {
        switch self {
        case .general: "General"
        case .manuscript: "Manuscript"
        case .ai: "AI"
        case .recording: "Recording"
        case .appearance: "Accent"
        }
    }

    var symbol: String {
        switch self {
        case .general: "gearshape"
        case .manuscript: "book.pages"
        case .ai: "sparkles"
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
    @AppStorage("reading.fontSize") private var fontSize = 16.0
    @AppStorage("reading.serif") private var serif = true

    var body: some View {
        Section("Reading") {
            Picker("Typeface", selection: $serif) {
                Text("Serif (New York)").tag(true)
                Text("Sans Serif (San Francisco)").tag(false)
            }
            LabeledContent("Text size") {
                HStack {
                    Slider(value: $fontSize, in: 12...26, step: 1)
                        .frame(width: 200)
                    Text("\(Int(fontSize)) pt").monospacedDigit().frame(width: 44, alignment: .trailing)
                }
            }
        }
        Section {
            Text("Highlight or Outline, the Unsure level and initials are saved with each project. Change them in the View menu or the toolbar.")
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
