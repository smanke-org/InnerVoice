import InnerVoiceKit
import SwiftUI

/// Settings › Voices › Models and Voices: when each cloud service's lists were last read
/// from its API, what's new since, a Refresh button, and a warning when the chosen model
/// was retired. Shown once any cloud service is set up.
struct ServiceCatalogSection: View {
    private var catalogs: ServiceCatalogs { .shared }

    var body: some View {
        let services = CatalogService.allCases.filter { catalogs.isSetUp($0) }
        if !services.isEmpty {
            Section {
                ForEach(services) { ServiceCatalogRow(service: $0) }
            } header: {
                Text("Models and Voices")
            } footer: {
                Text("InnerVoice reads each service’s models and voices from its own API once a day, so new ones appear without an update. Only the lists are fetched; no book text is sent. Your chosen model never changes on its own. Gemini’s voices are built in because Google doesn’t list them.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
    }
}

struct ServiceCatalogRow: View {
    let service: CatalogService
    private var catalogs: ServiceCatalogs { .shared }

    var body: some View {
        let st = catalogs.state(service)
        LabeledContent {
            HStack(spacing: Space.sm) {
                if let new = st.newSummary {
                    Text(new)
                        .font(.caption.weight(.medium))
                        .padding(.horizontal, 6).padding(.vertical, 2)
                        .background(Color.accentColor.opacity(0.2), in: Capsule())
                        .help("Added since the refresh before. New models are marked New in the model menu.")
                    Button("Got It") { catalogs.markSeen(service) }.controlSize(.small)
                }
                Button {
                    Task { await catalogs.refresh(service) }
                } label: {
                    if catalogs.refreshing.contains(service) { ProgressView().controlSize(.small) } else { Text("Refresh") }
                }
                .controlSize(.small)
                .disabled(catalogs.refreshing.contains(service))
                .help("Read \(service.title)’s models and voices again now")
            }
        } label: {
            Text(service.title)
            Text(detail(st))
            if let warning = catalogs.retiredWarning(service) {
                Label(warning, systemImage: "exclamationmark.triangle.fill")
                    .foregroundStyle(Color(nsColor: .attention))
            }
            if let failure = catalogs.failures[service] {
                Text("Couldn’t refresh: \(failure)").foregroundStyle(Color(nsColor: .attention))
            }
        }
    }

    private func detail(_ st: CatalogState) -> String {
        var parts: [String] = []
        if !st.models.isEmpty { parts.append("\(st.models.count) model\(st.models.count == 1 ? "" : "s")") }
        if !st.voices.isEmpty { parts.append("\(st.voices.count.formatted()) voices") }
        let when = st.lastRefreshed.map { "Updated " + $0.formatted(.relative(presentation: .named)) } ?? "Not refreshed yet"
        return ([when] + parts).joined(separator: " · ")
    }
}
