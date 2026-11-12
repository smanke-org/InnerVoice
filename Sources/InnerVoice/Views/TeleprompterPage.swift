import AppKit
import InnerVoiceKit
import SwiftUI

/// The teleprompter for the booth: one paragraph at a time in large type, the speaker's
/// color and voice cue, the next speaker below. Space or → moves on, ← goes back; foot
/// pedals that send keys work too. InnerVoice doesn't record sound: the narrator records in
/// their own audio app. Each book opens where it was left off.
struct TeleprompterPage: View {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    @AppStorage("record.fontSize") private var fontSize = 30.0
    @AppStorage("record.nextKey") private var nextKey = "space"
    @FocusState private var focused: Bool

    /// Every readable paragraph in book order (headings and scene breaks are skipped).
    private var paragraphs: [(chapter: Int, paragraph: Paragraph)] {
        document.project.chapters.enumerated().flatMap { c, ch in
            ch.paragraphs.filter { $0.style == .body || $0.style == .blockQuote }.map { (c, $0) }
        }
    }

    private func currentIndex(_ all: [(chapter: Int, paragraph: Paragraph)]) -> Int {
        let id = workspace.teleprompterParagraph ?? TeleprompterPosition.saved(for: document.project)
        if let id, let i = all.firstIndex(where: { $0.paragraph.id == id }) { return i }
        return 0
    }

    var body: some View {
        let all = paragraphs
        let i = currentIndex(all)
        VStack(spacing: 0) {
            progressHeader(all: all, i: i)
            Divider()
            if all.isEmpty {
                ContentUnavailableView("Nothing to Read", systemImage: "text.alignleft")
            } else {
                prompter(all: all, i: i)
            }
            Divider()
            controls(all: all)
        }
        .background(Color(nsColor: .textBackgroundColor))
        .navigationTitle("Teleprompter")
        .pageSubtitle()
        .focusable()
        .focusEffectDisabled()
        .focused($focused)
        .onAppear { focused = true }
        .onKeyPress(keys: [.space, .rightArrow, .downArrow, .pageDown, .return]) { press in
            guard matchesNext(press.key) || press.key == .rightArrow else { return .ignored }
            move(1, all: all)
            return .handled
        }
        .onKeyPress(keys: [.leftArrow, .upArrow, .pageUp]) { _ in
            move(-1, all: all)
            return .handled
        }
    }

    private func matchesNext(_ key: KeyEquivalent) -> Bool {
        switch nextKey {
        case "space": key == .space
        case "down": key == .downArrow
        case "pagedown": key == .pageDown
        case "return": key == .return
        default: key == .space
        }
    }

    private func move(_ delta: Int, all: [(chapter: Int, paragraph: Paragraph)]) {
        guard !all.isEmpty else { return }
        let next = max(0, min(all.count - 1, currentIndex(all) + delta))
        let id = all[next].paragraph.id
        withAnimation(.easeOut(duration: 0.18)) { workspace.teleprompterParagraph = id }
        TeleprompterPosition.save(id, for: document.project)
    }

    // MARK: Parts

    private func progressHeader(all: [(chapter: Int, paragraph: Paragraph)], i: Int) -> some View {
        let chapterIndex = all.indices.contains(i) ? all[i].chapter : 0
        let inChapter = all.filter { $0.chapter == chapterIndex }
        let inChapterIndex = inChapter.firstIndex { all.indices.contains(i) && $0.paragraph.id == all[i].paragraph.id } ?? 0
        return HStack(spacing: Space.xl) {
            VStack(alignment: .leading, spacing: 2) {
                Text(document.project.chapters.indices.contains(chapterIndex) ? document.project.chapters[chapterIndex].shownTitle : "")
                    .font(.headline)
                Text("Paragraph \(i + 1) of \(all.count)")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
            Spacer()
            VStack(alignment: .trailing, spacing: Space.xs) {
                Text("\(Int(Double(inChapterIndex + 1) / Double(max(1, inChapter.count)) * 100))% through the chapter")
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(.secondary)
                ProgressView(value: Double(inChapterIndex + 1), total: Double(max(1, inChapter.count)))
                    .frame(width: 220)
            }
        }
        .padding(.horizontal, Space.xl)
        .padding(.vertical, Space.md)
    }

    private func prompter(all: [(chapter: Int, paragraph: Paragraph)], i: Int) -> some View {
        let current = all[i].paragraph
        let lines = (document.index.linesByParagraph[current.id] ?? []).map { document.project.lines[$0] }
        let speakers = uniqueSpeakers(lines)
        let next = all.indices.contains(i + 1) ? all[i + 1].paragraph : nil
        let nextSpeakers = next.map { p in uniqueSpeakers((document.index.linesByParagraph[p.id] ?? []).map { document.project.lines[$0] }) } ?? []
        return ScrollView {
            VStack(alignment: .leading, spacing: Space.xxl) {
                if i > 0 {
                    Text(all[i - 1].paragraph.text)
                        .font(.system(size: fontSize * 0.55, design: ReadingStyle.serif ? .serif : .default))
                        .foregroundStyle(.tertiary)
                        .lineLimit(3)
                }
                if !speakers.isEmpty {
                    HStack(spacing: Space.sm) {
                        ForEach(speakers, id: \.self) { id in speakerChip(id) }
                    }
                }
                Text(attributed(current, lines: lines))
                    .font(.system(size: fontSize, design: ReadingStyle.serif ? .serif : .default))
                    .lineSpacing(fontSize * 0.35)
                    .textSelection(.enabled)
                    .id(current.id)
                    .transition(.opacity)
                if let next {
                    VStack(alignment: .leading, spacing: Space.xs) {
                        // With a Casting Style, how to shade them too: "Next: Tom Hale (lower, gravelly)".
                        let shades = document.project.settings.castingStyle == nil ? [:] : document.narrationShades()
                        Text(nextSpeakers.isEmpty ? "Next: narration" : "Next: " + nextSpeakers.compactMap { id in
                            document.project.character(id).map { c in c.name + (shades[id]?.cue.map { " (\($0))" } ?? "") }
                        }.joined(separator: ", "))
                            .font(.caption.weight(.semibold))
                            .foregroundStyle(.secondary)
                        Text(next.text)
                            .font(.system(size: fontSize * 0.55, design: ReadingStyle.serif ? .serif : .default))
                            .foregroundStyle(.tertiary)
                            .lineLimit(2)
                    }
                    .padding(.top, Space.lg)
                }
            }
            .padding(.horizontal, Space.section * 2)
            .padding(.vertical, Space.section)
            .frame(maxWidth: 960, alignment: .leading)
            .frame(maxWidth: .infinity)
        }
    }

    private func uniqueSpeakers(_ lines: [Line]) -> [UUID] {
        var seen: [UUID] = []
        for l in lines { if let s = l.speakerID, !seen.contains(s) { seen.append(s) } }
        return seen
    }

    private func speakerChip(_ id: UUID) -> some View {
        let c = document.project.character(id)
        // Casting Style: how to shade them first ("lower, gravelly"), then the summary.
        let shade = document.project.settings.castingStyle == nil ? nil : document.narrationShades()[id]?.cue
        let cue = ([shade.map { "Shade: " + $0 }] + [c.map { document.voiceCue(for: $0) }]).compactMap { $0 }.filter { !$0.isEmpty }.joined(separator: " · ")
        return HStack(spacing: Space.sm) {
            if let c { Avatar(character: c, size: 26) }
            VStack(alignment: .leading, spacing: 0) {
                Text(c?.name ?? "").font(.headline)
                if !cue.isEmpty { Text(cue).font(.caption).foregroundStyle(.secondary).lineLimit(1) }
            }
        }
        .padding(.horizontal, Space.md)
        .padding(.vertical, Space.sm)
        .background(Color(hexString: c?.colorHex ?? "#888888").opacity(0.14), in: Capsule())
    }

    /// The paragraph with each line tinted in its speaker's color.
    private func attributed(_ p: Paragraph, lines: [Line]) -> AttributedString {
        var s = AttributedString(p.text)
        let chars = Array(p.text.utf16)
        for line in lines {
            guard line.range.end <= chars.count else { continue }
            let color: Color = document.project.needsYou(line) ? Color(nsColor: .attention) : Color(hexString: document.project.character(line.speakerID)?.colorHex ?? "#888888")
            let startIdx = String.Index(utf16Offset: line.range.location, in: p.text)
            let endIdx = String.Index(utf16Offset: line.range.end, in: p.text)
            guard let lo = AttributedString.Index(startIdx, within: s), let hi = AttributedString.Index(endIdx, within: s) else { continue }
            s[lo..<hi].backgroundColor = color.opacity(0.22)
            if line.kind == .thought { s[lo..<hi].underlineStyle = .init(pattern: .dash, color: color) }
        }
        return s
    }

    private func controls(all: [(chapter: Int, paragraph: Paragraph)]) -> some View {
        HStack(spacing: Space.md) {
            Button("Previous", systemImage: "chevron.left") { move(-1, all: all) }
                .help("Go back a paragraph (←)")
            Button("Next", systemImage: "chevron.right") { move(1, all: all) }
                .help("Next paragraph (Space or →)")
                .buttonStyle(.borderedProminent)
            Spacer()
            Text("Record in your own audio app while you read")
                .font(.caption)
                .foregroundStyle(.tertiary)
            Menu("Text Size", systemImage: "textformat.size") {
                Button("Larger") { fontSize = min(64, fontSize + 4) }
                Button("Smaller") { fontSize = max(18, fontSize - 4) }
            }
            .fixedSize()
            .help("Change the size of the text")
        }
        .padding(.horizontal, Space.xl)
        .padding(.vertical, Space.md)
    }
}

/// Where the teleprompter was left in each book, per Mac (test runs keep their own store,
/// like the reading settings). A book is known by when it was imported and its title.
enum TeleprompterPosition {
    private static let key = "teleprompter.positions"

    private static func bookKey(_ project: Project) -> String {
        "\(Int(project.importedAt.timeIntervalSince1970))|\(project.title)"
    }

    static func saved(for project: Project) -> UUID? {
        let all = ReadingStyle.store.dictionary(forKey: key) as? [String: String] ?? [:]
        return all[bookKey(project)].flatMap(UUID.init(uuidString:))
    }

    static func save(_ paragraphID: UUID, for project: Project) {
        var all = ReadingStyle.store.dictionary(forKey: key) as? [String: String] ?? [:]
        all[bookKey(project)] = paragraphID.uuidString
        ReadingStyle.store.set(all, forKey: key)
    }
}
