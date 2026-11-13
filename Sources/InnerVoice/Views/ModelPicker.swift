import AppKit
import InnerVoiceKit
import SwiftUI

/// Choose and download the on-device AI. Recommendations come from this Mac's memory,
/// and every model says why it fits or not.
struct ModelPicker: View {
    @State private var manager = ModelManager.shared
    @State private var showAll = false

    var body: some View {
        let mem = ModelManager.memoryGB
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(alignment: .top, spacing: Space.md) {
                Image(systemName: "memorychip")
                    .font(.title2)
                    .foregroundStyle(.tint)
                VStack(alignment: .leading, spacing: 2) {
                    Text("This Mac has \(mem) GB of memory.").font(.headline)
                    Text("With these models your manuscript never leaves this Mac. Only the model itself is downloaded, once, from Hugging Face. Claude is different: it runs in the cloud, and each book asks first.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
            let sorted = ModelCatalog.all.sorted { a, b in
                let fa = ModelCatalog.advice(for: a, memoryGB: mem).fit, fb = ModelCatalog.advice(for: b, memoryGB: mem).fit
                if fa != fb { return fa < fb }
                return a.quality == b.quality ? a.speed > b.speed : a.quality > b.quality
            }
            let visible = showAll ? sorted : sorted.filter { ModelCatalog.advice(for: $0, memoryGB: mem).fit != .tooLarge }
            VStack(spacing: Space.sm) {
                ForEach(visible) { m in ModelRow(model: m, manager: manager) }
                AppleModelRow(manager: manager)
                ClaudeModelRow(manager: manager)
            }
            HStack {
                if sorted.count != visible.count || showAll {
                    Button(showAll ? "Hide Models Too Large for This Mac" : "Show Models Too Large for This Mac") { showAll.toggle() }
                        .buttonStyle(.link)
                }
                Spacer()
                HelpButton(topic: .choosingModel)
                Button("Choose a Model File…") { manager.chooseFile() }
                    .help("Use a GGUF model you already have")
            }
            .font(.callout)
            if manager.selection.hasPrefix("file:") {
                Label("Using \(manager.selectionName) (your own file)", systemImage: "checkmark.circle.fill")
                    .font(.callout)
                    .foregroundStyle(.green)
            }
        }
    }
}

struct ModelRow: View {
    let model: CatalogModel
    var manager: ModelManager

    var body: some View {
        let advice = ModelCatalog.advice(for: model, memoryGB: ModelManager.memoryGB)
        let selected = manager.selection == "catalog:" + model.id
        let isDownloaded = manager.downloaded.contains(model.id)
        HStack(alignment: .top, spacing: Space.md) {
            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .font(.title3)
                .foregroundStyle(selected ? AnyShapeStyle(.tint) : AnyShapeStyle(.tertiary))
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: Space.xs) {
                HStack(spacing: Space.sm) {
                    Text(model.name).font(.headline)
                    FitBadge(fit: advice.fit)
                    Spacer()
                    Text(model.sizeDescription).font(.caption.monospacedDigit()).foregroundStyle(.secondary)
                }
                Text(model.blurb).font(.callout)
                Text(advice.note)
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                // How it did on this person's own books (Settings › Activity), once there's enough to go on.
                if let acc = ModelAccuracy.all(in: ActivityStore.shared.combined(allMacs: true).lifetime).first(where: { $0.model == model.name }), acc.reviewed >= 10 {
                    Label("On your books: \(Int((acc.rate * 100).rounded()))% of \(acc.reviewed.formatted()) checked guesses kept", systemImage: "chart.bar")
                        .font(.callout)
                        .foregroundStyle(Palette.accentText)
                }
                HStack(spacing: Space.lg) {
                    Meter(label: "Accuracy", value: model.quality)
                    Meter(label: "Speed", value: model.speed)
                    Spacer()
                    actions(selected: selected, isDownloaded: isDownloaded)
                }
                .padding(.top, 2)
            }
        }
        .padding(Space.md)
        .background(selected ? AnyShapeStyle(.tint.opacity(0.08)) : AnyShapeStyle(.quaternary.opacity(0.35)), in: RoundedRectangle(cornerRadius: 10))
        .overlay(RoundedRectangle(cornerRadius: 10).strokeBorder(selected ? AnyShapeStyle(.tint.opacity(0.5)) : AnyShapeStyle(.clear)))
    }

    @ViewBuilder
    private func actions(selected: Bool, isDownloaded: Bool) -> some View {
        switch manager.downloads[model.id] {
        case .downloading(let p, let bytes)?:
            HStack(spacing: Space.sm) {
                ProgressView(value: p).frame(width: 120)
                Text("\(ByteCountFormatter.string(fromByteCount: bytes, countStyle: .file)) of \(model.sizeDescription)")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                Button("Cancel") { manager.cancelDownload(model) }.controlSize(.small)
            }
        case .verifying?:
            HStack(spacing: Space.sm) {
                ProgressView().controlSize(.small)
                Text("Checking the download…").font(.caption).foregroundStyle(.secondary)
            }
        case .failed(let message)?:
            HStack(spacing: Space.sm) {
                Text(message).font(.caption).foregroundStyle(.orange).lineLimit(2)
                Button("Download") { manager.download(model) }.controlSize(.small)
            }
        case nil:
            if isDownloaded {
                HStack(spacing: Space.sm) {
                    if !selected {
                        Button("Use This Model") { manager.selection = "catalog:" + model.id }.controlSize(.small)
                    }
                    Menu {
                        Button("Move to Trash", role: .destructive) { manager.delete(model) }
                    } label: { Image(systemName: "ellipsis.circle") }
                        .menuStyle(.borderlessButton)
                        .fixedSize()
                        .help("Remove the downloaded model")
                }
            } else {
                Button("Download") { manager.download(model) }
                    .controlSize(.small)
                    .help("Download \(model.sizeDescription) from Hugging Face")
            }
        }
    }
}

struct AppleModelRow: View {
    var manager: ModelManager

    var body: some View {
        let availability = AppleTextModel.availability
        let selected = manager.selection == "apple"
        HStack(alignment: .top, spacing: Space.md) {
            Image(systemName: selected ? "checkmark.circle.fill" : "circle")
                .font(.title3)
                .foregroundStyle(selected ? AnyShapeStyle(.tint) : AnyShapeStyle(.tertiary))
                .padding(.top, 2)
            VStack(alignment: .leading, spacing: Space.xs) {
                HStack {
                    Text("Apple On-Device Model").font(.headline)
                    Text("No download").font(.caption.weight(.semibold)).foregroundStyle(.secondary)
                        .padding(.horizontal, 6).padding(.vertical, 1)
                        .background(.quaternary, in: Capsule())
                }
                Text("Built into macOS with Apple Intelligence. Quick to start, but it reads shorter passages and gets more lines wrong than the downloaded models.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .fixedSize(horizontal: false, vertical: true)
                HStack {
                    Meter(label: "Accuracy", value: 2)
                    Meter(label: "Speed", value: 4)
                    Spacer()
                    if availability.ok {
                        if !selected { Button("Use This Model") { manager.selection = "apple" }.controlSize(.small) }
                    } else {
                        Text(availability.why).font(.caption).foregroundStyle(.secondary)
                    }
                }
            }
        }
        .padding(Space.md)
        .background(selected ? AnyShapeStyle(.tint.opacity(0.08)) : AnyShapeStyle(.quaternary.opacity(0.35)), in: RoundedRectangle(cornerRadius: 10))
    }
}

struct FitBadge: View {
    let fit: ModelAdvice.Fit
    var body: some View {
        let (symbol, tint): (String, Color) = switch fit {
        case .recommended: ("star.fill", .accentColor)
        case .good: ("checkmark", .green)
        case .tight: ("exclamationmark.triangle.fill", .orange)
        case .tooLarge: ("xmark.octagon.fill", Color(nsColor: .attention))
        }
        Label(fit.title, systemImage: symbol)
            .font(.caption.weight(.semibold))
            .foregroundStyle(tint)
            .padding(.horizontal, 6)
            .padding(.vertical, 1)
            .background(tint.opacity(0.12), in: Capsule())
    }
}

struct Meter: View {
    let label: String
    let value: Int
    var body: some View {
        HStack(spacing: Space.xs) {
            Text(label).font(.caption).foregroundStyle(.secondary)
            HStack(spacing: 2) {
                ForEach(1...5, id: \.self) { i in
                    Capsule().fill(i <= value ? AnyShapeStyle(.tint) : AnyShapeStyle(.quaternary)).frame(width: 10, height: 4)
                }
            }
        }
        .accessibilityElement(children: .ignore)
        .accessibilityLabel("\(label) \(value) of 5")
    }
}

/// Shown the first time Tag Speakers runs without a model.
struct ModelSheet: View {
    var done: (Bool) -> Void
    @State private var manager = ModelManager.shared

    var body: some View {
        VStack(alignment: .leading, spacing: Space.lg) {
            Text("Choose a Model for Tag Speakers").font(.title2.weight(.semibold))
            Text("InnerVoice reads your book with an AI model that runs on this Mac. Pick one to download; the one marked Best for this Mac balances accuracy and speed for your memory.")
                .foregroundStyle(.secondary)
                .fixedSize(horizontal: false, vertical: true)
            ScrollView {
                ModelPicker().padding(.vertical, Space.xs)
            }
            .frame(minHeight: 360, maxHeight: 520)
            HStack {
                Spacer()
                Button("Cancel") { done(false) }.keyboardShortcut(.cancelAction)
                Button("Tag Speakers") { done(true) }
                    .keyboardShortcut(.defaultAction)
                    .buttonStyle(.borderedProminent)
                    .disabled(!manager.isReady)
            }
        }
        .padding(Space.xl)
        .frame(width: 640)
        .noInitialFocus()
    }
}
