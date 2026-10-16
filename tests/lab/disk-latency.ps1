<#
.SYNOPSIS
  LAB ONLY: saturates the volume of the lab database with Microsoft DiskSpd (random 8 KB, 40 % writes).
  Expected RSM top cause: SLOW-DISK-DATA. Never run on a customer or production server; RSM never runs this.
#>
param(
    [Parameter(Mandatory)] [string] $DiskSpd,
    [Parameter(Mandatory)] [string] $Target,
    [int] $Seconds = 300,
    [string] $FileSize = '20G'
)

$answer = Read-Host "Lab machine '$env:COMPUTERNAME'? DiskSpd will write a $FileSize file in $Target and load that volume for $Seconds s. Type LAB to continue"
if ($answer -ne 'LAB') { Write-Host 'Cancelled.'; exit 1 }

New-Item -ItemType Directory -Force -Path $Target | Out-Null
$file = Join-Path $Target 'rsm-lab-io.dat'
Write-Host 'Load running. Start the RSM capture now.'
try { & $DiskSpd "-c$FileSize" "-d$Seconds" -r -w40 -t8 -o32 -b8K -Sh -L $file }
finally { Remove-Item -Force -ErrorAction SilentlyContinue $file; Write-Host 'Test file removed.' }
