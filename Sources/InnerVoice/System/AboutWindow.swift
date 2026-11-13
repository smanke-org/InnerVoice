import AppKit
import SwiftUI

/// About InnerVoice: the icon, version and copyright, then what changed in the last 10
/// releases, from their GitHub release notes. (The standard About panel can't show notes
/// that arrive after it opens.)
final class AboutWindow {
    static let shared = AboutWindow()

    /// How many releases' notes it shows.
    static let releaseCount = 10

    private var window: NSWindow?

    func show() {
        if window == nil {
            let window = NSWindow(contentViewController: NSHostingController(rootView: AboutView()))
            window.title = "About \(AppInfo.name)"
            window.styleMask = [.titled, .closable]
            window.isReleasedWhenClosed = false
            window.center()
            self.window = window
        }
        NSApp.activate(ignoringOtherApps: true)
        window?.makeKeyAndOrderFront(nil)
    }

    #if DEBUG
    var debugWindow: NSWindow? { window }
    #endif
}

private struct AboutView: View {
    @State private var entries: [ReleaseNotes.Entry]?

    private var copyright: String {
        Bundle.main.infoDictionary?["NSHumanReadableCopyright"] as? String ?? ""
    }

    var body: some View {
        VStack(alignment: .leading, spacing: Space.md) {
            HStack(spacing: Space.md) {
                Image(nsImage: NSApp.applicationIconImage).resizable().frame(width: 64, height: 64)
                VStack(alignment: .leading, spacing: Space.xs) {
                    Text(AppInfo.name).font(.title2.weight(.semibold))
                    Text("Version \(AppInfo.labeledVersion)").foregroundStyle(.secondary)
                    if !copyright.isEmpty { Text(copyright).font(.caption).foregroundStyle(.secondary) }
                }
            }
            VStack(alignment: .leading, spacing: Space.xs) {
                Text("Acknowledgments").font(.headline)
                ForEach(Credits.all, id: \.text) { credit in
                    HStack(alignment: .firstTextBaseline, spacing: Space.xs) {
                        Text(credit.text).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
                        Link("License", destination: credit.license).font(.caption)
                    }
                }
            }
            Text("Recent releases").font(.headline)
            Group {
                if let entries, !entries.isEmpty {
                    ReleaseNotesList(entries: entries)
                } else if entries != nil {
                    VStack(alignment: .leading, spacing: Space.sm) {
                        Text("The release notes couldn't be loaded right now.")
                        Link("Read them on GitHub", destination: ReleaseNotes.allReleasesURL)
                    }
                    .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
                } else {
                    ProgressView().frame(maxWidth: .infinity, maxHeight: .infinity)
                }
            }
            .frame(height: 360)
            HStack {
                Link("All releases on GitHub", destination: ReleaseNotes.allReleasesURL)
                Spacer()
            }
        }
        .padding(Space.margin)
        .frame(width: 500)
        .noInitialFocus()
        // Up to the version running; newer ones are the update prompt's to show.
        .task { entries = await ReleaseNotes.fetch(newAfter: AppInfo.version, through: AppInfo.version,
                                                  count: AboutWindow.releaseCount) }
    }
}

/// Work by others that ships with InnerVoice or that it downloads, credited as their
/// licenses ask (Creative Commons Attribution needs the creator, the license, a link and
/// any changes). Shown in About and in Help › Voices & Listening.
enum Credits {
    struct Credit {
        var text: String
        var license: URL
    }

    static let ccBY4 = URL(string: "https://creativecommons.org/licenses/by/4.0/")!

    static let all: [Credit] = [
        Credit(text: "Pocket TTS voices: the Pocket TTS model and its Mimi codec by Kyutai (kyutai.org), CC BY 4.0, in mlx-community’s MLX conversion (mlx-community/pocket-tts).",
               license: ccBY4),
        Credit(text: "The stock voice Alba: a clip by Alba MacKenna from Kyutai’s voice collection (kyutai/tts-voices), CC BY 4.0, converted to 24 kHz mono.",
               license: ccBY4),
        Credit(text: "The other eight stock voices were spoken by Orpheus TTS by Canopy Labs, Apache 2.0.",
               license: URL(string: "https://www.apache.org/licenses/LICENSE-2.0")!),
    ]
}
