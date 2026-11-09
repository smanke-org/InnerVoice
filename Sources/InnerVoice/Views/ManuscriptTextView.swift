import AppKit
import InnerVoiceKit
import SwiftUI

/// Reading settings shared by the manuscript and Record.
enum ReadingStyle {
    static var fontSize: CGFloat {
        let v = UserDefaults.standard.double(forKey: "reading.fontSize")
        return v > 0 ? v : 16
    }
    static var serif: Bool { UserDefaults.standard.object(forKey: "reading.serif") as? Bool ?? true }

    static func font(size: CGFloat, weight: NSFont.Weight = .regular) -> NSFont {
        let base = NSFont.systemFont(ofSize: size, weight: weight)
        if serif, let d = base.fontDescriptor.withDesign(.serif) { return NSFont(descriptor: d, size: size) ?? base }
        return base
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
    var onAssignRequest: (UUID, NSRect, NSView) -> Void
    var onVisibleChapter: (Int) -> Void

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
        if c.builtText != document.textRevision || c.builtFontSize != ReadingStyle.fontSize || c.builtSerif != ReadingStyle.serif {
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
        if let pending = workspace.pendingAssign {
            DispatchQueue.main.async {
                workspace.pendingAssign = nil
                c.requestAssign(pending)
            }
        }
        if let reveal = workspace.reveal, reveal != c.handledReveal {
            c.handledReveal = reveal
            DispatchQueue.main.async { c.reveal(reveal) }
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
        var builtSelection: [UUID] = []
        var handledReveal: Workspace.Reveal?
        private var lastReportedChapter = -1

        init(_ parent: ManuscriptTextView) { self.parent = parent }

        var document: InnerVoiceDocument { parent.document }
        var project: Project { parent.document.project }

        // MARK: Building

        func rebuild() {
            guard let textView else { return }
            builtText = document.textRevision
            builtTags = document.tagRevision
            builtLines = document.lineRevision
            builtFontSize = ReadingStyle.fontSize
            builtSerif = ReadingStyle.serif
            let (text, layout) = Self.build(project: project, index: document.index, fontSize: builtFontSize)
            self.layout = layout
            textView.textStorage?.setAttributedString(text)
            didResize()
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

        /// The line an entry stands for, if it still exists.
        func line(_ e: LineLayout.Entry) -> Line? {
            document.index.lineIndexByID[e.lineID].map { project.lines[$0] }
        }

        static func build(project: Project, index: BookIndex, fontSize: CGFloat) -> (NSAttributedString, LineLayout) {
            let out = NSMutableAttributedString()
            var layout = LineLayout()
            let body = ReadingStyle.font(size: fontSize)
            let heading = ReadingStyle.font(size: round(fontSize * 1.55), weight: .semibold)
            let bodyStyle = NSMutableParagraphStyle()
            bodyStyle.lineHeightMultiple = 1.32
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
                out.append(NSAttributedString(string: chapter.title + "\n", attributes: [
                    .font: heading, .foregroundColor: NSColor.labelColor, .paragraphStyle: headingStyle,
                    .chapterIndex: c,
                ]))
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

        /// Centers a comfortable reading measure and leaves a gutter for initials.
        @objc func didResize() {
            guard let textView, let scroll = textView.enclosingScrollView, let container = textView.textContainer else { return }
            let width = scroll.contentSize.width
            let measure = min(max(360, width - 140), builtFontSize * 42)
            let side = max(72, (width - measure) / 2)
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

        func requestAssign(_ lineID: UUID) {
            guard let textView, let e = layout.entries.first(where: { $0.lineID == lineID }) else { return }
            let rects = rects(for: e.range)
            guard let rect = rects.first else { return }
            parent.onAssignRequest(lineID, rect, textView)
        }

        func lines(in range: NSRange) -> [UUID] {
            layout.entries(in: range).compactMap { line($0)?.id }
        }

        func menu(for char: Int, selection: NSRange) -> NSMenu {
            let menu = NSMenu()
            var ids = selection.length > 0 ? lines(in: selection) : []
            if ids.isEmpty, let e = layout.entry(at: char), let l = line(e) { ids = [l.id] }
            let undo = parent.undoManager
            if !ids.isEmpty {
                parent.workspace.selectedLines = ids
                let title = ids.count == 1 ? "Assign To" : "Assign \(ids.count) Lines To"
                let assign = NSMenuItem(title: title, action: nil, keyEquivalent: "")
                let sub = NSMenu()
                let index = document.index
                for c in project.characters.sorted(by: { (index.stats[$0.id]?.lines ?? 0) > (index.stats[$1.id]?.lines ?? 0) }) {
                    let item = ClosureMenuItem(title: c.name) { [weak self] in self?.document.assign(ids, to: c.id, undoManager: undo) }
                    item.image = Self.dot(NSColor(hexString: c.colorHex))
                    sub.addItem(item)
                }
                sub.addItem(.separator())
                sub.addItem(ClosureMenuItem(title: "Who's Speaking? (Clear)") { [weak self] in
                    self?.document.assign(ids, to: nil, undoManager: undo)
                })
                assign.submenu = sub
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
            return menu
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
        coordinator?.click(at: characterIndexForInsertion(at: point))
    }

    override func menu(for event: NSEvent) -> NSMenu? {
        let point = convert(event.locationInWindow, from: nil)
        return coordinator?.menu(for: characterIndexForInsertion(at: point), selection: selectedRange())
    }

    override func keyDown(with event: NSEvent) {
        // Return opens Assign To for the selected line.
        if event.keyCode == 36 || event.keyCode == 76, let id = coordinator?.parent.workspace.selectedLines.first {
            coordinator?.requestAssign(id)
            return
        }
        super.keyDown(with: event)
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

/// The speaker's initials (or "?" when the narrator is needed) in the left margin. A
/// subview, because the text view's own drawing sits under its text layers.
final class MarginOverlay: NSView {
    weak var textView: ReadingTextView?
    override var isFlipped: Bool { true }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let tv = textView, let c = tv.coordinator, let lm = tv.layoutManager, let tc = tv.textContainer else { return }
        let project = c.project
        let origin = tv.textContainerOrigin
        let glyphs = lm.glyphRange(forBoundingRect: dirtyRect.offsetBy(dx: -origin.x, dy: -origin.y).insetBy(dx: -400, dy: -20), in: tc)
        let chars = lm.characterRange(forGlyphRange: glyphs, actualGlyphRange: nil)
        let dark = effectiveAppearance.bestMatch(from: [.aqua, .darkAqua]) == .darkAqua
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
            LineLayoutManager.drawMarker(needs ? "?" : (speaker?.initials ?? ""), color: color,
                                         at: NSPoint(x: origin.x - 40, y: origin.y + first.midY), dark: dark)
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
                let box = rect.insetBy(dx: -3, dy: 0.5)
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

    @MainActor static func drawMarker(_ text: String, color: NSColor, at center: NSPoint, dark: Bool) {
        guard !text.isEmpty else { return }
        let font = NSFont.systemFont(ofSize: 10, weight: .bold)
        let attrs: [NSAttributedString.Key: Any] = [.font: font, .foregroundColor: dark ? color.blended(withFraction: 0.35, of: .white) ?? color : color.blended(withFraction: 0.25, of: .black) ?? color]
        let size = (text as NSString).size(withAttributes: attrs)
        let w = max(22, size.width + 10)
        let box = NSRect(x: center.x - w / 2, y: center.y - 9, width: w, height: 18)
        color.withAlphaComponent(dark ? 0.28 : 0.16).setFill()
        NSBezierPath(roundedRect: box, xRadius: 9, yRadius: 9).fill()
        (text as NSString).draw(at: NSPoint(x: box.midX - size.width / 2, y: box.midY - size.height / 2), withAttributes: attrs)
    }
}
