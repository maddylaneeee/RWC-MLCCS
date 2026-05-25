$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Artifacts = Join-Path $Root "artifacts"
$ClientOut = Join-Path $Artifacts "client"
$ServerOut = Join-Path $Artifacts "server"
$WebOut = Join-Path $Artifacts "web\rwc-mlccs"

foreach ($path in @($ClientOut, $ServerOut, $WebOut)) {
    if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $path | Out-Null
}

dotnet test (Join-Path $Root "RWC-MLCCS.sln") -c Release

dotnet publish (Join-Path $Root "src\RWC-MLCCS.Client\RWC-MLCCS.Client.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $ClientOut

dotnet publish (Join-Path $Root "src\RWC-MLCCS.Server\RWC-MLCCS.Server.csproj") `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $ServerOut

Copy-Item -LiteralPath (Join-Path $Root "server.sample.json") -Destination (Join-Path $ServerOut "server.json") -Force
Copy-Item -LiteralPath (Join-Path $Root "config.sample.json") -Destination (Join-Path $WebOut "config.json") -Force

Write-Host "Client: $ClientOut"
Write-Host "Server: $ServerOut"
Write-Host "Web config: $WebOut"
