import Charts
import InnerVoiceKit
import SwiftUI

/// Settings › Activity: what InnerVoice has done for the narrator. Numbers only, kept on
/// this Mac (and the person's other Macs with settings sync on), never anything from a book.
struct ActivitySettings: View {
    @AppStorage("activity.allMacs") private var allMacs = true
    private var store: ActivityStore { .shared }

    private var log: ActivityLog { store.combined(allMacs: allMacs && store.allMacsAvailable) }
    private var totals: [String: Int] { log.lifetime }

    var body: some View {
        Section {
            if store.allMacsAvailable {
                Picker("Show", selection: $allMacs) {
                    Text("This Mac").tag(false)
                    Text(store.otherMacNames.isEmpty ? "All Macs" : "All Macs (\(store.otherMacNames.count + 1))").tag(true)
                }
                .pickerStyle(.segmented)
            }
            headline
            chart
        } footer: {
            Text("Only numbers are kept: on this Mac, and on your other Macs when settings sync is on. Nothing from your books is counted or sent.")
                .font(.caption)
                .foregroundStyle(.secondary)
        }
        Section("Speakers") {
            row("Tagged by the rules (“said Tom”)", totals[ActivityKey.linesRules])
            row("Tagged by the AI", totals[ActivityKey.linesAI])
            row("Assigned by you", totals[ActivityKey.linesByHand])
            row("Guesses you kept", totals[ActivityKey.guessesKept], of: (totals[ActivityKey.guessesKept] ?? 0) + (totals[ActivityKey.guessesChanged] ?? 0))
            row("Tones the AI read", totals[ActivityKey.tonesAI])
            row("Tones you kept", totals[ActivityKey.tonesKept], of: (totals[ActivityKey.tonesKept] ?? 0) + (totals[ActivityKey.tonesChanged] ?? 0))
        }
        let models = ModelAccuracy.all(in: totals)
        if !models.isEmpty {
            Section {
                ForEach(models) { m in
                    LabeledContent(m.model) {
                        Text("\(Int((m.rate * 100).rounded()))% kept of \(m.reviewed.formatted()) reviewed")
                            .monospacedDigit()
                            .foregroundStyle(.secondary)
                    }
                }
            } header: {
                Text("AI Accuracy on Your Books")
            } footer: {
                Text("Of the AI’s guesses you checked, how many you kept, by model. It also shows in Choose a Model.")
                    .font(.caption)
                    .foregroundStyle(.secondary)
            }
        }
        Section("Narration") {
            row("Character facts gathered", totals[ActivityKey.facts])
            row("Pronunciations confirmed", totals[ActivityKey.pronunciations])
            row("Tags carried over to new drafts", totals[ActivityKey.linesCarriedOver])
        }
        Section("AI Voices") {
            LabeledContent("Audio played") { Text(duration(totals[ActivityKey.secondsPlayed] ?? 0)).monospacedDigit().foregroundStyle(.secondary) }
            LabeledContent("Audio exported") { Text(duration(totals[ActivityKey.secondsExported] ?? 0)).monospacedDigit().foregroundStyle(.secondary) }
            row("Voices you kept", totals[ActivityKey.voicesKept], of: (totals[ActivityKey.voicesKept] ?? 0) + (totals[ActivityKey.voicesChanged] ?? 0))
            if let chars = totals[ActivityKey.elevenLabsCharacters], chars > 0 {
                LabeledContent("Characters sent to ElevenLabs") { Text(chars.formatted()).monospacedDigit().foregroundStyle(.secondary) }
            }
        }
        .onAppear { store.loadRemotes() }
    }

    // MARK: The headline

    private var tagged: Int { (totals[ActivityKey.linesRules] ?? 0) + (totals[ActivityKey.linesAI] ?? 0) }

    private var headline: some View {
        let reviewed = (totals[ActivityKey.guessesKept] ?? 0) + (totals[ActivityKey.guessesChanged] ?? 0)
        let kept = reviewed == 0 ? nil : Double(totals[ActivityKey.guessesKept] ?? 0) / Double(reviewed)
        let saved = Double(tagged) * ActivityKey.secondsPerLineByHand
        let audio = (totals[ActivityKey.secondsPlayed] ?? 0) + (totals[ActivityKey.secondsExported] ?? 0)
        return HStack(alignment: .top, spacing: Space.lg) {
            stat(tagged.formatted(), "lines tagged for you")
            stat(kept.map { "\(Int(($0 * 100).rounded()))%" } ?? "—", "of guesses kept")
            stat(saved < 3600 ? "\(Int((saved / 60).rounded())) min" : String(format: "%.1f h", saved / 3600), "saved (estimate)", help: "An estimate: \(Int(ActivityKey.secondsPerLineByHand)) seconds to find and mark each line’s speaker by hand.")
            stat(duration(audio), "of audio")
        }
        .padding(.vertical, Space.xs)
    }

    private func stat(_ value: String, _ label: String, help: String? = nil) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            Text(value).font(.title2.weight(.semibold)).monospacedDigit()
            Text(label).font(.caption).foregroundStyle(.secondary)
        }
        .frame(maxWidth: .infinity, alignment: .leading)
        .help(help ?? "")
    }

    // MARK: The graph: lines tagged for you, last 30 days

    private var chart: some View {
        let cal = Calendar.current
        let today = cal.startOfDay(for: .now)
        let counts = log.counts(keys: ActivityKey.taggedForYou)
        let days = (0..<30).reversed().compactMap { cal.date(byAdding: .day, value: -$0, to: today) }
        return VStack(alignment: .leading, spacing: Space.xs) {
            Text("Lines tagged for you, last 30 days").font(.caption).foregroundStyle(.secondary)
            Chart(days, id: \.self) { day in
                BarMark(x: .value("Day", day, unit: .day), y: .value("Lines", counts[day] ?? 0))
                    .foregroundStyle(Palette.accent)
            }
            .chartXAxis { AxisMarks(values: .stride(by: .day, count: 7)) { AxisValueLabel(format: .dateTime.month(.abbreviated).day()) } }
            .frame(height: 110)
        }
    }

    // MARK: Rows

    private func row(_ label: String, _ value: Int?, of total: Int? = nil) -> some View {
        LabeledContent(label) {
            Group {
                if let total, total > 0 {
                    Text("\((value ?? 0).formatted()) of \(total.formatted()) (\(Int((Double(value ?? 0) / Double(total) * 100).rounded()))%)")
                } else {
                    Text((value ?? 0).formatted())
                }
            }
            .monospacedDigit()
            .foregroundStyle(.secondary)
        }
    }

    private func duration(_ seconds: Int) -> String {
        if seconds < 60 { return "\(seconds) s" }
        if seconds < 3600 { return "\(seconds / 60) min" }
        return String(format: "%.1f h", Double(seconds) / 3600)
    }
}
