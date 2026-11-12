// swift-tools-version: 6.2
import PackageDescription

// Phase 0 shading test for InnerVoice's Voice Plan (docs/CASTING-STYLE.md section 6).
// Uses a copy of the pinned mlx-audio-swift with extractSpeakerEmbedding made public.
let package = Package(
    name: "ShadeSpike",
    platforms: [.macOS(.v26)],
    dependencies: [.package(path: "mlx-audio-swift")],
    targets: [
        .executableTarget(
            name: "ShadeSpike",
            dependencies: [
                .product(name: "MLXAudioTTS", package: "mlx-audio-swift"),
                .product(name: "MLXAudioCore", package: "mlx-audio-swift"),
            ]
        )
    ]
)
