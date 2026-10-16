<#
.SYNOPSIS
  LAB ONLY: keeps every logical CPU busy with a PowerShell loop (a process other than SQL Server).
  Expected RSM top cause: SLOW-CPU-NONSQL. Never run on a customer or production server; RSM never runs this.
#>
param(
    [int] $Minutes = 5,
    [int] $Threads = [Environment]::ProcessorCount
)

$answer = Read-Host "Lab machine '$env:COMPUTERNAME'? This loads all $Threads CPUs for $Minutes minutes. Type LAB to continue"
if ($answer -ne 'LAB') { Write-Host 'Cancelled.'; exit 1 }

$until = (Get-Date).AddMinutes($Minutes)
$jobs = 1..$Threads | ForEach-Object {
    Start-Job -ScriptBlock { param($end) while ((Get-Date) -lt $end) { $x = 0; for ($i = 0; $i -lt 100000; $i++) { $x += $i * $i } } } -ArgumentList $until
}
Write-Host "Load running until $until. Start the RSM capture now. Ctrl+C stops early."
try { $jobs | Wait-Job | Out-Null }
finally { $jobs | Stop-Job -PassThru | Remove-Job; Write-Host 'Stopped and cleaned up.' }
