param(
    [string[]] $Runtime = @(),
    [string] $ArtifactsRoot = ""
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Artifacts = if ([string]::IsNullOrWhiteSpace($ArtifactsRoot)) {
    Join-Path $Root "artifacts"
} else {
    [IO.Path]::GetFullPath($ArtifactsRoot)
}
$ServerProject = Join-Path $Root "src\RWC-MLCCS.Server\RWC-MLCCS.Server.csproj"
$ServerConfig = Join-Path $Root "server.sample.json"
$RunId = [Guid]::NewGuid().ToString("N")
$StagingRoot = Join-Path $Artifacts ".staging-$RunId"

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

if ($Runtime.Count -eq 0) {
    $Runtime = @([System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier)
}

New-Item -ItemType Directory -Force -Path $StagingRoot | Out-Null
try {
    Invoke-DotNet @(
        "test",
        (Join-Path $Root "tests\RWC-MLCCS.Tests\RWC-MLCCS.Tests.csproj"),
        "-c", "Release"
    )
    $builtTargets = @()
    foreach ($rid in $Runtime) {
        $outputName = if ($rid -eq "win-x64") { "server" } else { "server-$rid" }
        $output = Join-Path $Artifacts $outputName
        $stage = Join-Path $StagingRoot $outputName
        New-Item -ItemType Directory -Force -Path $stage | Out-Null
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
            "-o", $stage
        )
        Copy-Item -LiteralPath $ServerConfig -Destination (Join-Path $stage "server.json") -Force
        $executable = Join-Path $stage "RWC-MLCCS.Server"
        if ((Test-Path -LiteralPath $executable) -and ($IsMacOS -or $IsLinux)) {
            chmod +x $executable
        }
        $builtTargets += @{
            Runtime = $rid
            Stage = $stage
            Final = $output
        }
    }
    foreach ($target in $builtTargets) {
        Assert-NoLiveProcessInDirectory -Path $target.Final
    }
    foreach ($target in $builtTargets) {
        Publish-StagedDirectory -Stage $target.Stage -Final $target.Final
        Write-Host "Server ($($target.Runtime)): $($target.Final)"
    }
} finally {
    if (Test-Path -LiteralPath $StagingRoot) {
        Remove-Item -LiteralPath $StagingRoot -Recurse -Force
    }
}
