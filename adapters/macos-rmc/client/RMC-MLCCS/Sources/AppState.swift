import Combine
import Foundation
import AppKit
import UniformTypeIdentifiers

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
    @Published var publicConfigURL = UserDefaults.standard.string(
        forKey: "rmc.publicConfigURL"
    ) ?? ClientConfig.publicBootstrapURL
    @Published var privateConfigURL = ""
    @Published var configurationMessage = ""
    @Published var isApplyingConfiguration = false

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
        appendLog("开始连接 \(config.brokerUrl)")
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

    func applyRemoteConfiguration() {
        guard !isApplyingConfiguration else { return }
        let publicURL = publicConfigURL.trimmingCharacters(in: .whitespacesAndNewlines)
        let privateURL = privateConfigURL.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !privateURL.isEmpty else {
            configurationMessage = "请输入带 #crc-key 的加密私有配置 URL"
            return
        }
        isApplyingConfiguration = true
        configurationMessage = "正在安全下载并验证配置…"
        Task {
            do {
                let candidate = try await ProvisioningCodec.downloadAndDecrypt(
                    publicURLText: publicURL,
                    privateURLText: privateURL
                )
                try await installAndActivate(candidate, publicURL: publicURL)
                privateConfigURL = ""
                configurationMessage = "配置已安全应用"
            } catch {
                configurationMessage = "配置应用失败：\(safeConfigurationError(error))"
                appendLog("配置应用失败")
            }
            isApplyingConfiguration = false
        }
    }

    func importLocalConfiguration() {
        guard !isApplyingConfiguration else { return }
        let panel = NSOpenPanel()
        panel.title = "选择 device.private.json"
        panel.prompt = "导入"
        panel.canChooseDirectories = false
        panel.canChooseFiles = true
        panel.allowsMultipleSelection = false
        panel.resolvesAliases = false
        panel.allowedContentTypes = [.json]
        guard panel.runModal() == .OK, let url = panel.url else { return }

        isApplyingConfiguration = true
        configurationMessage = "正在验证本地私有配置…"
        Task {
            do {
                let candidate = try ProvisioningCodec.loadLocalConfig(from: url)
                try await installAndActivate(candidate, publicURL: nil)
                privateConfigURL = ""
                configurationMessage = "本地配置已安全导入"
            } catch {
                configurationMessage = "配置导入失败：\(safeConfigurationError(error))"
                appendLog("本地配置导入失败")
            }
            isApplyingConfiguration = false
        }
    }

    private func installAndActivate(_ candidate: ClientConfig, publicURL: String?) async throws {
        let shouldRestart = isRunning
        if shouldRestart {
            await stopForConfigurationChange()
        }
        do {
            try ClientConfig.install(candidate)
            config = candidate.normalized()
            if let publicURL {
                publicConfigURL = publicURL
                UserDefaults.standard.set(publicURL, forKey: "rmc.publicConfigURL")
            }
            appendLog("已安装新的私有配置")
            if shouldRestart {
                start()
            }
        } catch {
            if shouldRestart {
                start()
            }
            throw error
        }
    }

    private func stopForConfigurationChange() async {
        let existingTask = remoteTask
        remoteClient?.stop()
        existingTask?.cancel()
        if let existingTask {
            await existingTask.value
        }
        remoteClient = nil
        remoteTask = nil
        isRunning = false
        isConnected = false
        statusText = "正在重新配置"
    }

    private func safeConfigurationError(_ error: Error) -> String {
        if let rmc = error as? RMCError {
            return rmc.localizedDescription
        }
        if error is DecodingError {
            return "配置 JSON 格式无效"
        }
        return "无法验证或保存配置"
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
