import Foundation

/// InnerVoice's own menu shortcuts, in one place: ShortcutsLens gets them in its report, and
/// Help's Keyboard Shortcuts page lists them. InnerVoice registers no global shortcuts; these
/// work only while it is in front. Keep in step with the menus (App.swift's commands).
enum ShortcutCatalog {
    /// The project window's pages, in sidebar order: ⌘1 is the first.
    static let pages = ["Manuscript", "Cast", "Index", "Pronunciations", "Teleprompter", "Review", "Voices"]

    // Carbon modifiers.
    private static let command = 0x100
    private static let shift = 0x200

    /// Virtual key codes (kVK_ANSI_…) for ⌘1–⌘7.
    private static let digitKeyCodes = [18, 19, 20, 21, 23, 22, 26]
    private static let option = 0x800

    static var reported: [ShortcutsLensReport.Shortcut] {
        var list: [ShortcutsLensReport.Shortcut] = [
            .init(id: "newFromManuscript", title: "New from Manuscript…", keyCode: 45, carbonModifiers: command,
                  category: "File", scope: "app"),
            .init(id: "tagSpeakers", title: "Tag Speakers", keyCode: 17, carbonModifiers: command | shift,
                  category: "Book", scope: "app"),
            .init(id: "projectSettings", title: "Project Settings…", keyCode: 43, carbonModifiers: command | option,
                  category: "Book", scope: "app"),
            .init(id: "nextNeedsYou", title: "Next Line That Needs You", keyCode: 125, carbonModifiers: command,
                  category: "Lines", scope: "app"),
            .init(id: "previousNeedsYou", title: "Previous Line That Needs You", keyCode: 126, carbonModifiers: command,
                  category: "Lines", scope: "app"),
            .init(id: "playLine", title: "Play Line", keyCode: 49, carbonModifiers: option,
                  category: "Listening", scope: "app"),
            .init(id: "playFromHere", title: "Play from Here", keyCode: 49, carbonModifiers: option | shift,
                  category: "Listening", scope: "app"),
            .init(id: "stopPlaying", title: "Stop Playing", keyCode: 53, carbonModifiers: option,
                  category: "Listening", scope: "app"),
        ]
        for (index, page) in pages.enumerated() where index < digitKeyCodes.count {
            list.append(.init(id: "page.\(page.lowercased())", title: "Show \(page)", keyCode: digitKeyCodes[index],
                              carbonModifiers: command, category: "View", scope: "app"))
        }
        return list
    }
}
