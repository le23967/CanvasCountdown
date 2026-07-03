// swift-tools-version: 6.0

import PackageDescription

let package = Package(
    name: "CanvasCountdown",
    platforms: [
        .macOS(.v15),
    ],
    products: [
        .library(
            name: "CanvasCountdown",
            targets: ["CanvasCountdown"]
        ),
    ],
    targets: [
        .target(
            name: "CanvasCountdown",
            path: "CanvasCountdown",
            exclude: [
                "App",
                "Preview Content",
                "Resources",
            ]
        ),
        .testTarget(
            name: "CanvasCountdownTests",
            dependencies: ["CanvasCountdown"],
            path: "CanvasCountdownTests",
            resources: [
                .copy("Fixtures"),
            ]
        ),
    ],
    swiftLanguageModes: [.v6]
)
