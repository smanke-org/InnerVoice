@preconcurrency import AVFoundation
import InnerVoiceKit

/// Shading one voice for a character (Casting Style, docs/CASTING-STYLE.md §6). Qwen3-TTS
/// shifts the voice sample before cloning (tested: exact, and it keeps the narrator's voice);
/// Orpheus and ElevenLabs shift the finished audio; Apple's voices take pitch and rate.
nonisolated enum ShadeAudio {
    /// How much one pace step changes speed (±2 steps: ±16%).
    static func paceRate(_ pace: Int) -> Double { 1 + 0.08 * Double(pace) }

    /// One pitch step is a semitone.
    static func pitchMultiplier(_ steps: Int) -> Double { pow(2, Double(steps) / 12) }

    /// The audio shifted `semitones` and sped up or slowed by `pace` steps, in the app's format.
    static func apply(_ input: AVAudioPCMBuffer, semitones: Int, pace: Int) throws -> AVAudioPCMBuffer {
        guard semitones != 0 || pace != 0, input.frameLength > 0, let data = input.floatChannelData else { return input }
        let samples = try apply(Array(UnsafeBufferPointer(start: data[0], count: Int(input.frameLength))), semitones: semitones, pace: pace,
                                format: input.format)
        guard let out = AVAudioPCMBuffer(pcmFormat: input.format, frameCapacity: AVAudioFrameCount(max(1, samples.count))) else { return input }
        out.frameLength = AVAudioFrameCount(samples.count)
        samples.withUnsafeBufferPointer { out.floatChannelData![0].update(from: $0.baseAddress!, count: samples.count) }
        return out
    }

    /// Offline AVAudioUnitTimePitch: pitch in cents, rate as a speed.
    static func apply(_ input: [Float], semitones: Int, pace: Int, format: AVAudioFormat = SpeechAudio.format) throws -> [Float] {
        guard semitones != 0 || pace != 0, !input.isEmpty else { return input }
        let engine = AVAudioEngine()
        let player = AVAudioPlayerNode()
        let unit = AVAudioUnitTimePitch()
        unit.pitch = Float(semitones * 100)
        unit.rate = Float(paceRate(pace))
        engine.attach(player)
        engine.attach(unit)
        engine.connect(player, to: unit, format: format)
        engine.connect(unit, to: engine.mainMixerNode, format: format)
        try engine.enableManualRenderingMode(.offline, format: format, maximumFrameCount: 4096)
        try engine.start()
        defer { engine.stop() }
        guard let b = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(input.count)),
              let chunk = AVAudioPCMBuffer(pcmFormat: engine.manualRenderingFormat, frameCapacity: 4096) else { return input }
        b.frameLength = AVAudioFrameCount(input.count)
        input.withUnsafeBufferPointer { b.floatChannelData![0].update(from: $0.baseAddress!, count: input.count) }
        player.scheduleBuffer(b, completionHandler: nil)
        player.play()
        // The unit's latency comes first; render past the end to get it all back.
        let expected = Int(Double(input.count) / paceRate(pace)) + 8192
        var out: [Float] = []
        out.reserveCapacity(expected)
        while out.count < expected {
            let status = try engine.renderOffline(AVAudioFrameCount(min(4096, expected - out.count)), to: chunk)
            guard status == .success else { break }
            out += UnsafeBufferPointer(start: chunk.floatChannelData![0], count: Int(chunk.frameLength))
        }
        // Drop the latency's silence at the start and the padding at the end, keeping 10 ms.
        // (The same threshold as `SpeechAudio.trimmed`: the unit's tail rings on quietly.)
        let keep = Int(format.sampleRate * 0.01)
        if let last = out.lastIndex(where: { abs($0) > 0.008 }) { out.removeLast(max(0, out.count - 1 - last - keep)) }
        if let first = out.firstIndex(where: { abs($0) > 0.008 }) { out.removeFirst(max(0, first - keep)) }
        return out
    }
}
