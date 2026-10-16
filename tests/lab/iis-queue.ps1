<#
.SYNOPSIS
  LAB ONLY: sends many concurrent requests to a slow endpoint so the HTTP.sys queue of the lab pool backs up.
  Before running, set the lab pool's Queue Length to 50 by hand (IIS Manager > Advanced Settings).
  Expected RSM top cause: SLOW-IIS-QUEUE. Never point this at a customer or production server; RSM never runs this.
#>
param(
    [Parameter(Mandatory)] [string] $Url,
    [int] $Concurrency = 200,
    [int] $Minutes = 5
)

$answer = Read-Host "Is '$Url' a lab server? This sends $Concurrency parallel requests for $Minutes minutes. Type LAB to continue"
if ($answer -ne 'LAB') { Write-Host 'Cancelled.'; exit 1 }

$until = (Get-Date).AddMinutes($Minutes)
$jobs = 1..$Concurrency | ForEach-Object {
    Start-Job -ScriptBlock {
        param($u, $end)
        while ((Get-Date) -lt $end) { try { Invoke-WebRequest -UseBasicParsing -TimeoutSec 120 $u | Out-Null } catch { } }
    } -ArgumentList $Url, $until
}
Write-Host "Load running until $until. Start the RSM capture now. Ctrl+C stops early."
try { $jobs | Wait-Job | Out-Null }
finally { $jobs | Stop-Job -PassThru | Remove-Job; Write-Host 'Stopped and cleaned up.' }
