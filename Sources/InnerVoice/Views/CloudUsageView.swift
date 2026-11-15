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

    @MainActor static func gemini(_ project: Project) -> Bool {
        guard project.showsVoices, project.settings.googleVoicesAllowed != nil, GeminiVoices.shared.isSetUp else { return false }
        let engine = project.engineInUse
        return engine == .gemini || (engine == .mixed && EngineSwitches.shared.isOn(.gemini))
    }

    @MainActor static func any(_ project: Project) -> Bool { claude() || elevenLabs(project) || gemini(project) }
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
        // A service shows while it's in use, and always once it has usage here: an engine
        // switched off later still cost what it cost.
        let used = Set(store.records.filter { book == nil || $0.book == book }.map(\.service))
        let showClaude = CloudInUse.claude() || used.contains(.claude)
        let showEleven = CloudInUse.elevenLabs(project) || used.contains(.elevenlabs)
        let showGemini = CloudInUse.gemini(project) || used.contains(.google)
        let records = store.records.filter {
            ($0.service == .claude && showClaude) || ($0.service == .elevenlabs && showEleven) || ($0.service == .google && showGemini)
        }
        let services = [showClaude ? "Anthropic" : nil, showEleven ? "ElevenLabs" : nil, showGemini ? "Google" : nil].compactMap { $0 }
        let totals = CloudUsage.totals(records, book: book)
        let days = CloudUsage.byDay(records, book: book)
        let currentClaude = ModelManager.shared.selection.hasPrefix("claude:") ? String(ModelManager.shared.selection.dropFirst(7)) : nil
        VStack(alignment: .leading, spacing: Space.md) {
            HStack {
                Text("Cloud Usage").font(.title3.weight(.semibold))
                CloudBadge(service: services.formatted(.list(type: .and)))
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

            if showGemini {
                section("Gemini TTS", systemImage: "waveform") {
                    let g = totals.filter { $0.service == .google }
                    if g.isEmpty { empty("No Gemini voices made yet" + (allBooks ? "." : " for this book.")) }
                    ForEach(g, id: \.model) { t in
                        modelRow(name: t.model, current: t.model == GeminiVoices.shared.model.rawValue,
                                 lines: [("Characters", t.characters), ("Seconds of audio", Int(t.audioSeconds ?? 0)), ("Requests", t.requests)],
                                 cost: t.cost.map { $0.formatted(.currency(code: "USD").precision(.fractionLength(2...4))) } ?? "Price not known")
                    }
                    Text("Costs are worked out from Google’s paid-tier list prices (which double on January 1, 2027) and may differ from your bill. Your spending is in Google AI Studio.")
                        .font(.caption).foregroundStyle(.secondary)
                        .fixedSize(horizontal: false, vertical: true)
                }
            }

            if !days.isEmpty {
                // Tokens and characters on separate charts: a day of Claude's millions of
                // tokens would flatten the voices' characters to nothing.
                let tokenDays = days.filter { $0.service == .claude }
                let voiceDays = days.filter { $0.service != .claude }
                if !tokenDays.isEmpty {
                    Text("Claude by day").font(.headline)
                    Chart(tokenDays, id: \.self) { r in
                        BarMark(x: .value("Day", date(r.day), unit: .day), y: .value("Tokens", r.totalTokens))
                            .foregroundStyle(by: .value("Model", label(r)))
                    }
                    .chartYAxisLabel("Tokens")
                    .frame(height: 120)
                }
                if !voiceDays.isEmpty {
                    Text("Voices by day").font(.headline)
                    Chart(voiceDays, id: \.self) { r in
                        BarMark(x: .value("Day", date(r.day), unit: .day), y: .value("Characters", r.characters))
                            .foregroundStyle(by: .value("Model", label(r)))
                    }
                    .chartYAxisLabel("Characters spoken")
                    .frame(height: 120)
                }
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
        switch r.service {
        case .claude: ClaudeAccount.shared.name(of: r.model)
        case .elevenlabs: "ElevenLabs (\(r.model))"
        case .google: "Gemini (\(r.model))"
        }
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
