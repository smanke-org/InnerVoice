import AppKit
import InnerVoiceKit
import SwiftUI
import UniformTypeIdentifiers

@Observable
final class StartWindowState {
    static let shared = StartWindowState()
    var lastError: String?
    var busy = false
}

/// Shown when no project is open: drop a manuscript, open one, or reopen a recent project.
struct StartWindow: View {
    @Environment(\.newDocument) private var newDocument
    @Environment(\.openDocument) private var openDocument
    @Environment(\.dismissWindow) private var dismissWindow
    @State private var state = StartWindowState.shared
    @State private var recents: [URL] = NSDocumentController.shared.recentDocumentURLs

    var body: some View {
        HStack(spacing: 0) {
            VStack(alignment: .leading, spacing: Space.xl) {
                HStack(spacing: Space.md) {
                    Image(nsImage: NSApp.applicationIconImage)
                        .resizable()
                        .frame(width: 64, height: 64)
                    VStack(alignment: .leading, spacing: 2) {
                        Text("Inner Voice").font(.largeTitle.weight(.semibold))
                        Text("Know every voice before you press record.")
                            .foregroundStyle(.secondary)
                    }
                }
                DropArea(busy: state.busy, error: state.lastError) { url in
                    open(url)
                }
                HStack {
                    Button("Open Project…") {
                        NSDocumentController.shared.openDocument(nil)
                    }
                    Spacer()
                    Text("Manuscripts stay on this Mac.")
                        .font(.caption)
                        .foregroundStyle(.secondary)
                }
            }
            .padding(Space.section)
            .frame(width: 480)
            Divider()
            VStack(alignment: .leading, spacing: Space.sm) {
                Text("Recent Projects").font(.headline).padding(.horizontal, Space.lg).padding(.top, Space.lg)
                if recents.isEmpty {
                    Text("Projects you open show up here.")
                        .font(.callout)
                        .foregroundStyle(.secondary)
                        .padding(.horizontal, Space.lg)
                    Spacer()
                } else {
                    List(recents, id: \.self) { url in
                        Button {
                            Task { try? await openDocument(at: url); dismissWindow(id: "start") }
                        } label: {
                            HStack(spacing: Space.sm) {
                                Image(nsImage: NSWorkspace.shared.icon(forFile: url.path)).resizable().frame(width: 28, height: 28)
                                VStack(alignment: .leading, spacing: 0) {
                                    Text(url.deletingPathExtension().lastPathComponent).lineLimit(1)
                                    Text(url.deletingLastPathComponent().path(percentEncoded: false).replacingOccurrences(of: NSHomeDirectory(), with: "~"))
                                        .font(.caption)
                                        .foregroundStyle(.secondary)
                                        .lineLimit(1)
                                        .truncationMode(.middle)
                                }
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)
                    }
                    .listStyle(.sidebar)
                }
            }
            .frame(width: 280)
            .background(.background.secondary)
        }
        .frame(height: 400)
        .noInitialFocus()
        .onAppear { recents = NSDocumentController.shared.recentDocumentURLs }
        .onReceive(NotificationCenter.default.publisher(for: .innerVoiceAction)) { note in
            if note.object as? String == "newFromManuscript" { Importer.newFromManuscript(newDocument: newDocument) }
        }
    }

    private func open(_ url: URL) {
        if url.pathExtension == "innervoice" {
            Task { try? await openDocument(at: url); dismissWindow(id: "start") }
            return
        }
        state.busy = true
        state.lastError = nil
        Task {
            defer { state.busy = false }
            do {
                let (project, original) = try await Importer.project(from: url)
                newDocument { InnerVoiceDocument(project: project, original: original) }
                dismissWindow(id: "start")
            } catch {
                state.lastError = error.localizedDescription
            }
        }
    }
}

/// A large target for a manuscript file, with Open Manuscript… for people who'd rather browse.
struct DropArea: View {
    var busy: Bool
    var error: String?
    var open: (URL) -> Void
    @State private var targeted = false

    var body: some View {
        VStack(spacing: Space.md) {
            if busy {
                ProgressView().controlSize(.large)
                Text("Reading the manuscript…").foregroundStyle(.secondary)
            } else {
                Image(systemName: "doc.text.magnifyingglass")
                    .font(.system(size: 40, weight: .light))
                    .foregroundStyle(targeted ? AnyShapeStyle(.tint) : AnyShapeStyle(.secondary))
                Text("Drop a manuscript here").font(.title3.weight(.medium))
                Text("Word, PDF, RTF, plain text, Markdown, OpenDocument, Pages, EPUB or HTML. For Google Docs, choose File › Download › Microsoft Word first.")
                    .font(.callout)
                    .foregroundStyle(.secondary)
                    .multilineTextAlignment(.center)
                    .fixedSize(horizontal: false, vertical: true)
                Button("Open Manuscript…") { browse() }
                    .buttonStyle(.borderedProminent)
                    .controlSize(.large)
                if let error {
                    Label(error, systemImage: "exclamationmark.triangle.fill")
                        .font(.callout)
                        .foregroundStyle(.orange)
                        .multilineTextAlignment(.center)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }
        }
        .padding(Space.xl)
        .frame(maxWidth: .infinity, minHeight: 220)
        .background(RoundedRectangle(cornerRadius: 16).fill(targeted ? AnyShapeStyle(.tint.opacity(0.08)) : AnyShapeStyle(.quaternary.opacity(0.3))))
        .overlay(RoundedRectangle(cornerRadius: 16).strokeBorder(style: StrokeStyle(lineWidth: 1.5, dash: [6, 5]))
            .foregroundStyle(targeted ? AnyShapeStyle(.tint) : AnyShapeStyle(.separator)))
        .dropDestination(for: URL.self) { urls, _ in
            guard let url = urls.first else { return false }
            open(url)
            return true
        } isTargeted: { targeted = $0 }
        .animation(.easeOut(duration: 0.15), value: targeted)
        .accessibilityElement(children: .contain)
        .accessibilityLabel("Drop a manuscript here")
    }

    private func browse() {
        let panel = NSOpenPanel()
        panel.title = "Open Manuscript"
        panel.prompt = "Open"
        panel.allowedContentTypes = Importer.contentTypes + [.innerVoiceProject]
        guard panel.runModal() == .OK, let url = panel.url else { return }
        open(url)
    }
}
