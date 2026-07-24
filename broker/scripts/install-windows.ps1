[CmdletBinding()]
param(
    [string]$InstallRoot = 'C:\MLCCSapps\CRC-MLCCS\broker',
    [string]$NodePath = 'C:\Program Files\nodejs\node.exe',
    [string]$SourceRoot = (Split-Path -Parent $PSScriptRoot)
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $NodePath)) { throw "Node.js not found: $NodePath" }

$projectSourceRoot = Split-Path -Parent $SourceRoot
$projectInstallRoot = Split-Path -Parent $InstallRoot
New-Item -ItemType Directory -Force -Path $projectInstallRoot | Out-Null
if ([IO.Path]::GetFullPath($projectSourceRoot) -ne [IO.Path]::GetFullPath($projectInstallRoot)) {
    New-Item -ItemType Directory -Force -Path $InstallRoot | Out-Null
    foreach ($directory in @('src', 'scripts', 'iis')) {
        Copy-Item -Recurse -Force (Join-Path $SourceRoot $directory) $InstallRoot
    }
    New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot 'config') | Out-Null
    Copy-Item -Force (Join-Path $SourceRoot 'config\sample.json') (Join-Path $InstallRoot 'config\sample.json')
    foreach ($file in @('broker.mjs', 'audit.mjs', 'README.md')) {
        Copy-Item -Force (Join-Path $SourceRoot $file) $InstallRoot
    }
    Copy-Item -Recurse -Force (Join-Path $projectSourceRoot 'protocol') $projectInstallRoot
    Copy-Item -Force (Join-Path $projectSourceRoot 'package.json') $projectInstallRoot
    Copy-Item -Force (Join-Path $projectSourceRoot 'package-lock.json') $projectInstallRoot
}
Push-Location $projectInstallRoot
try { & npm.cmd ci --omit=dev } finally { Pop-Location }

$privateConfig = Join-Path $InstallRoot 'config\private.json'
if (-not (Test-Path $privateConfig)) {
    & $NodePath (Join-Path $InstallRoot 'scripts\generate-private-config.mjs') $privateConfig
}

$codeAcl = New-Object System.Security.AccessControl.DirectorySecurity
$codeAcl.SetAccessRuleProtection($true, $false)
$codeAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow')))
$codeAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow')))
$codeAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Users','ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -Path $projectInstallRoot -AclObject $codeAcl

$secretAcl = New-Object System.Security.AccessControl.DirectorySecurity
$secretAcl.SetAccessRuleProtection($true, $false)
$secretAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('SYSTEM','FullControl','ContainerInherit,ObjectInherit','None','Allow')))
$secretAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule('BUILTIN\Administrators','FullControl','ContainerInherit,ObjectInherit','None','Allow')))
Set-Acl -Path (Join-Path $InstallRoot 'config') -AclObject $secretAcl
New-Item -ItemType Directory -Force -Path (Join-Path $InstallRoot 'state') | Out-Null
Set-Acl -Path (Join-Path $InstallRoot 'state') -AclObject $secretAcl

$action = New-ScheduledTaskAction -Execute $NodePath -Argument '"src\main.mjs" --config "config\private.json"' -WorkingDirectory $InstallRoot
$trigger = New-ScheduledTaskTrigger -AtStartup
$principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -RestartCount 10 -RestartInterval (New-TimeSpan -Minutes 1) -ExecutionTimeLimit ([TimeSpan]::Zero)
Register-ScheduledTask -TaskName 'CRC-MLCCS Broker' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName 'CRC-MLCCS Broker'
Write-Host 'Installed and started CRC-MLCCS Broker.'
