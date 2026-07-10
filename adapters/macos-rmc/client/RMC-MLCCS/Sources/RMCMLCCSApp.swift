import SwiftUI

@main
struct RMCMLCCSApp: App {
    @StateObject private var state = AppState()

    var body: some Scene {
        WindowGroup {
            ContentView()
                .environmentObject(state)
                .frame(minWidth: 880, minHeight: 620)
        }
        .windowStyle(.automatic)
        .commands {
            CommandGroup(after: .appInfo) {
                Button("刷新权限状态") {
                    state.refreshPermissions()
                }
                .keyboardShortcut("r", modifiers: [.command])
            }
        }
    }
}
