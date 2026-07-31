param(
    [string] $ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    Join-Path $Root "artifacts"
} else {
    [IO.Path]::GetFullPath($ArtifactsRoot)
}
$RunId = [Guid]::NewGuid().ToString("N")
$StagingRoot = Join-Path $Artifacts ".staging-$RunId"
$ClientOut = Join-Path $Artifacts "client"
$ServerOut = Join-Path $Artifacts "server"
$ServerOsxArm64Out = Join-Path $Artifacts "server-osx-arm64"
$ServerOsxX64Out = Join-Path $Artifacts "server-osx-x64"
$WebOut = Join-Path $Artifacts "web\rwc-mlccs"

$Targets = @(
    @{ Name = "client"; Final = $ClientOut; Stage = (Join-Path $StagingRoot "client") },
    @{ Name = "server"; Final = $ServerOut; Stage = (Join-Path $StagingRoot "server") },
    @{ Name = "server-osx-arm64"; Final = $ServerOsxArm64Out; Stage = (Join-Path $StagingRoot "server-osx-arm64") },
    @{ Name = "server-osx-x64"; Final = $ServerOsxX64Out; Stage = (Join-Path $StagingRoot "server-osx-x64") },
    @{ Name = "web"; Final = $WebOut; Stage = (Join-Path $StagingRoot "web\rwc-mlccs") }
)

$ServerTargets = @(
    @{ Runtime = "win-x64"; Output = ($Targets | Where-Object Name -eq "server").Stage },
    @{ Runtime = "osx-arm64"; Output = ($Targets | Where-Object Name -eq "server-osx-arm64").Stage },
    @{ Runtime = "osx-x64"; Output = ($Targets | Where-Object Name -eq "server-osx-x64").Stage }
)

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet command failed with exit code $LASTEXITCODE."
    }
}

function Assert-NoLiveProcessInDirectory {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    foreach ($process in Get-Process) {
        try { $executable = [string] $process.Path } catch { continue }
        if (-not [string]::IsNullOrWhiteSpace($executable) -and
            [IO.Path]::GetFullPath($executable).StartsWith(
                $full, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to replace '$Path': PID $($process.Id) is running '$executable'."
        }
    }
}

function Publish-StagedDirectory {
    param(
        [Parameter(Mandatory)][string] $Stage,
        [Parameter(Mandatory)][string] $Final
    )
    if (-not (Test-Path -LiteralPath $Stage)) {
        throw "Staged output is missing: $Stage"
    }
    Assert-NoLiveProcessInDirectory -Path $Final
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Final) | Out-Null
    $previous = "$Final.previous-$RunId"
    $movedPrevious = $false
    try {
        if (Test-Path -LiteralPath $Final) {
            Move-Item -LiteralPath $Final -Destination $previous
            $movedPrevious = $true
        }
        Move-Item -LiteralPath $Stage -Destination $Final
        if ($movedPrevious -and (Test-Path -LiteralPath $previous)) {
            Remove-Item -LiteralPath $previous -Recurse -Force
        }
    } catch {
        if (-not (Test-Path -LiteralPath $Final) -and
            (Test-Path -LiteralPath $previous)) {
            Move-Item -LiteralPath $previous -Destination $Final
        }
        throw
    }
}

New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
foreach ($target in $Targets) {
    New-Item -ItemType Directory -Force -Path $target.Stage | Out-Null
}

try {
    Invoke-DotNet @(
        "test",
        (Join-Path $Root "RWC-MLCCS.sln"),
        "-c", "Release",
        "-p:EnableWindowsTargeting=true",
        "-p:RollForward=Major"
    )
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
        "-p:EnableWindowsTargeting=true",
        "-o", ($Targets | Where-Object Name -eq "client").Stage
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
        Copy-Item -LiteralPath (Join-Path $Root "server.sample.json") -Destination (Join-Path $target.Output "server.json") -Force
    }
    Copy-Item -LiteralPath (Join-Path $Root "public-bootstrap.json") -Destination (Join-Path (($Targets | Where-Object Name -eq "web").Stage) "config.json") -Force
    foreach ($target in $Targets) {
        Assert-NoLiveProcessInDirectory -Path $target.Final
    }
    foreach ($target in $Targets) {
        Publish-StagedDirectory -Stage $target.Stage -Final $target.Final
    }
} finally {
    if (Test-Path -LiteralPath $StagingRoot) {
        Remove-Item -LiteralPath $StagingRoot -Recurse -Force
    }
}

Write-Host "Client: $ClientOut"
Write-Host "Server (win-x64): $ServerOut"
Write-Host "Server (osx-arm64): $ServerOsxArm64Out"
Write-Host "Server (osx-x64): $ServerOsxX64Out"
Write-Host "Web config: $WebOut"
