param(
    [string[]] $Runtime = @()
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Artifacts = Join-Path $Root "artifacts"
$ServerProject = Join-Path $Root "src\RWC-MLCCS.Server\RWC-MLCCS.Server.csproj"
$ServerConfig = Join-Path $Root "server.sample.json"

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

if ($Runtime.Count -eq 0) {
    $Runtime = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)
}

Invoke-DotNet @("test", (Join-Path $Root "tests\RWC-MLCCS.Tests\RWC-MLCCS.Tests.csproj"), "-c", "Release")

foreach ($rid in $Runtime) {
    $outputName = if ($rid -eq "win-x64") { "server" } else { "server-$rid" }
    $output = Join-Path $Artifacts $outputName

    if (Test-Path -LiteralPath $output) {
        Remove-Item -LiteralPath $output -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $output | Out-Null

    Invoke-DotNet @(
        "publish",
        $ServerProject,
        "-c", "Release",
        "-r", $rid,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $output
    )

    Copy-Item -LiteralPath $ServerConfig -Destination (Join-Path $output "server.json") -Force

    $executable = Join-Path $output "RWC-MLCCS.Server"
    if (Test-Path -LiteralPath $executable) {
        if ($IsMacOS -or $IsLinux) {
            chmod +x $executable
        }
    }

    Write-Host "Server ($rid): $output"
}
