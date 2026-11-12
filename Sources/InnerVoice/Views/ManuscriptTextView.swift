import AppKit
import InnerVoiceKit
import SwiftUI

/// Reading settings shared by the manuscript and the Teleprompter (per Mac, not per project).
enum ReadingStyle {
    static let sizes: ClosedRange<Double> = 12...26
    static let standardSize = 16.0

    /// Where reading settings live. Test runs get a store of their own (emptied at launch):
    /// a test copy shares the person's preferences, and its text size or margin choices
    /// must never change theirs.
    static let store: UserDefaults = {
        guard SystemHooks.isTestRun, let test = UserDefaults(suiteName: "com.smanke.InnerVoice.testrun") else { return .standard }
        test.removePersistentDomain(forName: "com.smanke.InnerVoice.testrun")
        return test
    }()

    static var fontSize: CGFloat {
        let v = store.double(forKey: "reading.fontSize")
        return v > 0 ? v : standardSize
    }
    static var serif: Bool { store.object(forKey: "reading.serif") as? Bool ?? true }
    static var spacing: LineSpacing { LineSpacing(rawValue: store.string(forKey: "reading.lineSpacing") ?? "") ?? .single }
    static var marginNames: MarginNames { MarginNames(rawValue: store.string(forKey: "reading.marginNames") ?? "") ?? .initials }

    /// The whole look at once, for the reading view to compare against what it built.
    static var look: ReadingLook { ReadingLook(fontSize: fontSize, serif: serif, spacing: spacing, margin: marginNames) }

    /// Bigger or smaller text, one point at a time, within `sizes`.
    static func setFontSize(_ size: Double) {
        store.set(min(max(size.rounded(), sizes.lowerBound), sizes.upperBound), forKey: "reading.fontSize")
    }

    static func setSpacing(_ spacing: LineSpacing) {
        store.set(spacing.rawValue, forKey: "reading.lineSpacing")
    }

    static func font(size: CGFloat, weight: NSFont.Weight = .regular, serif: Bool = ReadingStyle.serif) -> NSFont {
        let base = NSFont.systemFont(ofSize: size, weight: weight)
        if serif, let d = base.fontDescriptor.withDesign(.serif) { return NSFont(descriptor: d, size: size) ?? base }
        return base
    }
}

/// What the reading view is built with. The page passes it in, so a change from the
/// toolbar, Settings or the View menu reaches the view on the next update.
struct ReadingLook: Equatable {
    var fontSize: CGFloat
    var serif: Bool
    var spacing: LineSpacing
    var margin: MarginNames
}

/// Single keeps the reading view's usual spacing; Double is twice the single line height,
/// like a double-spaced manuscript.
enum LineSpacing: String, CaseIterable, Identifiable {
    case single, double
    var id: String { rawValue }
    var title: String { self == .single ? "Single" : "Double" }
    var multiple: CGFloat { self == .single ? 1.32 : 2.0 }
}

/// What the left margin shows for each line's speaker.
enum MarginNames: String, CaseIterable, Identifiable {
    case initials, firstName, fullName
    var id: String { rawValue }
    var title: String {
        switch self {
        case .initials: "Initials"
        case .firstName: "First Name"
        case .fullName: "Full Name"
        }
    }
    func label(for c: BookCharacter) -> String {
        switch self {
        case .initials: c.initials
        case .firstName: c.firstName
        case .fullName: c.name
        }
    }
}

/// Where each line sits in the reading view's text.
struct LineLayout {
    /// A line by its id, never by its place in `project.lines`: adding or removing a line
    /// shifts every place after it, and a stale place drew (and clicked) the next line.
    struct Entry {
        var range: NSRange
        var lineID: UUID
    }
    var entries: [Entry] = []
    var paragraphRanges: [UUID: NSRange] = [:]
    var chapterStarts: [Int] = []
    /// Paragraph id for a character offset, via sorted starts.
    var paragraphStarts: [(Int, UUID)] = []

    func entry(at char: Int) -> Entry? {
        var lo = 0, hi = entries.count - 1
        while lo <= hi {
            let mid = (lo + hi) / 2
            let r = entries[mid].range
            if char < r.location { hi = mid - 1 } else if char >= r.location + r.length { lo = mid + 1 } else { return entries[mid] }
        }
        return nil
    }

    func entries(in range: NSRange) -> [Entry] {
        // First entry ending after range.location.
        var lo = 0, hi = entries.count
        while lo < hi {
            let mid = (lo + hi) / 2
            if NSMaxRange(entries[mid].range) <= range.location { lo = mid + 1 } else { hi = mid }
        }
        var out: [Entry] = []
        var i = lo
        while i < entries.count, entries[i].range.location < NSMaxRange(range) {
            out.append(entries[i])
            i += 1
        }
        return out
    }

    func paragraph(at char: Int) -> UUID? {
        var lo = 0, hi = paragraphStarts.count - 1, found: UUID?
        while lo <= hi {
            let mid = (lo + hi) / 2
            if paragraphStarts[mid].0 <= char { found = paragraphStarts[mid].1; lo = mid + 1 } else { hi = mid - 1 }
        }
        return found
    }
}

/// The whole book in one read-only text view. Line colors are drawn by the layout
/// manager, so assigning a line only redraws; the text is rebuilt only when it changes.
struct ManuscriptTextView: NSViewRepresentable {
    @ObservedObject var document: InnerVoiceDocument
    @Bindable var workspace: Workspace
    var undoManager: UndoManager?
    /// Assign To for these lines (one line, or a whole paragraph from its margin name).
    var onAssignRequest: ([UUID], NSRect, NSView) -> Void
    var onVisibleChapter: (Int) -> Void
    /// The reading settings. The page reads them (so SwiftUI updates it when they change)
    /// and passes them down: settings the view only held in @AppStorage, without reading
    /// them, never reached it, so Bigger and Smaller Text did nothing.
    var look: ReadingLook

    func makeCoordinator() -> Coordinator { Coordinator(self) }

    func makeNSView(context: Context) -> NSScrollView {
        let storage = NSTextStorage()
        let layout = LineLayoutManager()
        layout.allowsNonContiguousLayout = true
        storage.addLayoutManager(layout)
        let container = NSTextContainer(size: NSSize(width: 600, height: CGFloat.greatestFiniteMagnitude))
        container.widthTracksTextView = false
        layout.addTextContainer(container)
        let textView = ReadingTextView(frame: .zero, textContainer: container)
        textView.isEditable = false
        textView.isSelectable = true
        textView.isRichText = true
        textView.drawsBackground = true
        textView.backgroundColor = .textBackgroundColor
        textView.usesFindBar = true
        textView.isIncrementalSearchingEnabled = true
        textView.isVerticallyResizable = true
        textView.isHorizontallyResizable = false
        textView.autoresizingMask = [.width]
        textView.minSize = NSSize(width: 0, height: 0)
        textView.maxSize = NSSize(width: CGFloat.greatestFiniteMagnitude, height: CGFloat.greatestFiniteMagnitude)
        textView.coordinator = context.coordinator
        layout.coordinator = context.coordinator
        textView.setAccessibilityLabel("Manuscript")

        let scroll = NSScrollView()
        scroll.documentView = textView
        scroll.hasVerticalScroller = true
        scroll.drawsBackground = true
        scroll.backgroundColor = .textBackgroundColor
        scroll.contentView.postsBoundsChangedNotifications = true
        NotificationCenter.default.addObserver(context.coordinator, selector: #selector(Coordinator.didScroll),
                                               name: NSView.boundsDidChangeNotification, object: scroll.contentView)
        NotificationCenter.default.addObserver(context.coordinator, selector: #selector(Coordinator.didResize),
                                               name: NSView.frameDidChangeNotification, object: scroll)
        context.coordinator.textView = textView
        context.coordinator.rebuild()
        return scroll
    }

    func updateNSView(_ scroll: NSScrollView, context: Context) {
        let c = context.coordinator
        c.parent = self
        if c.builtText != document.textRevision || c.builtFontSize != look.fontSize || c.builtSerif != look.serif
            || c.builtSpacing != look.spacing {
            c.rebuild()
        } else if c.builtLines != document.lineRevision {
            c.rebuildEntries()
            c.builtTags = document.tagRevision
            c.builtSelection = workspace.selectedLines
            c.textView?.needsDisplay = true
            c.textView?.margin.needsDisplay = true
        } else if c.builtTags != document.tagRevision || c.builtSelection != workspace.selectedLines {
            c.builtTags = document.tagRevision
            c.builtSelection = workspace.selectedLines
            c.textView?.needsDisplay = true
            c.textView?.margin.needsDisplay = true
        }
        // Longer names in the margin need a wider gutter (only resized when it changes).
        if c.builtGutter != c.gutterWidth() { c.didResize() }
        if c.builtMargin != look.margin {
            c.builtMargin = look.margin
            c.textView?.margin.needsDisplay = true
        }
        // Scroll first, then open Assign To, so the popover points at the line on screen.
        if let reveal = workspace.reveal, reveal != c.handledReveal {
            c.handledReveal = reveal
            DispatchQueue.main.async { c.reveal(reveal) }
        }
        if let pending = workspace.pendingAssign {
            DispatchQueue.main.async {
                workspace.pendingAssign = nil
                DispatchQueue.main.async { c.requestAssign(pending, group: c.pickedGroup(containing: pending)) }
            }
        }
    }

    final class Coordinator: NSObject {
        var parent: ManuscriptTextView
        weak var textView: ReadingTextView?
        var layout = LineLayout()
        var builtText = -1
        var builtTags = -1
        var builtLines = -1
        var builtFontSize: CGFloat = 0
        var builtSerif = true
        var builtSpacing = LineSpacing.single
        var builtGutter: CGFloat = 0
        var builtMargin = MarginNames.initials
        var builtSelection: [UUID] = []
        var handledReveal: Workspace.Reveal?
        private var lastReportedChapter = -1

        init(_ parent: ManuscriptTextView) { self.parent = parent }

        var document: InnerVoiceDocument { parent.document }
        var project: Project { parent.document.project }

        // MARK: Building

        func rebuild() {
            guard let textView else { return }
            // Same book, new size or spacing: keep the reader's place.
            let anchor = builtText == document.textRevision ? topCharacter() : nil
            builtText = document.textRevision
            builtTags = document.tagRevision
            builtLines = document.lineRevision
            builtFontSize = parent.look.fontSize
            builtSerif = parent.look.serif
            builtSpacing = parent.look.spacing
            builtMargin = parent.look.margin
            let (text, layout) = Self.build(project: project, index: document.index, fontSize: builtFontSize, spacing: builtSpacing, serif: builtSerif)
            self.layout = layout
            textView.textStorage?.setAttributedString(text)
            didResize()
            if let anchor { scroll(toCharacter: anchor) }
        }

        /// The first character at the top of the visible text, or nil at the very top.
        func topCharacter() -> Int? {
            guard let tv = textView, let lm = tv.layoutManager, let tc = tv.textContainer, !tv.string.isEmpty else { return nil }
            let y = tv.visibleRect.minY - tv.textContainerOrigin.y
            guard y > 1 else { return nil }
            return lm.characterIndexForGlyph(at: lm.glyphIndex(for: NSPoint(x: 0, y: y), in: tc))
        }

        func scroll(toCharacter char: Int) {
            guard let tv = textView, let lm = tv.layoutManager, let tc = tv.textContainer,
                  char < (tv.string as NSString).length else { return }
            let range = NSRange(location: char, length: 1)
            lm.ensureLayout(forCharacterRange: range)
            let rect = lm.boundingRect(forGlyphRange: lm.glyphRange(forCharacterRange: range, actualCharacterRange: nil), in: tc)
            tv.scroll(NSPoint(x: 0, y: rect.minY + tv.textContainerOrigin.y))
        }

        /// Lines were added or removed but the text is the same: only the line map changes.
        func rebuildEntries() {
            builtLines = document.lineRevision
            layout.entries = Self.entries(project: project, index: document.index, paragraphRanges: layout.paragraphRanges)
        }

        static func entries(project: Project, index: BookIndex, paragraphRanges: [UUID: NSRange]) -> [LineLayout.Entry] {
            var out: [LineLayout.Entry] = []
            for (pid, i) in index.linesByParagraph.flatMap({ pid, ids in ids.map { (pid, $0) } }) {
                guard let start = paragraphRanges[pid]?.location else { continue }
                let r = project.lines[i].range
                out.append(LineLayout.Entry(range: NSRange(location: start + r.location, length: r.length), lineID: project.lines[i].id))
            }
            return out.sorted { $0.range.location < $1.range.location }
        }

        /// The part of a line rect that holds the text. Extra line spacing sits above the
        /// words, so with Double spacing a highlight would otherwise reach up into the gap.
        /// Single spacing keeps its usual box.
        func textBox(_ rect: NSRect) -> NSRect {
            let m = builtSpacing.multiple, usual = LineSpacing.single.multiple
            guard m > usual else { return rect }
            let height = rect.height * usual / m
            return NSRect(x: rect.minX, y: rect.maxY - height, width: rect.width, height: height)
        }

        /// The line an entry stands for, if it still exists.
        func line(_ e: LineLayout.Entry) -> Line? {
            document.index.lineIndexByID[e.lineID].map { project.lines[$0] }
        }

        static func build(project: Project, index: BookIndex, fontSize: CGFloat, spacing: LineSpacing = .single,
                          serif: Bool = true) -> (NSAttributedString, LineLayout) {
            let out = NSMutableAttributedString()
            var layout = LineLayout()
            let body = ReadingStyle.font(size: fontSize, serif: serif)
            let heading = ReadingStyle.font(size: round(fontSize * 1.55), weight: .semibold, serif: serif)
            let bodyStyle = NSMutableParagraphStyle()
            bodyStyle.lineHeightMultiple = spacing.multiple
            bodyStyle.paragraphSpacing = fontSize * 0.75
            bodyStyle.firstLineHeadIndent = 0
            let headingStyle = NSMutableParagraphStyle()
            headingStyle.paragraphSpacingBefore = fontSize * 2.5
            headingStyle.paragraphSpacing = fontSize * 1.25
            let breakStyle = NSMutableParagraphStyle()
            breakStyle.alignment = .center
            breakStyle.paragraphSpacing = fontSize
            let quoteStyle = bodyStyle.mutableCopy() as! NSMutableParagraphStyle
            quoteStyle.headIndent = 28
            quoteStyle.firstLineHeadIndent = 28
            let italicFont = NSFontManager.shared.convert(body, toHaveTrait: .italicFontMask)

            for (c, chapter) in project.chapters.enumerated() {
                layout.chapterStarts.append(out.length)
                // A chapter whose heading was deleted shows none.
                if !chapter.title.isEmpty {
                    out.append(NSAttributedString(string: chapter.title + "\n", attributes: [
                        .font: heading, .foregroundColor: NSColor.labelColor, .paragraphStyle: headingStyle,
                        .chapterIndex: c,
                    ]))
                }
                for p in chapter.paragraphs {
                    let start = out.length
                    let style: NSParagraphStyle = p.style == .sceneBreak ? breakStyle : (p.style == .blockQuote ? quoteStyle : bodyStyle)
                    let s = NSMutableAttributedString(string: p.text, attributes: [
                        .font: body, .foregroundColor: NSColor.labelColor, .paragraphStyle: style,
                    ])
                    for r in p.italic where r.end <= s.length { s.addAttribute(.font, value: italicFont, range: r.ns) }
                    out.append(s)
                    out.append(NSAttributedString(string: "\n", attributes: [.font: body, .paragraphStyle: style]))
                    layout.paragraphRanges[p.id] = NSRange(location: start, length: (p.text as NSString).length)
                    layout.paragraphStarts.append((start, p.id))
                }
            }
            layout.entries = entries(project: project, index: index, paragraphRanges: layout.paragraphRanges)
            return (out, layout)
        }

        // MARK: Geometry

        /// Room left of the text for the margin labels: initials need little, full names more.
        func gutterWidth() -> CGFloat {
            guard project.settings.showInitials else { return 72 }
            let names = parent.look.margin
            // Room for a shading arrow too, when the Casting Style shows them.
            let extra = project.showsNarrationTools && project.settings.castingStyle != nil ? " ↓↓" : "?"
            let widest = project.characters.map { LineLayoutManager.markerWidth(names.label(for: $0) + extra) }.max() ?? 0
            return max(72, widest + 40)
        }

        /// Centers a comfortable reading measure and leaves a gutter for the margin labels.
        @objc func didResize() {
            guard let textView, let scroll = textView.enclosingScrollView, let container = textView.textContainer else { return }
            let width = scroll.contentSize.width
            let gutter = gutterWidth()
            builtGutter = gutter
            let measure = min(max(360, width - 2 * gutter), builtFontSize * 42)
            let side = max(gutter, (width - measure) / 2)
            textView.textContainerInset = NSSize(width: side, height: 40)
            container.size = NSSize(width: measure, height: CGFloat.greatestFiniteMagnitude)
            textView.frame.size.width = width
            textView.needsDisplay = true
            textView.margin.needsDisplay = true
        }

        @objc func didScroll() {
            guard let textView, let layoutManager = textView.layoutManager, let container = textView.textContainer else { return }
            let visible = textView.visibleRect
            let origin = textView.textContainerOrigin
            let glyphs = layoutManager.glyphRange(forBoundingRect: visible.offsetBy(dx: -origin.x, dy: -origin.y), in: container)
            let char = layoutManager.characterIndexForGlyph(at: glyphs.location)
            let chapter = (layout.chapterStarts.lastIndex { $0 <= char + 40 }) ?? 0
            if chapter != lastReportedChapter {
                lastReportedChapter = chapter
                parent.onVisibleChapter(chapter)
            }
        }

        func rects(for range: NSRange) -> [NSRect] {
            guard let textView, let lm = textView.layoutManager, let tc = textView.textContainer else { return [] }
            let glyphs = lm.glyphRange(forCharacterRange: range, actualCharacterRange: nil)
            var out: [NSRect] = []
            lm.enumerateEnclosingRects(forGlyphRange: glyphs, withinSelectedGlyphRange: NSRange(location: NSNotFound, length: 0), in: tc) { rect, _ in
                out.append(rect)
            }
            let origin = textView.textContainerOrigin
            return out.map { $0.offsetBy(dx: origin.x, dy: origin.y) }
        }

        func reveal(_ r: Workspace.Reveal) {
            guard let textView else { return }
            var range = layout.paragraphRanges[r.paragraphID] ?? NSRange(location: 0, length: 0)
            if let lineID = r.lineID, let e = layout.entries.first(where: { $0.lineID == lineID }) {
                range = e.range
            }
            guard range.location != NSNotFound, range.location <= (textView.string as NSString).length else { return }
            textView.layoutManager?.ensureLayout(forCharacterRange: range)
            if let first = rects(for: range).first {
                let visible = textView.visibleRect
                let target = NSRect(x: 0, y: max(0, first.midY - visible.height * 0.35), width: 1, height: visible.height)
                textView.scroll(target.origin)
            }
            textView.showFindIndicator(for: range)
        }

        // MARK: Interaction

        func click(at char: Int) {
            guard let e = layout.entry(at: char), let line = line(e) else {
                parent.workspace.selectedLines = []
                return
            }
            parent.workspace.selectedLines = [line.id]
            requestAssign(line.id)
        }

        func requestAssign(_ lineID: UUID, group: [UUID]? = nil) {
            guard let textView, let e = layout.entries.first(where: { $0.lineID == lineID }) else { return }
            let rects = rects(for: e.range)
            guard let rect = rects.first else { return }
            parent.onAssignRequest(group ?? [lineID], rect, textView)
        }

        func lines(in range: NSRange) -> [UUID] {
            layout.entries(in: range).compactMap { line($0)?.id }
        }

        func menu(for char: Int, selection: NSRange) -> NSMenu {
            let menu = NSMenu()
            var ids = selection.length > 0 ? lines(in: selection) : []
            if ids.isEmpty, let e = layout.entry(at: char), let l = line(e) { ids = pickedGroup(containing: l.id) ?? [l.id] }
            let undo = parent.undoManager
            if !ids.isEmpty {
                parent.workspace.selectedLines = ids
                let title = ids.count == 1 ? "Assign To" : "Assign \(ids.count) Lines To"
                let assign = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                assign.submenu = speakerMenu(for: ids, heading: nil)
                menu.addItem(assign)
                let kind = NSMenuItem(title: "Kind", action: nil, keyEquivalent: "")
                let kinds = NSMenu()
                for k in LineKind.allCases {
                    let item = ClosureMenuItem(title: k.title) { [weak self] in self?.document.setKind(k, for: ids, undoManager: undo) }
                    if ids.count == 1, let li = document.index.lineIndexByID[ids[0]], project.lines[li].kind == k { item.state = .on }
                    kinds.addItem(item)
                }
                kind.submenu = kinds
                menu.addItem(kind)
                let toneItem = NSMenuItem(title: "Tone", action: nil, keyEquivalent: "")
                let tones = NSMenu()
                let currentTones = Set(ids.compactMap { id in document.index.lineIndexByID[id].flatMap { project.lines[$0].tone } })
                for t in Tone.allCases {
                    let item = ClosureMenuItem(title: t.title) { [weak self] in self?.document.setTone(t, for: ids, undoManager: undo) }
                    if currentTones == [t] { item.state = .on }
                    tones.addItem(item)
                }
                toneItem.submenu = tones
                menu.addItem(toneItem)
                if document.project.showsVoices {
                    menu.addItem(ClosureMenuItem(title: ids.count == 1 ? "Play Line" : "Play \(ids.count) Lines") { [weak self] in
                        guard let self else { return }
                        PlayCommand.playLine(document: self.document, workspace: self.parent.workspace)
                    })
                }
                menu.addItem(ClosureMenuItem(title: ids.count == 1 ? "Mark as Narration" : "Mark \(ids.count) Lines as Narration") { [weak self] in
                    self?.document.removeLines(ids, undoManager: undo)
                })
                if ids.count == 1, let li = document.index.lineIndexByID[ids[0]], let speaker = project.lines[li].speakerID {
                    menu.addItem(.separator())
                    menu.addItem(ClosureMenuItem(title: "Show \(project.character(speaker)?.name ?? "Character")") { [weak self] in
                        self?.parent.workspace.showCharacter(speaker)
                    })
                }
            } else if selection.length > 0, let pid = layout.paragraph(at: selection.location),
                      let pr = layout.paragraphRanges[pid], NSMaxRange(selection) <= NSMaxRange(pr) {
                menu.addItem(ClosureMenuItem(title: "Make This a Line") { [weak self] in
                    guard let self else { return }
                    let id = self.document.makeLine(paragraphID: pid, range: TextSpan(location: selection.location - pr.location, length: selection.length),
                                                    speaker: nil, undoManager: undo)
                    self.parent.workspace.selectedLines = [id]
                    DispatchQueue.main.async { self.requestAssign(id) }
                })
            }
            if menu.items.count > 0 { menu.addItem(.separator()) }
            menu.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "")
            if selection.length > 0 {
                menu.addItem(ClosureMenuItem(title: "Delete Text") { [weak self] in self?.deleteText(in: selection) })
            }
            return menu
        }

        /// Speakers for `ids`: assigning, Narration and pins all go through the window's undo.
        func speakerMenu(for ids: [UUID], heading: String?) -> NSMenu {
            let undo = parent.undoManager
            let current = Set(ids.compactMap { id in document.index.lineIndexByID[id].map { project.lines[$0].speakerID } })
            return SpeakerMenu(project: project, heading: heading, current: current.count == 1 ? current.first! : nil,
                               assign: { [weak self] speaker in self?.document.assign(ids, to: speaker, undoManager: undo) },
                               narration: { [weak self] in
                                   self?.document.removeLines(ids, undoManager: undo)
                                   self?.parent.workspace.selectedLines = []
                               },
                               pin: { [weak self] id, on in self?.document.setPinned(id, on, undoManager: undo) }).menu
        }

        // MARK: The margin

        /// The line whose name is drawn in the margin at `point` (text view coordinates).
        func marginEntry(at point: NSPoint) -> LineLayout.Entry? {
            guard let tv = textView, let lm = tv.layoutManager, let tc = tv.textContainer else { return nil }
            let origin = tv.textContainerOrigin
            guard point.x < origin.x - 4 else { return nil }
            let probe = NSRect(x: 0, y: point.y - origin.y - 2, width: tc.size.width, height: 4)
            let glyphs = lm.glyphRange(forBoundingRect: probe, in: tc)
            let chars = lm.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
            for e in layout.entries(in: chars) {
                guard let l = line(e), project.settings.showInitials || project.needsYou(l) else { continue }
                let first = lm.boundingRect(forGlyphRange: lm.glyphRange(forCharacterRange: NSRange(location: e.range.location, length: min(1, e.range.length)), actualCharacterRange: nil), in: tc)
                if point.y >= first.minY + origin.y, point.y <= first.maxY + origin.y { return e }
            }
            return nil
        }

        /// Every line in the clicked line's paragraph, in reading order: a name in the margin
        /// stands for its whole paragraph.
        func paragraphLines(of clicked: Line) -> [UUID] {
            let ids = project.lines.filter { $0.paragraphID == clicked.paragraphID }.map(\.id)
            return ids.isEmpty ? [clicked.id] : ids
        }

        /// Where a line's name sits in the margin (text view coordinates), for tests.
        func marginPoint(of lineID: UUID) -> NSPoint? {
            guard let tv = textView, let lm = tv.layoutManager, let tc = tv.textContainer,
                  let e = layout.entries.first(where: { $0.lineID == lineID }) else { return nil }
            let glyphs = lm.glyphRange(forCharacterRange: NSRange(location: e.range.location, length: min(1, e.range.length)), actualCharacterRange: nil)
            let first = lm.boundingRect(forGlyphRange: glyphs, in: tc)
            let origin = tv.textContainerOrigin
            return NSPoint(x: origin.x - 24, y: origin.y + first.midY)
        }

        /// The picked lines in book order, when several are picked and `id` is one of them.
        func pickedGroup(containing id: UUID) -> [UUID]? {
            let picked = Set(parent.workspace.selectedLines)
            guard picked.count > 1, picked.contains(id) else { return nil }
            return project.lines.lazy.map(\.id).filter { picked.contains($0) }
        }

        /// A click on a name in the margin: Assign To for the whole paragraph, so one pick
        /// confirms or changes every line in it, or for every picked line when the paragraph
        /// is among them. ⌘-click adds the paragraph to the picked lines (or takes it out)
        /// without asking yet. False when no name is there.
        @discardableResult
        func marginClick(at point: NSPoint, adding: Bool = false) -> Bool {
            guard let e = marginEntry(at: point), let clicked = line(e) else { return false }
            let ids = paragraphLines(of: clicked)
            let picked = parent.workspace.selectedLines
            if adding {
                parent.workspace.selectedLines = Set(ids).isSubset(of: picked) ? picked.filter { !ids.contains($0) } : picked + ids.filter { !picked.contains($0) }
                return true
            }
            if picked.count > ids.count, Set(ids).isSubset(of: picked), let group = pickedGroup(containing: clicked.id) {
                requestAssign(clicked.id, group: group)
                return true
            }
            parent.workspace.selectedLines = ids
            requestAssign(clicked.id, group: ids)
            return true
        }

        /// ⌘-click on a line: add it to the picked lines, or take it out.
        func toggle(at char: Int) {
            guard let e = layout.entry(at: char), let line = line(e) else { return }
            let picked = parent.workspace.selectedLines
            parent.workspace.selectedLines = picked.contains(line.id) ? picked.filter { $0 != line.id } : picked + [line.id]
        }

        /// A right-click on a name in the margin: the same choice as a menu, for every line in
        /// the paragraph.
        func marginMenu(at point: NSPoint) -> NSMenu? {
            guard let e = marginEntry(at: point), let clicked = line(e) else { return nil }
            let ids = paragraphLines(of: clicked)
            parent.workspace.selectedLines = ids
            let needs = ids.contains { id in document.index.lineIndexByID[id].map { project.needsYou(project.lines[$0]) } ?? false }
            let heading: String
            if ids.count == 1 {
                heading = needs ? "Who says this line?" : "This line"
            } else {
                heading = needs ? "Who says the \(ids.count) lines in this paragraph?" : "The \(ids.count) lines in this paragraph"
            }
            return speakerMenu(for: ids, heading: heading)
        }

        // MARK: Deleting text

        /// Deletes the selected text from the project (not from the imported file). A whole
        /// chapter goes when its heading and every paragraph are selected.
        func deleteText(in selection: NSRange) {
            guard selection.length > 0 else { return }
            let total = (textView?.string as NSString?)?.length ?? 0
            let plan = Self.deletion(of: selection, project: project, layout: layout, total: total)
            guard !plan.isEmpty else { NSSound.beep(); return }
            textView?.setSelectedRange(NSRange(location: selection.location, length: 0))
            parent.workspace.selectedLines = []
            document.deleteText(cuts: plan.cuts, chapters: plan.chapters, headings: plan.headings, undoManager: parent.undoManager)
        }

        struct Deletion {
            var cuts: [UUID: [TextSpan]] = [:]
            var chapters = Set<UUID>()
            var headings: [UUID: TextSpan] = [:]
            var isEmpty: Bool { cuts.isEmpty && chapters.isEmpty && headings.isEmpty }
        }

        /// What a selection deletes: words inside paragraphs, words in a heading, or a whole
        /// chapter when its heading and every paragraph are selected.
        static func deletion(of selection: NSRange, project: Project, layout: LineLayout, total: Int) -> Deletion {
            var plan = Deletion()
            for (c, chapter) in project.chapters.enumerated() where c < layout.chapterStarts.count {
                let start = layout.chapterStarts[c]
                let end = c + 1 < layout.chapterStarts.count ? layout.chapterStarts[c + 1] : total
                guard NSIntersectionRange(selection, NSRange(location: start, length: end - start)).length > 0 else { continue }
                let headingEnd = chapter.paragraphs.first.flatMap { layout.paragraphRanges[$0.id]?.location } ?? end
                var everyParagraph = true
                var chapterCuts: [UUID: [TextSpan]] = [:]
                for p in chapter.paragraphs {
                    guard let r = layout.paragraphRanges[p.id] else { continue }
                    let hit = NSIntersectionRange(selection, r)
                    if hit.length == r.length, r.length > 0 {
                        chapterCuts[p.id] = [TextSpan(location: 0, length: r.length)]
                    } else {
                        everyParagraph = false
                        if hit.length > 0 { chapterCuts[p.id] = [TextSpan(location: hit.location - r.location, length: hit.length)] }
                    }
                }
                let title = NSRange(location: start, length: (chapter.title as NSString).length)
                if selection.location <= start, NSMaxRange(selection) >= headingEnd - 1, everyParagraph {
                    plan.chapters.insert(chapter.id)
                } else {
                    plan.cuts.merge(chapterCuts) { a, _ in a }
                    let inHeading = NSIntersectionRange(selection, title)
                    if inHeading.length > 0 { plan.headings[chapter.id] = TextSpan(location: inHeading.location - start, length: inHeading.length) }
                }
            }
            return plan
        }

        static func dot(_ color: NSColor) -> NSImage {
            NSImage(size: NSSize(width: 10, height: 10), flipped: false) { r in
                color.setFill()
                NSBezierPath(ovalIn: r.insetBy(dx: 1, dy: 1)).fill()
                return true
            }
        }
    }
}

extension NSAttributedString.Key {
    static let chapterIndex = NSAttributedString.Key("InnerVoiceChapterIndex")
}

/// A menu item that runs a closure.
nonisolated final class ClosureMenuItem: NSMenuItem {
    private let handler: @MainActor () -> Void
    init(title: String, handler: @escaping @MainActor () -> Void) {
        self.handler = handler
        super.init(title: title, action: #selector(run), keyEquivalent: "")
        target = self
    }
    required init(coder: NSCoder) { fatalError() }
    @objc private func run() {
        nonisolated(unsafe) let handler = self.handler
        MainActor.assumeIsolated { handler() }
    }
}

/// Clicks on a line open Assign To; a drag still selects text.
final class ReadingTextView: NSTextView {
    weak var coordinator: ManuscriptTextView.Coordinator?

    override func mouseDown(with event: NSEvent) {
        let point = convert(event.locationInWindow, from: nil)
        super.mouseDown(with: event) // runs the selection loop until mouse up
        guard event.clickCount == 1, selectedRange().length == 0 else {
            if selectedRange().length > 0, let c = coordinator {
                c.parent.workspace.selectedLines = c.lines(in: selectedRange())
            }
            return
        }
        // A name in the margin stands for its whole paragraph; ⌘-click picks several.
        let adding = event.modifierFlags.contains(.command)
        if coordinator?.marginClick(at: point, adding: adding) == true { return }
        if adding {
            coordinator?.toggle(at: characterIndexForInsertion(at: point))
            return
        }
        coordinator?.click(at: characterIndexForInsertion(at: point))
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        if let margin = coordinator?.marginMenu(at: point) { return margin }
        return coordinator?.menu(for: characterIndexForInsertion(at: point), selection: selectedRange())
    }

    override func keyDown(with event: NSEvent) {
        // Return opens Assign To for the selected line.
        if event.keyCode == 36 || event.keyCode == 76, let c = coordinator, let id = c.parent.workspace.selectedLines.first {
            c.requestAssign(id, group: c.pickedGroup(containing: id))
            return
        }
        // ⌥Space plays the selected line, ⌥⇧Space a table read from it, ⌥Esc stops (the
        // same as the Book menu, which misses keys while its window isn't key).
        let mods = event.modifierFlags.intersection([.option, .shift, .command, .control])
        if event.keyCode == 49, mods == .option || mods == [.option, .shift], let c = coordinator, c.document.project.showsVoices {
            if mods == .option, !c.parent.workspace.selectedLines.isEmpty {
                PlayCommand.playLine(document: c.document, workspace: c.parent.workspace)
            } else {
                PlayCommand.playFromHere(document: c.document, workspace: c.parent.workspace)
            }
            return
        }
        if event.keyCode == 53, mods == .option {
            SpeechPlayer.shared.stop()
            return
        }
        // Delete or Forward Delete removes the selected text from the project.
        if event.keyCode == 51 || event.keyCode == 117, selectedRange().length > 0 {
            coordinator?.deleteText(in: selectedRange())
            return
        }
        super.keyDown(with: event)
    }

    /// Edit › Delete works on a selection even though the reading view can't be typed in.
    override func delete(_ sender: Any?) {
        guard selectedRange().length > 0 else { return }
        coordinator?.deleteText(in: selectedRange())
    }

    override func validateUserInterfaceItem(_ item: any NSValidatedUserInterfaceItem) -> Bool {
        if item.action == #selector(delete(_:)) { return selectedRange().length > 0 }
        return super.validateUserInterfaceItem(item)
    }

    let margin = MarginOverlay()

    override func viewDidMoveToSuperview() {
        super.viewDidMoveToSuperview()
        if margin.superview == nil {
            margin.textView = self
            margin.frame = bounds
            margin.autoresizingMask = [.width, .height]
            addSubview(margin)
        }
    }

    override func setNeedsDisplay(_ invalidRect: NSRect) {
        super.setNeedsDisplay(invalidRect)
        margin.setNeedsDisplay(invalidRect)
    }
}

/// The speaker's initials (or "?" when the narrator is needed) in the left margin, with an
/// arrow for how to shade them (Casting Style). A subview, because the text view's own
/// drawing sits under its text layers.
final class MarginOverlay: NSView {
    weak var textView: ReadingTextView?

    /// ↓ or ↑ (doubled for 3 steps or more); nil when the pitch stays the narrator's.
    static func arrow(_ s: Shade) -> String? {
        switch s.pitch {
        case ...(-3): "↓↓"
        case -2 ... -1: "↓"
        case 1...2: "↑"
        case 3...: "↑↑"
        default: nil
        }
    }
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let tv = textView, let c = tv.coordinator, let lm = tv.layoutManager, let tc = tv.textContainer else { return }
        let project = c.project
        let origin = tv.textContainerOrigin
        let glyphs = lm.glyphRange(forBoundingRect: dirtyRect.offsetBy(dx: -origin.x, dy: -origin.y).insetBy(dx: -400, dy: -20), in: tc)
        let chars = lm.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
        let dark = effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        // Narration Tools with a Casting Style: an arrow for how to shade each character.
        let shades = project.showsNarrationTools && project.settings.castingStyle != nil ? c.document.narrationShades() : [:]
        var drawn = Set<Int>()
        for entry in c.layout.entries(in: chars) {
            guard let line = c.line(entry) else { continue }
            let needs = project.needsYou(line)
            guard project.settings.showInitials || needs else { continue }
            let firstGlyphs = lm.glyphRange(forCharacterRange: NSRange(location: entry.range.location, length: min(1, entry.range.length)), actualCharacterRange: nil)
            let first = lm.boundingRect(forGlyphRange: firstGlyphs, in: tc)
            let y = Int(first.minY)
            guard !drawn.contains(y) else { continue }
            drawn.insert(y)
            let speaker = project.character(line.speakerID)
            let color = needs ? NSColor.attention : NSColor(hexString: speaker?.colorHex ?? "#888888")
            // An unsure guess shows its name with a question mark ("Cyrus?"), in red.
            let named = speaker.map { c.builtMargin.label(for: $0) }
            var label = needs ? (named.map { $0 + "?" } ?? "?") : named ?? ""
            if !needs, let id = speaker?.id, let arrow = shades[id].flatMap(Self.arrow) { label += " " + arrow }
            LineLayoutManager.drawMarker(label, color: color, rightEdge: origin.x - 18, midY: origin.y + c.textBox(first).midY, dark: dark)
        }
    }
}

/// Draws each line's color behind the text: a fill (Highlight) or a ring (Outline),
/// dashed for thoughts, dotted for written text, red when the narrator is needed, and
/// the speaker's initials in the left margin.
nonisolated final class LineLayoutManager: NSLayoutManager {
    weak var coordinator: ManuscriptTextView.Coordinator?

    override func drawBackground(forGlyphRange glyphsToShow: NSRange, at origin: NSPoint) {
        super.drawBackground(forGlyphRange: glyphsToShow, at: origin)
        // AppKit draws on the main thread.
        nonisolated(unsafe) let me = self
        MainActor.assumeIsolated { me.drawLines(glyphsToShow, origin) }
    }

    @MainActor private func drawLines(_ glyphsToShow: NSRange, _ origin: NSPoint) {
        guard let c = coordinator, let container = textContainers.first else { return }
        let project = c.project
        let chars = characterRange(forGlyphRange: glyphsToShow, actualGlyphRange: nil)
        let dark = NSApp.effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
        let style = project.settings.style
        let selected = Set(c.parent.workspace.selectedLines)

        for entry in c.layout.entries(in: chars) {
            guard let line = c.line(entry) else { continue }
            let needs = project.needsYou(line)
            let speaker = project.character(line.speakerID)
            let color = needs ? NSColor.attention : NSColor(hexString: speaker?.colorHex ?? "#888888")
            let glyphs = glyphRange(forCharacterRange: entry.range, actualCharacterRange: nil)
            var rects: [NSRect] = []
            enumerateEnclosingRects(forGlyphRange: glyphs, withinSelectedGlyphRange: NSRange(location: NSNotFound, length: 0), in: container) { r, _ in
                rects.append(r.offsetBy(dx: origin.x, dy: origin.y))
            }
            let isSelected = selected.contains(line.id)
            for rect in rects {
                let box = c.textBox(rect).insetBy(dx: -3, dy: 0.5)
                if style == .underline {
                    // A colored rule under the words: solid for speech, dashed for thoughts,
                    // dotted for written text. Red when the narrator is needed.
                    let rule = NSBezierPath()
                    let y = rect.maxY - 1.5
                    rule.move(to: NSPoint(x: rect.minX, y: y))
                    rule.line(to: NSPoint(x: rect.maxX, y: y))
                    rule.lineWidth = 2.5
                    rule.lineCapStyle = .round
                    switch line.kind {
                    case .thought: rule.setLineDash([6, 4], count: 2, phase: 0)
                    case .written: rule.setLineDash([0.1, 4.5], count: 2, phase: 0)
                    case .spoken: break
                    }
                    color.withAlphaComponent(dark ? 0.95 : 0.85).setStroke()
                    rule.stroke()
                    if isSelected {
                        let ring = NSBezierPath(roundedRect: box.insetBy(dx: -2, dy: -2), xRadius: 6, yRadius: 6)
                        NSColor.controlAccentColor.setStroke()
                        ring.lineWidth = 2
                        ring.stroke()
                    }
                    continue
                }
                let path = NSBezierPath(roundedRect: box, xRadius: 4, yRadius: 4)
                switch line.kind {
                case .thought: path.setLineDash([4, 3], count: 2, phase: 0)
                case .written: path.setLineDash([1.5, 2.5], count: 2, phase: 0)
                case .spoken: break
                }
                if style == .highlight && line.kind == .spoken {
                    color.withAlphaComponent(dark ? 0.34 : 0.24).setFill()
                    path.fill()
                } else {
                    color.withAlphaComponent(dark ? 0.10 : 0.06).setFill()
                    path.fill()
                    color.withAlphaComponent(0.9).setStroke()
                    path.lineWidth = 1.5
                    path.stroke()
                }
                if isSelected {
                    let ring = NSBezierPath(roundedRect: box.insetBy(dx: -2, dy: -2), xRadius: 6, yRadius: 6)
                    NSColor.controlAccentColor.setStroke()
                    ring.lineWidth = 2
                    ring.stroke()
                }
            }
        }
    }

    @MainActor static let markerFont = NSFont.systemFont(ofSize: 10, weight: .bold)
    /// A full name longer than this is cut short with "…".
    static let markerMaxText: CGFloat = 150

    /// The label as drawn: short enough to fit the margin.
    @MainActor static func markerText(_ text: String) -> String {
        let attrs: [NSAttributedString.Key: Any] = [.font: markerFont]
        guard (text as NSString).size(withAttributes: attrs).width > markerMaxText else { return text }
        var cut = text
        while cut.count > 1, ((cut + "…") as NSString).size(withAttributes: attrs).width > markerMaxText { cut.removeLast() }
        return cut.trimmingCharacters(in: .whitespaces) + "…"
    }

    @MainActor static func markerWidth(_ text: String) -> CGFloat {
        let size = (markerText(text) as NSString).size(withAttributes: [.font: markerFont])
        return max(22, ceil(size.width) + 10)
    }

    /// A pill with the speaker's label, its right edge just left of the text.
    @MainActor static func drawMarker(_ raw: String, color: NSColor, rightEdge: CGFloat, midY: CGFloat, dark: Bool) {
        guard !raw.isEmpty else { return }
        let text = markerText(raw)
        let attrs: [NSAttributedString.Key: Any] = [.font: markerFont, .foregroundColor: dark ? color.blended(withFraction: 0.35, of: .white) ?? color : color.blended(withFraction: 0.25, of: .black) ?? color]
        let size = (text as NSString).size(withAttributes: attrs)
        let w = markerWidth(raw)
        let box = NSRect(x: rightEdge - w, y: midY - 9, width: w, height: 18)
        color.withAlphaComponent(dark ? 0.28 : 0.16).setFill()
        NSBezierPath(roundedRect: box, xRadius: 9, yRadius: 9).fill()
        (text as NSString).draw(at: NSPoint(x: box.midX - size.width / 2, y: box.midY - size.height / 2), withAttributes: attrs)
    }
}
