// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "InnerVoice",
    platforms: [.macOS(.v26)],
    targets: [
        // Pinned llama.cpp build; fetched by Tools/fetch_llama.sh (not committed).
        .binaryTarget(name: "llama", path: "Frameworks/llama.xcframework"),
        // The book, its lines and cast, importers, dialogue finding, attribution rules,
        // prompts, revisions. No SwiftUI, so tests run without the app.
        .target(name: "InnerVoiceKit"),
        .executableTarget(
            name: "InnerVoice",
            dependencies: ["InnerVoiceKit", "llama"],
            // App code runs on the main thread unless you say otherwise.
            swiftSettings: [.defaultIsolation(MainActor.self)],
            linkerSettings: [.unsafeFlags(["-Xlinker", "-rpath", "-Xlinker", "@executable_path/../Frameworks"])]
        ),
        .testTarget(name: "InnerVoiceKitTests", dependencies: ["InnerVoiceKit"]),
    ]
)
