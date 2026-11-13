import AppKit
import InnerVoiceKit
import SwiftUI

/// Claude in Settings › AI: marked Cloud, with Set Up… until it has a key and the narrator's
/// yes, then a model menu.
struct ClaudeModelRow: View {
    var manager: ModelManager
    @State private var account = ClaudeAccount.shared
    @State private var settingUp = false
    @State private var modelID = ClaudeAccount.defaultModelID

    var body: some View {
        let selected = manager.selection.hasPrefix("claude:")
        HStack(alignment: .top, spacing: Space.md) {
            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .font(.title3)
                .foregroundStyle(selected ? AnyShapeStyle(.tint) : AnyShapeStyle(.tertiary))
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: Space.xs) {
                HStack {
                    Text("Claude").font(.headline)
                    CloudBadge(service: "Anthropic")
                }
                Text("Anthropic's models, run in the cloud. The most accurate choice and nothing to download, but each passage of the book is sent to Anthropic, it needs an API key, and Anthropic charges for what you use. Each book asks before anything is sent.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    Meter(label: "Accuracy", value: 5)
                    Meter(label: "Speed", value: 4)
                    Spacer()
                    if account.isSetUp {
                        Picker("Model", selection: $modelID) {
                            ForEach(account.models, id: \.id) { Text($0.name).tag($0.id) }
                        }
                        .labelsHidden()
                        .fixedSize()
                        .controlSize(.small)
                        .onChange(of: modelID) { _, id in if selected { manager.selection = "claude:" + id } }
                        if !selected { Button("Use Claude") { manager.selection = "claude:" + modelID }.controlSize(.small) }
                        Button("Disconnect") { account.disconnect() }.controlSize(.small)
                    } else {
                        Button("Set Up…") { settingUp = true }.controlSize(.small)
                    }
                }
            }
        }
        .padding(Space.md)
        .background(selected ? AnyShapeStyle(.tint.opacity(0.08)) : AnyShapeStyle(.quaternary.opacity(0.35)), in: RoundedRectangle(cornerRadius: 10))
        .onAppear {
            if selected { modelID = String(manager.selection.dropFirst(7)) }
            if account.isSetUp { Task { try? await account.refreshModels() } }
        }
        .sheet(isPresented: $settingUp) { ClaudeSetupSheet(manager: manager) }
    }
}

/// Setting up Claude: the cloud warning, the key, and the narrator's yes. Checking the key
/// lists models only; no book text is sent.
struct ClaudeSetupSheet: View {
    var manager: ModelManager
    @Environment(\.dismiss) private var dismiss
    @State private var key = ""
    @State private var agreed = false
    @State private var checking = false
    @State private var problem: String?

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            HStack {
                Text("Set Up Claude").font(.title2.weight(.semibold))
                CloudBadge(service: "Anthropic")
            }
            Label {
                VStack(alignment: .leading, spacing: Space.xs) {
                    Text(ClaudeAccount.warning).fixedSize(horizontal: false, vertical: true)
                    Link("How to check", destination: ClaudeAccount.privacyHelp)
                }
            } icon: {
                Image(systemName: "exclamationmark.icloud").foregroundStyle(.orange)
            }
            .padding(Space.md)
            .background(.orange.opacity(0.08), in: RoundedRectangle(cornerRadius: 8))

            VStack(alignment: .leading, spacing: Space.xs) {
                Text("API key").font(.headline)
                SecureField("sk-ant-…", text: $key).textFieldStyle(.roundedBorder)
                Link("Get a key in the Claude Console", destination: ClaudeAccount.keysPage).font(.callout)
            }
            Toggle("I understand that book text I send to Claude goes to Anthropic.", isOn: $agreed)
            if let problem { Text(problem).font(.callout).foregroundStyle(.red) }
            HStack {
                Spacer()
                Button("Cancel") { dismiss() }.keyboardShortcut(.cancelAction)
                Button(checking ? "Checking…" : "Connect") { connect() }
                    .disabled(key.trimmingCharacters(in: .whitespaces).isEmpty || !agreed || checking)
            }
        }
        .padding(Space.xl)
        .frame(width: 480)
    }

    private func connect() {
        checking = true
        problem = nil
        let account = ClaudeAccount.shared
        account.save(key: key, consent: true)
        Task {
            do {
                try await account.refreshModels()
                if !manager.selection.hasPrefix("claude:") { manager.selection = "claude:" + ClaudeAccount.defaultModelID }
                dismiss()
            } catch {
                account.save(key: "", consent: false)
                problem = error.localizedDescription
            }
            checking = false
        }
    }
}
