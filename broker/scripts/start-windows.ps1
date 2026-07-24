$ErrorActionPreference = 'Stop'
Start-ScheduledTask -TaskName 'CRC-MLCCS Broker'
Get-ScheduledTaskInfo -TaskName 'CRC-MLCCS Broker'
