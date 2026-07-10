import AppKit
import ApplicationServices
import CoreGraphics
import Foundation

struct PermissionSnapshot {
    var accessibility: Bool
    var screenRecording: Bool
    var fullDiskAccessLikely: Bool

    static func current() -> PermissionSnapshot {
        PermissionSnapshot(
            accessibility: AXIsProcessTrusted(),
            screenRecording: CGPreflightScreenCaptureAccess(),
            fullDiskAccessLikely: FullDiskAccessProbe.canReadProtectedLocation()
        )
    }

    func asText() -> String {
        """
        accessibility: \(accessibility ? "granted" : "missing")
        screenRecording: \(screenRecording ? "granted" : "missing")
        fullDiskAccessLikely: \(fullDiskAccessLikely ? "granted" : "missing-or-unknown")
        """
    }
}

enum PermissionCenter {
    static func requestAccessibilityPrompt() {
        let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String
        AXIsProcessTrustedWithOptions([key: true] as CFDictionary)
    }

    static func requestScreenRecordingPrompt() {
        _ = CGRequestScreenCaptureAccess()
    }

    static func openAccessibilitySettings() {
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility")
    }

    static func openScreenRecordingSettings() {
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
    }

    static func openFullDiskAccessSettings() {
        openSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles")
    }

    private static func openSettings(_ urlString: String) {
        guard let url = URL(string: urlString) else {
            return
        }
        NSWorkspace.shared.open(url)
    }
}

private enum FullDiskAccessProbe {
    static func canReadProtectedLocation() -> Bool {
        let candidates = [
            "\(NSHomeDirectory())/Library/Mail",
            "\(NSHomeDirectory())/Library/Messages",
            "\(NSHomeDirectory())/Library/Safari"
        ]

        for path in candidates where FileManager.default.fileExists(atPath: path) {
            if (try? FileManager.default.contentsOfDirectory(atPath: path)) != nil {
                return true
            }
        }
        return false
    }
}
