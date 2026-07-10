import Foundation

struct ToolSpec: Identifiable {
    var id: String { name }
    var name: String
    var summary: String
}

enum ToolRunner {
    static let availableTools: [ToolSpec] = [
        ToolSpec(name: "sysinfo", summary: "系统版本、用户、磁盘和运行时间"),
        ToolSpec(name: "permissions", summary: "RMC 当前可见的 macOS 权限状态"),
        ToolSpec(name: "screenshot", summary: "保存一张屏幕截图到 /tmp"),
        ToolSpec(name: "processes", summary: "列出当前进程"),
        ToolSpec(name: "network", summary: "网络接口、路由和 DNS 概览"),
        ToolSpec(name: "apps", summary: "列出已安装应用"),
        ToolSpec(name: "brew", summary: "Homebrew 包概览"),
        ToolSpec(name: "deps", summary: "检查常用维护工具是否可用"),
        ToolSpec(name: "rg", summary: "使用 ripgrep 或 grep 搜索文本"),
        ToolSpec(name: "python", summary: "运行一段 Python 代码"),
        ToolSpec(name: "python-file", summary: "运行指定 Python 脚本文件"),
        ToolSpec(name: "download-url", summary: "从 URL 下载文件到客户端"),
        ToolSpec(name: "fileshare-upload", summary: "上传客户端文件到 lixinchen.ca FileShare"),
        ToolSpec(name: "file-info", summary: "查看路径元数据"),
        ToolSpec(name: "read-text", summary: "读取文本文件的前 N 字节"),
        ToolSpec(name: "clipboard", summary: "读取当前剪贴板文本"),
        ToolSpec(name: "password-popup", summary: "显示本机隐藏密码输入弹窗，不回传密码值")
    ]

    static func run(name: String, args: [String], executor: ShellExecutor, timeoutSeconds: Int) async -> ExecutionResult {
        let startedAt = Date()
        switch name {
        case "permissions":
            return ExecutionResult(
                exitCode: 0,
                stdout: PermissionSnapshot.current().asText(),
                stderr: "",
                cancelled: false,
                durationMs: elapsedMs(since: startedAt)
            )
        case "sysinfo":
            return await executor.execute(command: sysinfoScript, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "screenshot":
            return await executor.execute(command: screenshotScript, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "processes":
            return await executor.execute(command: "ps axo pid,user,%cpu,%mem,comm | head -120", runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "network":
            return await executor.execute(command: networkScript, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "apps":
            return await executor.execute(command: "mdfind 'kMDItemContentType == \"com.apple.application-bundle\"' | sort | head -200", runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "brew":
            return await executor.execute(command: brewScript, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "deps":
            return await executor.execute(command: depsScript, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "rg":
            guard let pattern = args.first else {
                return usage("usage: tool rg <pattern> [path] [maxLines]", startedAt: startedAt)
            }
            let path = args.dropFirst().first ?? "."
            let maxLines = Int(args.dropFirst(2).first ?? "") ?? 200
            let command = rgScript(pattern: pattern, path: path, maxLines: maxLines)
            return await executor.execute(command: command, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "python":
            guard let code = args.first else {
                return usage("usage: tool python <code> [args...]", startedAt: startedAt)
            }
            let command = pythonInlineScript(code: code, args: Array(args.dropFirst()))
            return await executor.execute(command: command, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "python-file":
            guard let path = args.first else {
                return usage("usage: tool python-file <path> [args...]", startedAt: startedAt)
            }
            let command = pythonFileScript(path: path, args: Array(args.dropFirst()))
            return await executor.execute(command: command, runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "download-url":
            guard let url = args.first else {
                return usage("usage: tool download-url <url> [outputPath]", startedAt: startedAt)
            }
            let outputPath = args.dropFirst().first ?? ""
            return await executor.execute(command: downloadUrlScript(url: url, outputPath: outputPath), runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "fileshare-upload":
            guard let path = args.first else {
                return usage("usage: tool fileshare-upload <path> [baseUrl]", startedAt: startedAt)
            }
            let baseUrl = args.dropFirst().first ?? "https://lixinchen.ca"
            return await executor.execute(command: fileshareUploadScript(path: path, baseUrl: baseUrl), runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "file-info":
            guard let path = args.first else {
                return usage("usage: tool file-info <path>", startedAt: startedAt)
            }
            let quoted = shellQuote(path)
            return await executor.execute(command: "ls -laOe@ \(quoted); file \(quoted); du -sh \(quoted) 2>/dev/null || true", runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "read-text":
            guard let path = args.first else {
                return usage("usage: tool read-text <path> [maxBytes]", startedAt: startedAt)
            }
            let maxBytes = Int(args.dropFirst().first ?? "") ?? 20000
            let quoted = shellQuote(path)
            return await executor.execute(command: "/usr/bin/head -c \(max(1, min(maxBytes, 1_000_000))) \(quoted)", runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "clipboard":
            return await executor.execute(command: "/usr/bin/pbpaste | /usr/bin/head -c 20000", runAsRoot: false, timeoutSeconds: timeoutSeconds)
        case "password-popup":
            let title = args.first ?? "CRC password prompt"
            let message = args.dropFirst().first ?? "Enter the requested password on this Mac. The password value is not returned to the server."
            return await executor.execute(command: passwordPopupScript(title: title, message: message), runAsRoot: false, timeoutSeconds: timeoutSeconds)
        default:
            return ExecutionResult(
                exitCode: 64,
                stdout: "",
                stderr: "unknown tool: \(name)\n",
                cancelled: false,
                durationMs: elapsedMs(since: startedAt)
            )
        }
    }

    private static let sysinfoScript = """
    echo "computer_name: $(scutil --get ComputerName 2>/dev/null || hostname)"
    echo "host_name: $(hostname)"
    echo "user: $(whoami)"
    echo "uid: $(id -u)"
    echo "macos: $(sw_vers -productVersion) ($(sw_vers -buildVersion))"
    echo "kernel: $(uname -a)"
    echo "uptime: $(uptime)"
    echo
    echo "[disk]"
    df -h /
    echo
    echo "[battery]"
    pmset -g batt 2>/dev/null || true
    """

    private static let screenshotScript = """
    out="/tmp/rmc-screenshot-$(date +%Y%m%d-%H%M%S).png"
    screencapture -x "$out"
    status=$?
    if [ "$status" -ne 0 ]; then
      echo "screencapture failed; check Screen Recording permission" >&2
      exit "$status"
    fi
    ls -lh "$out"
    echo "$out"
    """

    private static let networkScript = """
    echo "[network services]"
    networksetup -listallhardwareports 2>/dev/null || true
    echo
    echo "[routing]"
    netstat -rn | head -80
    echo
    echo "[dns]"
    scutil --dns | head -120
    echo
    echo "[nwi]"
    scutil --nwi 2>/dev/null || true
    """

    private static let brewScript = """
    if [ -x /opt/homebrew/bin/brew ]; then
      /opt/homebrew/bin/brew --version | head -1
      echo
      /opt/homebrew/bin/brew list --formula --versions | head -200
    else
      echo "Homebrew not found at /opt/homebrew/bin/brew"
    fi
    """

    private static let depsScript = """
    for name in rg python3 git node npm brew swift xcodebuild osascript screencapture jq curl; do
      printf "%-14s" "$name:"
      if command -v "$name" >/dev/null 2>&1; then
        command -v "$name"
      else
        echo "not found"
      fi
    done
    echo
    echo "[python]"
    python3 --version 2>&1 || true
    echo
    echo "[rg]"
    rg --version 2>/dev/null | head -1 || true
    """

    private static func rgScript(pattern: String, path: String, maxLines: Int) -> String {
        let limit = max(1, min(maxLines, 5000))
        return """
        pattern=\(shellQuote(pattern))
        root=\(shellQuote(path))
        max_lines=\(limit)
        if command -v rg >/dev/null 2>&1; then
          rg --hidden --glob '!.git' --glob '!node_modules' --line-number --color never -- "$pattern" "$root" | head -n "$max_lines"
        else
          grep -RIn --exclude-dir=.git --exclude-dir=node_modules -- "$pattern" "$root" | head -n "$max_lines"
        fi
        """
    }

    private static func downloadUrlScript(url: String, outputPath: String) -> String {
        """
        url=\(shellQuote(url))
        out=\(shellQuote(outputPath))
        if [ -z "$out" ]; then
          name="$(basename "${url%%\\?*}")"
          [ -n "$name" ] && [ "$name" != "/" ] || name="download"
          out="/tmp/$name"
        fi
        mkdir -p "$(dirname "$out")"
        curl -fL --retry 3 --connect-timeout 20 -o "$out" "$url"
        ls -lh "$out"
        echo "$out"
        """
    }

    private static func fileshareUploadScript(path: String, baseUrl: String) -> String {
        """
        python_bin="$(command -v python3 || command -v python || true)"
        if [ -z "$python_bin" ]; then
          echo "python3/python not found" >&2
          exit 127
        fi
        "$python_bin" - \(shellQuote(path)) \(shellQuote(baseUrl)) <<'PY'
        import json, math, os, sys, time
        from pathlib import Path
        from urllib.parse import quote, urlencode, urljoin
        from urllib.request import Request, urlopen
        from urllib.error import HTTPError

        path = Path(sys.argv[1]).expanduser().resolve()
        base = sys.argv[2].rstrip('/') + '/'
        if not path.is_file():
            raise SystemExit(f'not a file: {path}')
        size = path.stat().st_size
        if size <= 0:
            raise SystemExit('empty files are not supported by the current FileShare backend')
        timestamp = str(int(time.time() * 1000))
        chunk_size = 512 * 1024
        total = max(1, math.ceil(size / chunk_size))
        name = path.name or 'unnamed'

        def request_json(url, body, content_type):
            req = Request(url, data=body, method='POST', headers={'Content-Type': content_type, 'Accept': 'application/json', 'User-Agent': 'crc-client/1.0'})
            try:
                with urlopen(req, timeout=600) as resp:
                    data = resp.read()
                    status = resp.status
            except HTTPError as exc:
                data = exc.read()
                status = exc.code
            payload = json.loads(data.decode('utf-8') or '{}')
            if status >= 400 or payload.get('ok') is False:
                raise RuntimeError(payload.get('error') or payload.get('message') or f'HTTP {status}')
            return payload

        with path.open('rb') as fh:
            for index in range(total):
                chunk = fh.read(chunk_size)
                query = urlencode({'timestamp': timestamp, 'file_name': name, 'chunk_index': str(index), 'total_chunks': str(total)})
                request_json(urljoin(base, 'api/upload/chunk?' + query), chunk, 'application/octet-stream')
                print(f'{path.name}: chunk {index + 1}/{total}', file=sys.stderr)

        complete = {'timestamp': timestamp, 'file_name': name, 'total_chunks': total, 'file_size': size}
        data = request_json(urljoin(base, 'api/upload/complete'), json.dumps(complete).encode('utf-8'), 'application/json')
        final_name = data.get('file_name') or name
        print('timestamp:', timestamp)
        print(urljoin(base, f'fileshare/{quote(timestamp)}/{quote(final_name)}'))
        if data.get('url'):
            print(data['url'])
        PY
        """
    }

    private static func pythonInlineScript(code: String, args: [String]) -> String {
        let encoded = Data(code.utf8).base64EncodedString()
        return """
        python_bin="$(command -v python3 || command -v python || true)"
        if [ -z "$python_bin" ]; then
          echo "python3/python not found" >&2
          exit 127
        fi
        tmp="$(mktemp /tmp/rmc-python.XXXXXX.py)"
        trap 'rm -f "$tmp"' EXIT
        /usr/bin/base64 -D > "$tmp" <<'PYCODE'
        \(encoded)
        PYCODE
        "$python_bin" "$tmp" \(args.map(shellQuote).joined(separator: " "))
        """
    }

    private static func pythonFileScript(path: String, args: [String]) -> String {
        """
        python_bin="$(command -v python3 || command -v python || true)"
        if [ -z "$python_bin" ]; then
          echo "python3/python not found" >&2
          exit 127
        fi
        "$python_bin" \(shellQuote(path)) \(args.map(shellQuote).joined(separator: " "))
        """
    }

    private static func passwordPopupScript(title: String, message: String) -> String {
        """
        CRC_PASSWORD_TITLE=\(shellQuote(title)) CRC_PASSWORD_MESSAGE=\(shellQuote(message)) /usr/bin/osascript <<'APPLESCRIPT'
        set dialogTitle to system attribute "CRC_PASSWORD_TITLE"
        set dialogMessage to system attribute "CRC_PASSWORD_MESSAGE"
        try
          display dialog dialogMessage default answer "" with hidden answer buttons {"Cancel", "OK"} default button "OK" with title dialogTitle
          return "password received locally; value was not returned"
        on error number -128
          return "cancelled"
        end try
        APPLESCRIPT
        """
    }

    private static func usage(_ text: String, startedAt: Date) -> ExecutionResult {
        ExecutionResult(exitCode: 64, stdout: "", stderr: text + "\n", cancelled: false, durationMs: elapsedMs(since: startedAt))
    }

    private static func shellQuote(_ value: String) -> String {
        "'" + value.replacingOccurrences(of: "'", with: "'\\''") + "'"
    }

    private static func elapsedMs(since date: Date) -> Int64 {
        Int64(Date().timeIntervalSince(date) * 1000)
    }
}
