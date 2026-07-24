import SwiftUI

private enum SidebarItem: String, CaseIterable, Identifiable {
    case overview
    case permissions
    case tools
    case logs
    case config

    var id: String { rawValue }

    var title: String {
        switch self {
        case .overview: "概览"
        case .permissions: "权限"
        case .tools: "工具"
        case .logs: "日志"
        case .config: "配置"
        }
    }

    var symbol: String {
        switch self {
        case .overview: "dot.radiowaves.left.and.right"
        case .permissions: "lock.shield"
        case .tools: "wrench.and.screwdriver"
        case .logs: "doc.text.magnifyingglass"
        case .config: "slider.horizontal.3"
        }
    }
}

struct ContentView: View {
    @EnvironmentObject private var state: AppState
    @State private var selection: SidebarItem? = .overview

    var body: some View {
        NavigationSplitView {
            List(SidebarItem.allCases, selection: $selection) { item in
                Label(item.title, systemImage: item.symbol)
                    .tag(item)
            }
            .navigationTitle("RMC")
        } detail: {
            Group {
                switch selection ?? .overview {
                case .overview:
                    OverviewView()
                case .permissions:
                    PermissionsView()
                case .tools:
                    ToolsView()
                case .logs:
                    LogsView()
                case .config:
                    ConfigView()
                }
            }
            .padding(24)
            .frame(maxWidth: .infinity, maxHeight: .infinity, alignment: .topLeading)
        }
        .toolbar {
            ToolbarItemGroup {
                Button {
                    state.refreshPermissions()
                } label: {
                    Label("刷新", systemImage: "arrow.clockwise")
                }
                .help("刷新权限状态")

                if state.isRunning {
                    Button(role: .destructive) {
                        state.stop()
                    } label: {
                        Label("停止", systemImage: "stop.fill")
                    }
                    .help("停止远程连接")
                } else {
                    Button {
                        state.start()
                    } label: {
                        Label("启动", systemImage: "play.fill")
                    }
                    .help("启动远程连接")
                }
            }
        }
    }
}

private struct OverviewView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HeaderView(title: "RMC-MLCCS", subtitle: state.config.deviceId)

            HStack(spacing: 12) {
                StatusBadge(text: state.statusText, color: state.isConnected ? .green : (state.isRunning ? .orange : .secondary))
                Text(state.config.brokerUrl)
                    .font(.callout.monospaced())
                    .textSelection(.enabled)
                    .foregroundStyle(.secondary)
            }

            Divider()

            Toggle(isOn: Binding(
                get: { state.acceptedPolicy },
                set: { state.setAcceptedPolicy($0) }
            )) {
                Text("我授权此 Mac 连接到 MLCCS RMC 服务端，并允许服务端在我保持本程序运行期间执行远程管理操作。")
            }
            .toggleStyle(.checkbox)

            VStack(alignment: .leading, spacing: 10) {
                Text("sudo")
                    .font(.headline)
                HStack(spacing: 10) {
                    SecureField("管理员密码", text: $state.sudoPasswordInput)
                        .textFieldStyle(.roundedBorder)
                        .frame(maxWidth: 360)
                        .onSubmit { state.validateSudo() }
                    Button {
                        state.validateSudo()
                    } label: {
                        Label("验证", systemImage: "checkmark.shield")
                    }
                }
                Text(state.sudoMessage)
                    .foregroundStyle(state.sudoValidated ? .green : .secondary)
                    .font(.callout)
            }

            HStack(spacing: 12) {
                Button {
                    state.start()
                } label: {
                    Label("启动连接", systemImage: "play.fill")
                }
                .buttonStyle(.borderedProminent)
                .disabled(state.isRunning)

                Button(role: .destructive) {
                    state.stop()
                } label: {
                    Label("停止连接", systemImage: "stop.fill")
                }
                .disabled(!state.isRunning)
            }

            Spacer()
        }
    }
}

private struct PermissionsView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HeaderView(title: "权限", subtitle: "macOS 隐私授权状态")
            VStack(spacing: 12) {
                PermissionRow(
                    title: "辅助功能",
                    symbol: "cursorarrow.motionlines",
                    granted: state.permissions.accessibility,
                    actionTitle: "打开",
                    action: state.requestAccessibility
                )
                PermissionRow(
                    title: "屏幕录制",
                    symbol: "rectangle.dashed",
                    granted: state.permissions.screenRecording,
                    actionTitle: "请求",
                    action: state.requestScreenRecording
                )
                PermissionRow(
                    title: "完全磁盘访问",
                    symbol: "internaldrive",
                    granted: state.permissions.fullDiskAccessLikely,
                    actionTitle: "打开",
                    action: state.openFullDiskAccess
                )
            }
            Button {
                state.refreshPermissions()
            } label: {
                Label("刷新状态", systemImage: "arrow.clockwise")
            }
            Spacer()
        }
    }
}

private struct ToolsView: View {
    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HeaderView(title: "工具", subtitle: "服务端可通过 tool 命令调用")
            Grid(alignment: .leading, horizontalSpacing: 16, verticalSpacing: 12) {
                ForEach(ToolRunner.availableTools) { tool in
                    GridRow {
                        Text(tool.name)
                            .font(.body.monospaced())
                            .frame(width: 120, alignment: .leading)
                        Text(tool.summary)
                            .foregroundStyle(.secondary)
                    }
                    Divider()
                }
            }
            Spacer()
        }
    }
}

private struct LogsView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 14) {
            HeaderView(title: "日志", subtitle: "\(state.logs.count) 条")
            ScrollViewReader { proxy in
                ScrollView {
                    VStack(alignment: .leading, spacing: 4) {
                        ForEach(Array(state.logs.enumerated()), id: \.offset) { index, line in
                            Text(line)
                                .font(.system(.caption, design: .monospaced))
                                .textSelection(.enabled)
                                .frame(maxWidth: .infinity, alignment: .leading)
                                .id(index)
                        }
                    }
                }
                .onChange(of: state.logs.count) { _, count in
                    if count > 0 {
                        proxy.scrollTo(count - 1)
                    }
                }
            }
        }
    }
}

private struct ConfigView: View {
    @EnvironmentObject private var state: AppState

    var body: some View {
        VStack(alignment: .leading, spacing: 18) {
            HeaderView(title: "配置", subtitle: "当前客户端配置")
            Grid(alignment: .leading, horizontalSpacing: 18, verticalSpacing: 12) {
                ConfigRow("Broker URL", state.config.brokerUrl)
                ConfigRow("Device ID", state.config.deviceId)
                ConfigRow("Root Commands", state.config.allowRootCommands ? "enabled" : "disabled")
                ConfigRow("Require sudo", state.config.requireSudoBeforeConnect ? "enabled" : "disabled")
                ConfigRow("Reconnect Delay", "\(state.config.reconnectDelaySeconds)s")
                ConfigRow("Command Timeout", "\(state.config.commandTimeoutSeconds)s")
                ConfigRow("TLS Validation", "system trust (strict)")
            }
            Spacer()
        }
    }
}

private struct HeaderView: View {
    var title: String
    var subtitle: String

    var body: some View {
        VStack(alignment: .leading, spacing: 6) {
            Text(title)
                .font(.largeTitle.weight(.semibold))
            Text(subtitle)
                .font(.callout)
                .foregroundStyle(.secondary)
                .textSelection(.enabled)
        }
    }
}

private struct StatusBadge: View {
    var text: String
    var color: Color

    var body: some View {
        Label(text, systemImage: "circle.fill")
            .font(.callout.weight(.medium))
            .foregroundStyle(color)
            .padding(.horizontal, 12)
            .padding(.vertical, 7)
            .background(.regularMaterial, in: Capsule())
    }
}

private struct PermissionRow: View {
    var title: String
    var symbol: String
    var granted: Bool
    var actionTitle: String
    var action: () -> Void

    var body: some View {
        HStack(spacing: 14) {
            Image(systemName: symbol)
                .font(.title3)
                .frame(width: 28)
            VStack(alignment: .leading, spacing: 3) {
                Text(title)
                    .font(.headline)
                Text(granted ? "已授权" : "未授权")
                    .foregroundStyle(granted ? .green : .secondary)
            }
            Spacer()
            Button(actionTitle, action: action)
        }
        .padding(14)
        .background(.regularMaterial, in: RoundedRectangle(cornerRadius: 8, style: .continuous))
    }
}

private func ConfigRow(_ key: String, _ value: String) -> some View {
    GridRow {
        Text(key)
            .foregroundStyle(.secondary)
            .frame(width: 150, alignment: .leading)
        Text(value)
            .textSelection(.enabled)
            .font(.body.monospaced())
    }
}
