import AppKit
import InnerVoiceKit
import SwiftUI

/// Gemini TTS in Settings › Voices: marked Cloud, with Set Up… until it has a key and the
/// narrator's yes, and the model choice once it's on.
struct GeminiRow: View {
    @State private var showSetup = false
    private var gemini: GeminiVoices { .shared }

    var body: some View {
        LabeledContent {
            HStack(spacing: Space.sm) {
                if gemini.isSetUp {
                    Label("Connected", systemImage: "checkmark.circle.fill").foregroundStyle(.secondary)
                    Button("Change…") { showSetup = true }.controlSize(.small)
                } else {
                    Button("Set Up…") { showSetup = true }
                        .help("Connect your Gemini API key. Its voices are made in Google’s cloud.")
                }
                EngineSwitch(engine: .gemini).disabled(!gemini.isSetUp)
            }
        } label: {
            HStack(spacing: Space.xs) {
                Text("Gemini TTS")
                CloudBadge(service: "Google")
            }
            Text("Thirty voices made on Google’s servers, directed in plain words for each tone. The text they speak is sent to Google and billed to your Gemini API key (about $8 for a 10-hour book with Flash in 2026).")
        }
        .sheet(isPresented: $showSetup) { GeminiSetupSheet(isPresented: $showSetup) }
        if gemini.isSetUp, EngineSwitches.shared.isOn(.gemini) {
            Picker("Gemini model", selection: Binding(get: { gemini.model }, set: { gemini.model = $0 })) {
                ForEach(GeminiTTS.Model.allCases) { Text($0.title).tag($0) }
            }
            .help("Flash acts the tones best. Flash-Lite costs about two thirds as much.")
        }
    }
}

struct GeminiSetupSheet: View {
    @Binding var isPresented: Bool
    @State private var key = ""
    @State private var agreed = false
    @State private var checking = false
    @State private var checked = false
    @State private var error: String?
    private var gemini: GeminiVoices { .shared }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            HStack(spacing: Space.sm) {
                Text("Set Up Gemini TTS").font(.title2.weight(.semibold))
                CloudBadge(service: "Google")
            }
            warning
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("1. Get an API key").font(.headline)
                Text("In Google AI Studio, create a Gemini API key, then copy it. Turn on billing for its project: on the free tier Google may use what you send to improve its products, and the free limits are too small for a book.")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Open Google AI Studio") { NSWorkspace.shared.open(GeminiVoices.keysPage) }
            }
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("2. Paste it here").font(.headline)
                HStack {
                    SecureField("API key", text: $key)
                        .textFieldStyle(.roundedBorder)
                        .onChange(of: key) { checked = false; error = nil }
                    Button("Check Key", action: check)
                        .disabled(key.trimmingCharacters(in: .whitespaces).isEmpty || checking)
                }
                if checking {
                    HStack(spacing: Space.sm) { ProgressView().controlSize(.small); Text("Checking…").font(.callout).foregroundStyle(.secondary) }
                } else if checked {
                    Label("Connected: Google accepted the key", systemImage: "checkmark.circle.fill")
                        .font(.callout).foregroundStyle(.green)
                } else if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill").font(.callout).foregroundStyle(.orange)
                }
                Text("The key is kept in your Mac’s keychain.").font(.caption).foregroundStyle(.secondary)
            }
            Toggle(isOn: $agreed) {
                Text("I understand that when I use Gemini TTS voices, the text they speak from my books is sent to Google.")
                    .fixedSize(horizontal: false, vertical: true)
            }
            .toggleStyle(.checkbox)
            HStack {
                if gemini.isSetUp {
                    Button("Disconnect", role: .destructive) {
                        gemini.disconnect()
                        isPresented = false
                    }
                    .help("Forget the key. Nothing more is sent to Google.")
                }
                HelpButton(topic: .voices)
                Spacer()
                Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Turn On Gemini TTS") { turnOn() }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .disabled(!checked || !agreed)
            }
        }
        .padding(Space.xl)
        .frame(width: 560)
        .fixedSize(horizontal: false, vertical: true)
        .noInitialFocus()
        .onAppear {
            key = gemini.apiKey ?? ""
            agreed = gemini.consented != nil
            if !key.isEmpty { check() }
        }
    }

    private var warning: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Label("Gemini TTS sends your book to Google", systemImage: "icloud.and.arrow.up")
                .font(.headline)
            Text("Everything else in InnerVoice happens on this Mac. Gemini TTS doesn’t: its voices are made on Google’s servers. Every line you play or export with a Gemini voice is sent there as text, so a table read or an exported book sends the whole manuscript to Google, under the Gemini API terms.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Text("Each book asks before its text is sent the first time, separately from ElevenLabs. Use it only for books you have the right to share.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Link("Gemini API Terms", destination: GeminiVoices.privacyPage).font(.callout)
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.orange.opacity(0.1), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(.orange.opacity(0.35)))
    }

    private func check() {
        checking = true
        error = nil
        let k = key
        Task {
            defer { checking = false }
            do { try await gemini.check(key: k); checked = true } catch { self.error = error.localizedDescription; checked = false }
        }
    }

    private func turnOn() {
        gemini.save(key: key, consent: true)
        EngineSwitches.shared.set(.gemini, on: true)
        isPresented = false
    }
}
