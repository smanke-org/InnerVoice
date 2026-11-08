// swift-tools-version: 6.2
import PackageDescription

let package = Package(
    name: "InnerVoice",
    platforms: [.macOS(.v26)],
    targets: [
        // Rules and data logic with no SwiftUI, so tests run without the app.
        .target(name: "InnerVoiceKit"),
        .executableTarget(
            name: "InnerVoice",
            dependencies: ["InnerVoiceKit"],
            // App code runs on the main thread unless you say otherwise.
            swiftSettings: [.defaultIsolation(MainActor.self)]
        ),
        .testTarget(name: "InnerVoiceKitTests", dependencies: ["InnerVoiceKit"]),
    ]
)
