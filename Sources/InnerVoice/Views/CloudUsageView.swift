import Charts
import InnerVoiceKit
import SwiftUI

/// Which cloud services this book is using right now: Claude when it's the chosen model,
/// ElevenLabs only when this book has it turned on (allowed, and its engine or one of Mix
/// Engines'). The Usage button shows only when there's at least one.
enum CloudInUse {
    @MainActor static func claude() -> Bool { ModelManager.shared.selection.hasPrefix("claude:") }

    @MainActor static func elevenLabs(_ project: Project) -> Bool {
        guard project.showsVoices, project.settings.cloudVoicesAllowed != nil, ElevenLabs.shared.isSetUp else { return false }
        let engine = project.engineInUse
        return engine == .elevenlabs || (engine == .mixed && EngineSwitches.shared.isOn(.elevenlabs))
    }

    @MainActor static func any(_ project: Project) -> Bool { claude() || elevenLabs(project) }
}

/// The toolbar's Usage button: tokens, cost and what's left for each cloud model, in a popover.
struct CloudUsageButton: View {
    @ObservedObject var document: InnerVoiceDocument
    @State private var showing = false

    var body: some View {
        Button("Cloud Usage", systemImage: "chart.bar.xaxis") { showing.toggle() }
            .help("Tokens, cost and what's left for the cloud models this book uses")
            .popover(isPresented: $showing, arrowEdge: .bottom) {
                CloudUsageView(project: document.project)
            }
    }
}

struct CloudUsageView: View {
    let project: Project
    @State private var store = CloudUsageStore.shared
    @AppStorage("usage.allBooks") private var allBooks = false
    @State private var eleven: ElevenLabs.Account?
    @State private var elevenProblem: String?

    var body: some View {
        let book = allBooks ? nil : project.usageKey
        let showClaude = CloudInUse.claude()
        let showEleven = CloudInUse.elevenLabs(project)
        let records = store.records.filter { ($0.service == .claude && showClaude) || ($0.service == .elevenlabs && showEleven) }
        let totals = CloudUsage.totals(records, book: book)
        let days = CloudUsage.byDay(records, book: book)
        let currentClaude = ModelManager.shared.selection.hasPrefix("claude:") ? String(ModelManager.shared.selection.dropFirst(7)) : nil
        VStack(alignment: .leading, spacing: Space.md) {
            HStack {
                Text("Cloud Usage").font(.title3.weight(.semibold))
                CloudBadge(service: showClaude && showEleven ? "Anthropic and ElevenLabs" : showClaude ? "Anthropic" : "ElevenLabs")
                Spacer()
                Picker("Show", selection: $allBooks) {
                    Text("This Book").tag(false)
                    Text("All Books").tag(true)
                }
                .pickerStyle(.segmented)
                .labelsHidden()
                .fixedSize()
            }

            if showClaude {
                section("Claude", systemImage: "sparkles") {
                    let claude = totals.filter { $0.service == .claude }
                    if claude.isEmpty {
                        empty("Nothing sent to Claude yet" + (allBooks ? "." : " for this book."))
                    }
                    ForEach(claude, id: \.model) { t in
                        modelRow(name: ClaudeAccount.shared.name(of: t.model), current: t.model == currentClaude,
                                 lines: [
                                    ("Tokens in", t.inputTokens + t.cacheReadTokens + t.cacheWriteTokens),
                                    ("Tokens out", t.outputTokens),
                                    ("Requests", t.requests),
                                 ],
                                 cost: t.cost.map { $0.formatted(.currency(code: "USD").precision(.fractionLength(2...4))) } ?? "Price not known")
                        if let left = store.claudeTokensLeft[t.model] {
                            Text("\(left.formatted()) tokens left in this minute’s rate limit")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    }
                    if claude.count > 1, let sum = Optional(claude.compactMap(\.cost).reduce(0, +)) {
                        Text("All Claude models: \(sum.formatted(.currency(code: "USD").precision(.fractionLength(2...4))))")
                            .font(.callout.weight(.medium))
                    }
                    Text("Costs are worked out from Anthropic’s list prices and may differ from your bill. Your account’s remaining credit is in the Claude Console.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            if showEleven {
                section("ElevenLabs", systemImage: "waveform") {
                    let el = totals.filter { $0.service == .elevenlabs }
                    if el.isEmpty { empty("No ElevenLabs voices made yet" + (allBooks ? "." : " for this book.")) }
                    ForEach(el, id: \.model) { t in
                        modelRow(name: t.model, current: true,
                                 lines: [("Characters", t.characters), ("Requests", t.requests)], cost: nil)
                    }
                    if let eleven {
                        let left = max(0, eleven.limit - eleven.used)
                        VStack(alignment: .leading, spacing: 2) {
                            ProgressView(value: Double(eleven.used), total: Double(max(1, eleven.limit)))
                            Text("Your \(eleven.tier.isEmpty ? "" : eleven.tier.capitalized + " ")plan: \(eleven.used.formatted()) of \(eleven.limit.formatted()) credits used, \(left.formatted()) left this period")
                                .font(.caption).foregroundStyle(.secondary)
                        }
                    } else if let elevenProblem {
                        Text(elevenProblem).font(.caption).foregroundStyle(.secondary)
                    } else {
                        ProgressView().controlSize(.small)
                    }
                }
            }

            if !days.isEmpty {
                Text("By day").font(.headline)
                Chart(days, id: \.self) { r in
                    BarMark(x: .value("Day", date(r.day), unit: .day),
                            y: .value(r.service == .claude ? "Tokens" : "Characters", r.service == .claude ? r.totalTokens : r.characters))
                        .foregroundStyle(by: .value("Model", label(r)))
                }
                .chartYAxisLabel(showClaude && showEleven ? "Tokens (Claude) / characters (ElevenLabs)" : showClaude ? "Tokens" : "Characters")
                .frame(height: 160)
                let costDays = days.filter { $0.cost != nil }
                if !costDays.isEmpty {
                    Text("Cost by day").font(.headline)
                    Chart(costDays, id: \.self) { r in
                        BarMark(x: .value("Day", date(r.day), unit: .day), y: .value("Dollars", r.cost ?? 0))
                            .foregroundStyle(by: .value("Model", label(r)))
                    }
                    .chartYAxis { AxisMarks { v in AxisGridLine(); AxisValueLabel { if let d = v.as(Double.self) { Text(d, format: .currency(code: "USD")) } } } }
                    .frame(height: 120)
                }
            }
        }
        .padding(Space.lg)
        .frame(width: 460)
        .task(id: showEleven) {
            guard showEleven, !SystemHooks.isTestRun else { return }
            do { eleven = try await ElevenLabs.shared.account() } catch { elevenProblem = "Couldn’t read your ElevenLabs credits: \(error.localizedDescription)" }
        }
    }

    private func label(_ r: CloudUsageRecord) -> String {
        r.service == .claude ? ClaudeAccount.shared.name(of: r.model) : "ElevenLabs (\(r.model))"
    }

    private func date(_ day: String) -> Date {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.dateFormat = "yyyy-MM-dd"
        return f.date(from: day) ?? .now
    }

    private func section<Content: View>(_ title: String, systemImage: String, @ViewBuilder content: () -> Content) -> some View {
        VStack(alignment: .leading, spacing: Space.xs) {
            Label(title, systemImage: systemImage).font(.headline)
            content()
        }
        .padding(Space.md)
        .frame(maxWidth: .infinity, alignment: .leading)
        .background(.quaternary.opacity(0.35), in: RoundedRectangle(cornerRadius: 10))
    }

    private func empty(_ text: String) -> some View {
        Text(text).font(.callout).foregroundStyle(.secondary)
    }

    private func modelRow(name: String, current: Bool, lines: [(String, Int)], cost: String?) -> some View {
        VStack(alignment: .leading, spacing: 2) {
            HStack {
                Text(name).fontWeight(.medium)
                if current { Text("In use").font(.caption2.weight(.semibold)).foregroundStyle(.secondary) }
                Spacer()
                if let cost { Text(cost).font(.callout.monospacedDigit().weight(.semibold)) }
            }
            HStack(spacing: Space.lg) {
                ForEach(lines, id: \.0) { l in
                    VStack(alignment: .leading, spacing: 0) {
                        Text(l.1.formatted()).font(.callout.monospacedDigit())
                        Text(l.0).font(.caption2).foregroundStyle(.secondary)
                    }
                }
            }
        }
    }
}
