import AppKit
import SwiftUI

/// Each book's window opens where it was last left, at the size it was: the frame is saved
/// per book (by `Project.usageKey`, which survives moving or renaming the file) whenever the
/// window moves or resizes, and restored when the book opens again. A frame that's no longer
/// on any screen (a display unplugged) is left alone, so the window opens where macOS puts it.
/// Test runs neither save nor restore.
enum ProjectWindowFrames {
    static let key = "windows.projectFrames"

    static func frame(for book: String) -> NSRect? {
        guard let s = (UserDefaults.standard.dictionary(forKey: key) as? [String: String])?[book] else { return nil }
        let r = NSRectFromString(s)
        return r.width > 100 && r.height > 100 ? r : nil
    }

    static func save(_ frame: NSRect, for book: String) {
        var all = UserDefaults.standard.dictionary(forKey: key) as? [String: String] ?? [:]
        all[book] = NSStringFromRect(frame)
        UserDefaults.standard.set(all, forKey: key)
    }
}

extension View {
    func remembersWindowFrame(book: String) -> some View {
        background(FrameHook(book: book))
    }
}

private struct FrameHook: NSViewRepresentable {
    let book: String

    func makeNSView(context: Context) -> Probe { Probe(book: book) }
    func updateNSView(_ probe: Probe, context: Context) { probe.book = book }

    final class Probe: NSView {
        var book: String
        private var restored = false

        init(book: String) {
            self.book = book
            super.init(frame: .zero)
        }

        required init?(coder: NSCoder) { fatalError() }

        override func viewDidMoveToWindow() {
            super.viewDidMoveToWindow()
            guard let window, !SystemHooks.isTestRun else { return }
            NotificationCenter.default.removeObserver(self)
            for name in [NSWindow.didMoveNotification, NSWindow.didEndLiveResizeNotification, NSWindow.didResizeNotification] {
                NotificationCenter.default.addObserver(self, selector: #selector(changed), name: name, object: window)
            }
            guard !restored else { return }
            restored = true
            // After SwiftUI has placed the window, so its own placement doesn't win.
            DispatchQueue.main.async { [weak self] in
                guard let self, let window = self.window, let saved = ProjectWindowFrames.frame(for: self.book),
                      NSScreen.screens.contains(where: { $0.visibleFrame.intersects(saved) }) else { return }
                window.setFrame(saved, display: true)
            }
        }

        @objc private func changed(_ note: Notification) {
            guard restored, let window, !window.styleMask.contains(.fullScreen), !book.isEmpty else { return }
            ProjectWindowFrames.save(window.frame, for: book)
        }
    }
}
