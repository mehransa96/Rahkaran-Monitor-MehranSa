<#
.SYNOPSIS
  LAB ONLY: allocates memory until only -LeaveMb MB is available, holds it, then releases it.
  Expected RSM top cause: SLOW-MEM-OS. Never run on a customer or production server; RSM never runs this.
#>
param(
    [int] $LeaveMb = 300,
    [int] $Minutes = 5
)

$answer = Read-Host "Lab machine '$env:COMPUTERNAME'? This leaves about $LeaveMb MB free for $Minutes minutes. Type LAB to continue"
if ($answer -ne 'LAB') { Write-Host 'Cancelled.'; exit 1 }

$blocks = New-Object System.Collections.Generic.List[byte[]]
try {
    while ((Get-Counter '\Memory\Available MBytes').CounterSamples[0].CookedValue -gt $LeaveMb) {
        $block = New-Object byte[] (64MB)
        for ($i = 0; $i -lt $block.Length; $i += 4096) { $block[$i] = 1 }   # touch every page so it is committed
        $blocks.Add($block)
    }
    Write-Host "Holding $($blocks.Count * 64) MB until $((Get-Date).AddMinutes($Minutes)). Start the RSM capture now."
    Start-Sleep -Seconds ($Minutes * 60)
}
finally { $blocks.Clear(); [GC]::Collect(); Write-Host 'Memory released.' }
