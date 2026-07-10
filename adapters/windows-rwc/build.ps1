$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Artifacts = Join-Path $Root "artifacts"
$ClientOut = Join-Path $Artifacts "client"
$ServerOut = Join-Path $Artifacts "server"
$ServerOsxArm64Out = Join-Path $Artifacts "server-osx-arm64"
$ServerOsxX64Out = Join-Path $Artifacts "server-osx-x64"
$WebOut = Join-Path $Artifacts "web\rwc-mlccs"

$ServerTargets = @(
    @{ Runtime = "win-x64"; Output = $ServerOut },
    @{ Runtime = "osx-arm64"; Output = $ServerOsxArm64Out },
    @{ Runtime = "osx-x64"; Output = $ServerOsxX64Out }
)

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

foreach ($path in @($ClientOut, $WebOut) + ($ServerTargets | ForEach-Object { $_.Output })) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

Invoke-DotNet @("test", (Join-Path $Root "RWC-MLCCS.sln"), "-c", "Release")

Invoke-DotNet @(
    "publish",
    (Join-Path $Root "src\RWC-MLCCS.Client\RWC-MLCCS.Client.csproj"),
    "-c", "Release",
    "-r", "win-x64",
    "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=None",
    "-p:DebugSymbols=false",
    "-o", $ClientOut
)

foreach ($target in $ServerTargets) {
    Invoke-DotNet @(
        "publish",
        (Join-Path $Root "src\RWC-MLCCS.Server\RWC-MLCCS.Server.csproj"),
        "-c", "Release",
        "-r", $target.Runtime,
        "--self-contained", "true",
        "-p:PublishSingleFile=true",
        "-p:IncludeNativeLibrariesForSelfExtract=true",
        "-p:EnableCompressionInSingleFile=true",
        "-p:DebugType=None",
        "-p:DebugSymbols=false",
        "-o", $target.Output
    )

    Copy-Item -LiteralPath (Join-Path $Root "server.sample.json") -Destination (Join-Path $($target.Output) "server.json") -Force
}

Copy-Item -LiteralPath (Join-Path $Root "config.sample.json") -Destination (Join-Path $WebOut "config.json") -Force

Write-Host "Client: $ClientOut"
foreach ($target in $ServerTargets) {
    Write-Host "Server ($($target.Runtime)): $($target.Output)"
}
Write-Host "Web config: $WebOut"
