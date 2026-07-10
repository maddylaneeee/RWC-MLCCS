import Darwin
import Foundation

struct ExecutionResult {
    var exitCode: Int
    var stdout: String
    var stderr: String
    var cancelled: Bool
    var durationMs: Int64
}

final class ShellExecutor {
    typealias PasswordProvider = () async -> String?

    private let passwordProvider: PasswordProvider

    init(passwordProvider: @escaping PasswordProvider) {
        self.passwordProvider = passwordProvider
    }

    func execute(command: String, runAsRoot: Bool, timeoutSeconds: Int) async -> ExecutionResult {
        let startedAt = Date()
        let password: String?
        if runAsRoot {
            password = await passwordProvider()
            if password?.isEmpty ?? true {
                return ExecutionResult(
                    exitCode: 77,
                    stdout: "",
                    stderr: "sudo password is not available in the running RMC client.\n",
                    cancelled: false,
                    durationMs: elapsedMs(since: startedAt)
                )
            }
        } else {
            password = nil
        }

        let process = Process()
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        let stdinPipe = Pipe()

        if runAsRoot {
            process.executableURL = URL(fileURLWithPath: "/usr/bin/sudo")
            process.arguments = ["-S", "-p", "", "/bin/zsh", "-lc", command]
        } else {
            process.executableURL = URL(fileURLWithPath: "/bin/zsh")
            process.arguments = ["-lc", command]
        }

        var environment = ProcessInfo.processInfo.environment
        environment["LANG"] = environment["LANG"] ?? "en_US.UTF-8"
        environment["LC_ALL"] = environment["LC_ALL"] ?? "en_US.UTF-8"
        process.environment = environment
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe
        process.standardInput = stdinPipe

        let stdoutTask = Task.detached {
            stdoutPipe.fileHandleForReading.readDataToEndOfFile()
        }
        let stderrTask = Task.detached {
            stderrPipe.fileHandleForReading.readDataToEndOfFile()
        }

        do {
            try process.run()
            if let password {
                stdinPipe.fileHandleForWriting.write(Data((password + "\n").utf8))
            }
            try? stdinPipe.fileHandleForWriting.close()
        } catch {
            return ExecutionResult(
                exitCode: 126,
                stdout: "",
                stderr: "failed to start command: \(error.localizedDescription)\n",
                cancelled: false,
                durationMs: elapsedMs(since: startedAt)
            )
        }

        let timedOut = await waitForExitOrTimeout(process: process, timeoutSeconds: max(1, timeoutSeconds))
        if timedOut {
            process.terminate()
            try? await Task.sleep(nanoseconds: 1_500_000_000)
            if process.isRunning {
                kill(process.processIdentifier, SIGKILL)
            }
            process.waitUntilExit()
        }

        let stdout = String(data: await stdoutTask.value, encoding: .utf8) ?? ""
        let stderr = String(data: await stderrTask.value, encoding: .utf8) ?? ""
        return ExecutionResult(
            exitCode: process.isRunning ? -1 : Int(process.terminationStatus),
            stdout: stdout,
            stderr: stderr,
            cancelled: timedOut,
            durationMs: elapsedMs(since: startedAt)
        )
    }

    static func validateSudoPassword(_ password: String) async -> Bool {
        let process = Process()
        let stdinPipe = Pipe()
        let stdoutPipe = Pipe()
        let stderrPipe = Pipe()
        process.executableURL = URL(fileURLWithPath: "/usr/bin/sudo")
        process.arguments = ["-S", "-k", "-v"]
        process.standardInput = stdinPipe
        process.standardOutput = stdoutPipe
        process.standardError = stderrPipe

        do {
            try process.run()
            stdinPipe.fileHandleForWriting.write(Data((password + "\n").utf8))
            try? stdinPipe.fileHandleForWriting.close()
            let timedOut = await waitForExitOrTimeout(process: process, timeoutSeconds: 12)
            if timedOut {
                process.terminate()
                process.waitUntilExit()
                return false
            }
            _ = stdoutPipe.fileHandleForReading.readDataToEndOfFile()
            _ = stderrPipe.fileHandleForReading.readDataToEndOfFile()
            return process.terminationStatus == 0
        } catch {
            return false
        }
    }

    private static func waitForExitOrTimeout(process: Process, timeoutSeconds: Int) async -> Bool {
        await withCheckedContinuation { continuation in
            let gate = ContinuationGate(continuation)

            @Sendable func finish(_ timedOut: Bool) {
                gate.resume(returning: timedOut)
            }

            process.terminationHandler = { _ in
                finish(false)
            }

            Task {
                try? await Task.sleep(nanoseconds: UInt64(timeoutSeconds) * 1_000_000_000)
                finish(true)
            }
        }
    }

    private func waitForExitOrTimeout(process: Process, timeoutSeconds: Int) async -> Bool {
        await Self.waitForExitOrTimeout(process: process, timeoutSeconds: timeoutSeconds)
    }

    private func elapsedMs(since date: Date) -> Int64 {
        Int64(Date().timeIntervalSince(date) * 1000)
    }
}

private final class ContinuationGate<Value>: @unchecked Sendable {
    private let lock = NSLock()
    private var resumed = false
    private let continuation: CheckedContinuation<Value, Never>

    init(_ continuation: CheckedContinuation<Value, Never>) {
        self.continuation = continuation
    }

    func resume(returning value: Value) {
        lock.lock()
        defer { lock.unlock() }
        guard !resumed else {
            return
        }
        resumed = true
        continuation.resume(returning: value)
    }
}
