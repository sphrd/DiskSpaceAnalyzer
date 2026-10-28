// swift-tools-version:5.7
// Disk Space Analyzer for macOS. Built by macos/build.sh as a universal (arm64 + x86_64) .app inside a .dmg.
import PackageDescription

let package = Package(
    name: "DiskSpaceAnalyzer",
    platforms: [.macOS(.v11)],
    targets: [
        .executableTarget(
            name: "DiskSpaceAnalyzer",
            path: "Sources/DiskSpaceAnalyzer"
        )
    ]
)
