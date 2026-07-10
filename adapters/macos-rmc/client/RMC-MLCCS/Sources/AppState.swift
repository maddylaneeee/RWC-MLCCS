import Combine
import Foundation

@MainActor
final class AppState: ObservableObject {
    @Published var config: ClientConfig
    @Published var permissions: PermissionSnapshot
    @Published var acceptedPolicy: Bool
    @Published var sudoPasswordInput = ""
    @Published var sudoValidated = false
    @Published var sudoMessage = "尚未验证"
    @Published var isRunning = false
    @Published var isConnected = false
    @Published var statusText = "未连接"
    @Published var logs: [String] = []

    private var sudoPassword: String?
    private var remoteClient: RemoteClient?
    private var remoteTask: Task<Void, Never>?

    init() {
        self.config = ClientConfig.load()
        self.permissions = PermissionSnapshot.current()
        self.acceptedPolicy = UserDefaults.standard.bool(forKey: "rmc.acceptedPolicy")
        appendLog("RMC-MLCCS 已启动")
    }

    func refreshPermissions() {
        permissions = PermissionSnapshot.current()
        appendLog("已刷新权限状态")
    }

    func requestAccessibility() {
        PermissionCenter.requestAccessibilityPrompt()
        PermissionCenter.openAccessibilitySettings()
    }

    func requestScreenRecording() {
        PermissionCenter.requestScreenRecordingPrompt()
        PermissionCenter.openScreenRecordingSettings()
    }

    func openFullDiskAccess() {
        PermissionCenter.openFullDiskAccessSettings()
    }

    func setAcceptedPolicy(_ accepted: Bool) {
        acceptedPolicy = accepted
        UserDefaults.standard.set(accepted, forKey: "rmc.acceptedPolicy")
    }

    func validateSudo() {
        let password = sudoPasswordInput
        guard !password.isEmpty else {
            sudoMessage = "请输入本机管理员密码"
            return
        }

        sudoMessage = "正在验证..."
        Task {
            let valid = await ShellExecutor.validateSudoPassword(password)
            await MainActor.run {
                if valid {
                    self.sudoPassword = password
                    self.sudoPasswordInput = ""
                    self.sudoValidated = true
                    self.sudoMessage = "已验证，本次运行期间可用于 root 命令"
                    self.appendLog("sudo 已验证")
                } else {
                    self.sudoPassword = nil
                    self.sudoValidated = false
                    self.sudoMessage = "sudo 验证失败"
                    self.appendLog("sudo 验证失败")
                }
            }
        }
    }

    func start() {
        guard !isRunning else {
            return
        }

        guard acceptedPolicy else {
            statusText = "需要接受授权说明"
            return
        }

        if config.requireSudoBeforeConnect && !sudoValidated {
            statusText = "需要先验证 sudo"
            return
        }

        let client = RemoteClient(
            config: config,
            passwordProvider: { [weak self] in
                await MainActor.run {
                    self?.sudoPassword
                }
            },
            onLog: { [weak self] message in
                Task { @MainActor in self?.appendLog(message) }
            },
            onConnectionChange: { [weak self] connected in
                Task { @MainActor in
                    self?.isConnected = connected
                    self?.statusText = connected ? "已连接" : "等待连接"
                }
            }
        )

        remoteClient = client
        isRunning = true
        statusText = "正在连接"
        appendLog("开始连接 \(config.serverUrl)")
        remoteTask = Task { [weak self, client] in
            await client.run()
            await MainActor.run {
                self?.isRunning = false
                self?.isConnected = false
                self?.statusText = "已停止"
            }
        }
    }

    func stop() {
        remoteClient?.stop()
        remoteTask?.cancel()
        remoteClient = nil
        remoteTask = nil
        isRunning = false
        isConnected = false
        statusText = "已停止"
        appendLog("连接已停止")
    }

    func appendLog(_ message: String) {
        let formatter = DateFormatter()
        formatter.dateFormat = "HH:mm:ss"
        logs.append("[\(formatter.string(from: Date()))] \(message)")
        if logs.count > 500 {
            logs.removeFirst(logs.count - 500)
        }
    }

    var sudoPasswordForExecution: String? {
        sudoPassword
    }
}
