import AppKit
import InnerVoiceKit
import SwiftUI

/// Speechify in Settings › Voices: marked Cloud, with Set Up… until it has a key and the
/// narrator's yes.
struct SpeechifyRow: View {
    @State private var showSetup = false
    private var speechify: SpeechifyVoices { .shared }

    var body: some View {
        LabeledContent {
            HStack(spacing: Space.sm) {
                if speechify.isSetUp {
                    Label("Connected", systemImage: "checkmark.circle.fill").foregroundStyle(.secondary)
                    Button("Change…") { showSetup = true }.controlSize(.small)
                } else {
                    Button("Set Up…") { showSetup = true }
                        .help("Connect your Speechify API key. Its voices are made in Speechify’s cloud.")
                }
            }
        } label: {
            HStack(spacing: Space.xs) {
                Text("Speechify")
                CloudBadge(service: "Speechify")
            }
            Text("Speechify’s voices, made on its servers. The text they speak is sent to Speechify and counted against your Speechify API plan (the free plan includes a monthly allowance of characters). Tones change only the pace, pitch and loudness.")
        }
        .sheet(isPresented: $showSetup) { SpeechifySetupSheet(isPresented: $showSetup) }
    }
}

struct SpeechifySetupSheet: View {
    @Binding var isPresented: Bool
    @State private var key = ""
    @State private var agreed = false
    @State private var checking = false
    @State private var checked = false
    @State private var error: String?
    private var speechify: SpeechifyVoices { .shared }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            HStack(spacing: Space.sm) {
                Text("Set Up Speechify").font(.title2.weight(.semibold))
                CloudBadge(service: "Speechify")
            }
            warning
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("1. Get an API key").font(.headline)
                Text("On the Speechify API platform, sign in, create an API key, then copy it. The API is a separate plan from the Speechify reading app: a Speechify Premium subscription doesn’t include it.")
                    .font(.callout).foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Open Speechify API Keys") { NSWorkspace.shared.open(SpeechifyVoices.keysPage) }
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
                    Label("Connected: Speechify accepted the key", systemImage: "checkmark.circle.fill")
                        .font(.callout).foregroundStyle(.green)
                } else if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill").font(.callout).foregroundStyle(.orange)
                }
                Text("The key is kept in your Mac’s keychain.").font(.caption).foregroundStyle(.secondary)
            }
            Toggle(isOn: $agreed) {
                Text("I understand that when I use Speechify voices, the text they speak from my books is sent to Speechify.")
                    .fixedSize(horizontal: false, vertical: true)
            }
            .toggleStyle(.checkbox)
            HStack {
                if speechify.isSetUp {
                    Button("Disconnect", role: .destructive) {
                        speechify.disconnect()
                        isPresented = false
                    }
                    .help("Forget the key. Nothing more is sent to Speechify.")
                }
                HelpButton(topic: .voices)
                Spacer()
                Button("Cancel") { isPresented = false }.keyboardShortcut(.cancelAction)
                Button("Turn On Speechify") { turnOn() }
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
            key = speechify.apiKey ?? ""
            agreed = speechify.consented != nil
            if !key.isEmpty { check() }
        }
    }

    private var warning: some View {
        VStack(alignment: .leading, spacing: Space.sm) {
            Label("Speechify sends your book to Speechify", systemImage: "icloud.and.arrow.up")
                .font(.headline)
            Text("Everything else in InnerVoice happens on this Mac. Speechify doesn’t: its voices are made on Speechify’s servers. Every line you play or export with a Speechify voice is sent there as text, so a table read or an exported book sends the whole manuscript to Speechify, under its API terms.")
                .font(.callout)
                .fixedSize(horizontal: false, vertical: true)
            Text("Each book asks before its text is sent the first time, separately from ElevenLabs and Google. Use it only for books you have the right to share.")
                .font(.callout)
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            Link("Speechify API Terms", destination: SpeechifyVoices.termsPage).font(.callout)
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
            do { try await speechify.check(key: k); checked = true } catch { self.error = error.localizedDescription; checked = false }
        }
    }

    private func turnOn() {
        speechify.save(key: key, consent: true)
        EngineSwitches.shared.set(.speechify, on: true)
        isPresented = false
    }
}
