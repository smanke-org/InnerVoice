import SwiftUI

/// The iCloud settings sync and update choices, as sections of General's grouped Form.
struct SyncUpdateSections: View {
    @ObservedObject private var sync = SettingsSync.shared
    @ObservedObject private var updater = AutoUpdater.shared

    var body: some View {
        Section {
            Toggle("Sync settings with your other Macs", isOn: $sync.isEnabled)
            if sync.isEnabled {
                LabeledContent {
                    Button("Sync Now") { sync.syncNow() }.controlSize(.small)
                } label: {
                    Group {
                        if let error = sync.lastError {
                            Text(error).foregroundStyle(.red)
                        } else if let date = sync.lastSynced {
                            Text("Synced \(date.formatted(date: .omitted, time: .shortened))\(sync.lastSource.map { $0 == (Host.current().localizedName ?? "") ? "" : " from \($0)" } ?? "")")
                                .foregroundStyle(.secondary)
                        } else {
                            Text("Waiting for iCloud Drive…").foregroundStyle(.secondary)
                        }
                    }
                    .font(.caption)
                }
            }
        } header: {
            Text("iCloud")
        } footer: {
            SystemCaption("Appearance, the accent color, asking before quitting, where Inner Voice appears and the update choices follow you to every Mac signed in to the same iCloud account with sync turned on. Your projects never sync: they stay where you saved them.")
        }

        Section {
            Picker("When an update is available", selection: $updater.policy) {
                ForEach(UpdatePolicy.allCases, id: \.self) { Text($0.title).tag($0) }
            }
            if updater.policy != .off {
                UpdateIntervalPicker(interval: $updater.interval)
            }
            if let staged = updater.staged {
                LabeledContent("Version \(staged.version) is ready") {
                    Button("Install Now") { updater.installNow() }
                }
            }
            LabeledContent("Version \(AppInfo.displayVersion)") {
                HStack {
                    Button("What's New…") { WhatsNew.shared.show(asked: true) }
                    Button("Check Now") { UpdateController.checkForUpdates() }
                }
            }
            if let error = updater.lastError {
                Text(error).font(.caption).foregroundStyle(.red)
            }
            if updater.policy != .off && !updater.notificationsAllowed {
                LabeledContent {
                    Button("Open Notification Settings") { AutoUpdater.openNotificationSettings() }
                } label: {
                    Label("Notifications are off for Inner Voice, so update news only shows in the menus.", systemImage: "bell.slash")
                        .font(.caption).foregroundStyle(.orange)
                }
            }
        } header: {
            Text("Updates")
        } footer: {
            SystemCaption(updateFooter)
        }
        .onAppear { updater.refreshNotificationStatus() }
    }

    private var updateFooter: String {
        switch updater.policy {
        case .automatic:
            return "Inner Voice checks \(updater.interval.phrase). A new version installs itself when Tag Speakers and Build Summaries aren't running, no app is presenting or keeping the screen awake, and your Mac hasn't been used for 2 minutes. Your projects are saved first. You'll get a notification when it's done."
        case .ask:
            return "Inner Voice checks \(updater.interval.phrase) and sends a notification when a new version is out. Nothing installs until you choose to."
        case .off:
            return "Inner Voice won't look for updates on its own. Use Check Now whenever you like."
        }
    }
}

/// A small gray explanation under a setting.
struct SystemCaption: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text).font(.caption).foregroundStyle(.secondary).fixedSize(horizontal: false, vertical: true)
    }
}
