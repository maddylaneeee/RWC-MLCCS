using System.Text;

namespace RWC_MLCCS.Client;

internal static class ClientToolRunner
{
    public static bool TryBuildScript(string command, out string script, out string toolName)
    {
        script = "";
        toolName = "";

        if (!command.StartsWith("tool ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var args = SplitArgs(command[5..]);
        if (args.Count == 0)
        {
            script = "Write-Error 'Usage: tool <name> [args...]'; exit 64";
            toolName = "";
            return true;
        }

        toolName = args[0].ToLowerInvariant();
        var toolArgs = args.Skip(1).ToArray();
        script = toolName switch
        {
            "deps" => DepsScript,
            "sysinfo" => SysinfoScript,
            "processes" => "Get-Process | Sort-Object CPU -Descending | Select-Object -First 120 Id,ProcessName,CPU,WS,StartTime -ErrorAction SilentlyContinue | Format-Table -AutoSize",
            "network" => NetworkScript,
            "apps" => AppsScript,
            "rg" => BuildRgScript(toolArgs),
            "python" => BuildPythonInlineScript(toolArgs),
            "python-file" => BuildPythonFileScript(toolArgs),
            "download-url" => BuildDownloadUrlScript(toolArgs),
            "fileshare-upload" => BuildFileShareUploadScript(toolArgs),
            "file-info" => BuildFileInfoScript(toolArgs),
            "read-text" => BuildReadTextScript(toolArgs),
            "clipboard" => "Get-Clipboard -Raw | Select-Object -First 1",
            "password-popup" => "Write-Error 'password-popup is only implemented by the macOS CRC client.'; exit 64",
            _ => $"Write-Error {PsQuote("unknown tool: " + toolName)}; exit 64"
        };
        return true;
    }

    private const string DepsScript = """
        function Get-PythonCommand {
          foreach ($name in @('python','python3')) {
            $cmd = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($cmd) { return $cmd.Source }
          }
          $launcher = Get-Command py -ErrorAction SilentlyContinue | Select-Object -First 1
          if ($launcher) { return $launcher.Source }
          return $null
        }

        $names = @('rg','python','python3','py','git','node','npm','pwsh','powershell','winget','curl')
        foreach ($name in $names) {
          $cmd = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
          if ($cmd) {
            "{0,-12} {1}" -f ($name + ':'), $cmd.Source
          } else {
            "{0,-12} not found" -f ($name + ':')
          }
        }
        ""
        "[powershell]"
        $PSVersionTable.PSVersion.ToString()
        ""
        "[rg]"
        if (Get-Command rg -ErrorAction SilentlyContinue) { rg --version | Select-Object -First 1 }
        ""
        "[python]"
        $py = Get-PythonCommand
        if ($py) { & $py --version 2>&1 }
        ""
        """;

    private const string SysinfoScript = """
        "computer_name: $env:COMPUTERNAME"
        "user: $env:USERDOMAIN\$env:USERNAME"
        "os: $((Get-CimInstance Win32_OperatingSystem).Caption) $((Get-CimInstance Win32_OperatingSystem).Version)"
        "uptime: $((Get-Date) - (Get-CimInstance Win32_OperatingSystem).LastBootUpTime)"
        ""
        "[cpu]"
        Get-CimInstance Win32_Processor | Select-Object Name,NumberOfCores,NumberOfLogicalProcessors | Format-List
        "[memory]"
        Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize,FreePhysicalMemory | Format-List
        "[disk]"
        Get-PSDrive -PSProvider FileSystem | Select-Object Name,Root,Used,Free | Format-Table -AutoSize
        """;

    private const string NetworkScript = """
        "[ip configuration]"
        Get-NetIPConfiguration | Format-List
        "[dns]"
        Get-DnsClientServerAddress | Format-Table -AutoSize
        "[routes]"
        Get-NetRoute -AddressFamily IPv4 | Sort-Object RouteMetric | Select-Object -First 80 | Format-Table -AutoSize
        """;

    private const string AppsScript = """
        $roots = @(
          'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
          'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*',
          'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*'
        )
        Get-ItemProperty $roots -ErrorAction SilentlyContinue |
          Where-Object DisplayName |
          Sort-Object DisplayName |
          Select-Object -First 250 DisplayName,DisplayVersion,Publisher,InstallDate |
          Format-Table -AutoSize
        """;

    private static string BuildRgScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool rg <pattern> [path] [maxLines]'; exit 64";
        }

        var pattern = args[0];
        var path = args.Count >= 2 ? args[1] : ".";
        var maxLines = ParseLimit(args, 2, 200, 5000);
        return $$"""
            $pattern = {{PsQuote(pattern)}}
            $root = {{PsQuote(path)}}
            $maxLines = {{maxLines}}
            $rg = Get-Command rg -ErrorAction SilentlyContinue | Select-Object -First 1
            if ($rg) {
              & $rg.Source --hidden --glob '!.git' --glob '!node_modules' --line-number --color never -- $pattern $root | Select-Object -First $maxLines
            } else {
              Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notmatch '\\(\.git|node_modules)\\' } |
                Select-String -Pattern $pattern -ErrorAction SilentlyContinue |
                Select-Object -First $maxLines |
                ForEach-Object { "$($_.Path):$($_.LineNumber):$($_.Line)" }
            }
            """;
    }

    private static string BuildPythonInlineScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool python <code> [args...]'; exit 64";
        }

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(args[0]));
        return BuildPythonScript(encoded, args.Skip(1).ToArray());
    }

    private static string BuildPythonFileScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool python-file <path> [args...]'; exit 64";
        }

        var psArgs = PsArray(args.Skip(1));
        return $$"""
            function Get-CrcPythonCommand {
              foreach ($name in @('python','python3')) {
                $cmd = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($cmd) { return $cmd.Source }
              }
              $launcher = Get-Command py -ErrorAction SilentlyContinue | Select-Object -First 1
              if ($launcher) { return $launcher.Source }
              return $null
            }

            $python = Get-CrcPythonCommand
            if (-not $python) { Write-Error 'python/python3/py not found'; exit 127 }
            $scriptPath = {{PsQuote(args[0])}}
            $scriptArgs = {{psArgs}}
            if ((Split-Path -Leaf $python) -ieq 'py.exe') {
              & $python -3 $scriptPath @scriptArgs
            } else {
              & $python $scriptPath @scriptArgs
            }
            exit $LASTEXITCODE
            """;
    }

    private static string BuildDownloadUrlScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool download-url <url> [outputPath]'; exit 64";
        }

        var outputPath = args.Count >= 2 ? args[1] : "";
        return $$"""
            $url = {{PsQuote(args[0])}}
            $out = {{PsQuote(outputPath)}}
            if (-not $out) {
              $uri = [Uri]$url
              $name = [IO.Path]::GetFileName($uri.AbsolutePath)
              if (-not $name) { $name = 'download' }
              $out = Join-Path $env:TEMP $name
            }
            $dir = Split-Path -Parent $out
            if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
            Invoke-WebRequest -Uri $url -OutFile $out -UseBasicParsing
            Get-Item -LiteralPath $out | Format-List FullName,Length,LastWriteTime
            """;
    }

    private static string BuildFileShareUploadScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool fileshare-upload <path> [baseUrl]'; exit 64";
        }

        var baseUrl = args.Count >= 2 ? args[1] : "https://lixinchen.ca";
        return $$"""
            Add-Type -AssemblyName System.Web
            $path = {{PsQuote(args[0])}}
            $base = {{PsQuote(baseUrl)}}.TrimEnd('/') + '/'
            $file = Get-Item -LiteralPath $path -Force
            if (-not $file -or $file.PSIsContainer) { Write-Error "not a file: $path"; exit 64 }
            if ($file.Length -le 0) { Write-Error 'empty files are not supported by the current FileShare backend'; exit 64 }
            $timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds().ToString()
            $chunkSize = 512 * 1024
            $totalChunks = [Math]::Ceiling($file.Length / $chunkSize)
            $name = $file.Name
            $stream = [IO.File]::OpenRead($file.FullName)
            try {
              $buffer = [byte[]]::new($chunkSize)
              for ($index = 0; $index -lt $totalChunks; $index++) {
                $read = $stream.Read($buffer, 0, $buffer.Length)
                if ($read -le 0) { throw "unexpected empty chunk at index $index" }
                if ($read -eq $buffer.Length) {
                  $chunk = $buffer
                } else {
                  $chunk = [byte[]]::new($read)
                  [Array]::Copy($buffer, $chunk, $read)
                }
                $query = 'timestamp=' + [System.Web.HttpUtility]::UrlEncode($timestamp) +
                  '&file_name=' + [System.Web.HttpUtility]::UrlEncode($name) +
                  '&chunk_index=' + $index +
                  '&total_chunks=' + $totalChunks
                Invoke-RestMethod -Uri ($base + 'api/upload/chunk?' + $query) -Method Post -ContentType 'application/octet-stream' -Body $chunk | Out-Null
                Write-Error "$name: chunk $($index + 1)/$totalChunks"
              }
            } finally {
              $stream.Dispose()
            }
            $complete = @{
              timestamp = $timestamp
              file_name = $name
              total_chunks = [int]$totalChunks
              file_size = $file.Length
            } | ConvertTo-Json -Compress
            $data = Invoke-RestMethod -Uri ($base + 'api/upload/complete') -Method Post -ContentType 'application/json' -Body $complete
            $finalName = if ($data.file_name) { [string]$data.file_name } else { $name }
            'timestamp: ' + $timestamp
            $base + 'fileshare/' + [Uri]::EscapeDataString($timestamp) + '/' + [Uri]::EscapeDataString($finalName)
            if ($data.url) { [string]$data.url }
            """;
    }

    private static string BuildPythonScript(string encodedCode, IReadOnlyList<string> args)
    {
        var psArgs = PsArray(args);
        return $$"""
            function Get-CrcPythonCommand {
              foreach ($name in @('python','python3')) {
                $cmd = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
                if ($cmd) { return $cmd.Source }
              }
              $launcher = Get-Command py -ErrorAction SilentlyContinue | Select-Object -First 1
              if ($launcher) { return $launcher.Source }
              return $null
            }

            $python = Get-CrcPythonCommand
            if (-not $python) { Write-Error 'python/python3/py not found'; exit 127 }
            $tmp = Join-Path ([IO.Path]::GetTempPath()) ('crc-python-' + [guid]::NewGuid().ToString('N') + '.py')
            try {
              [IO.File]::WriteAllText($tmp, [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String({{PsQuote(encodedCode)}})), [Text.UTF8Encoding]::new($false))
              $scriptArgs = {{psArgs}}
              if ((Split-Path -Leaf $python) -ieq 'py.exe') {
                & $python -3 $tmp @scriptArgs
              } else {
                & $python $tmp @scriptArgs
              }
              exit $LASTEXITCODE
            } finally {
              Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
            }
            """;
    }

    private static string BuildFileInfoScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool file-info <path>'; exit 64";
        }

        return $$"""
            $path = {{PsQuote(args[0])}}
            Get-Item -LiteralPath $path -Force | Format-List FullName,Length,Mode,CreationTime,LastWriteTime,Attributes
            """;
    }

    private static string BuildReadTextScript(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            return "Write-Error 'usage: tool read-text <path> [maxBytes]'; exit 64";
        }

        var maxBytes = ParseLimit(args, 1, 20000, 1_000_000);
        return $$"""
            $path = {{PsQuote(args[0])}}
            $maxBytes = {{maxBytes}}
            $stream = [IO.File]::OpenRead($path)
            try {
              $buffer = [byte[]]::new([Math]::Min($maxBytes, [int]$stream.Length))
              $read = $stream.Read($buffer, 0, $buffer.Length)
              [Text.Encoding]::UTF8.GetString($buffer, 0, $read)
            } finally {
              $stream.Dispose()
            }
            """;
    }

    private static int ParseLimit(IReadOnlyList<string> args, int index, int defaultValue, int maxValue)
    {
        return args.Count > index && int.TryParse(args[index], out var parsed)
            ? Math.Clamp(parsed, 1, maxValue)
            : defaultValue;
    }

    private static string PsArray(IEnumerable<string> values)
    {
        var items = values.Select(PsQuote).ToArray();
        return items.Length == 0 ? "@()" : "@(" + string.Join(", ", items) + ")";
    }

    private static string PsQuote(string value)
    {
        return "'" + value.Replace("'", "''") + "'";
    }

    private static List<string> SplitArgs(string input)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaped = false;

        foreach (var ch in input)
        {
            if (escaped)
            {
                current.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                escaped = true;
                continue;
            }

            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
                continue;
            }

            if (ch is '"' or '\'')
            {
                quote = ch;
                continue;
            }

            if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (escaped)
        {
            current.Append('\\');
        }
        if (current.Length > 0)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}
