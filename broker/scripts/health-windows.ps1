$ErrorActionPreference = 'Stop'
$task = Get-ScheduledTask -TaskName 'CRC-MLCCS Broker'
$health = Invoke-RestMethod -Uri 'http://127.0.0.1:7590/healthz' -TimeoutSec 5
if ($task.State -ne 'Running' -or -not $health.ok -or $health.v -ne 2) {
    throw "CRC broker unhealthy (task=$($task.State), response=$($health | ConvertTo-Json -Compress))"
}
$health | ConvertTo-Json
