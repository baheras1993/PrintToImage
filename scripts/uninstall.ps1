<#
.SYNOPSIS
    Removes the PrintToImage service, its firewall rules and program files.

.DESCRIPTION
    The saved documents are kept: the output folder is never deleted by this script.
    Settings, logs and the job archive in C:\ProgramData\PrintToImage are kept unless -RemoveData is given.

    powershell -ExecutionPolicy Bypass -File .\uninstall.ps1 [-RemoveData]
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\PrintToImage",
    [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'PrintToImage'
$DataRoot = Join-Path $env:ProgramData 'PrintToImage'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this script in an elevated PowerShell (Run as administrator).'
}

$outputRoot = $null
$settingsPath = Join-Path $DataRoot 'settings.json'
if (Test-Path $settingsPath) { $outputRoot = (Get-Content $settingsPath -Raw | ConvertFrom-Json).OutputRoot }

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    Write-Host '==> Stopping and removing the service' -ForegroundColor Cyan
    if ($svc.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
    & sc.exe delete $ServiceName | Out-Null
}

$printerName = 'PrintToImage'
if (Test-Path $settingsPath) { $n = (Get-Content $settingsPath -Raw | ConvertFrom-Json).SharedPrinterName; if ($n) { $printerName = $n } }
$sharedScript = Join-Path $InstallDir 'shared-printer.ps1'
if (Test-Path $sharedScript) {
    Write-Host "==> Removing the shared printer $printerName" -ForegroundColor Cyan
    & $sharedScript -Name $printerName -Remove | Out-Null
}

Write-Host '==> Removing firewall rules' -ForegroundColor Cyan
Get-NetFirewallRule -Group 'PrintToImage' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

if ([Diagnostics.EventLog]::SourceExists($ServiceName)) { Remove-EventLog -Source $ServiceName }

foreach ($folder in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
    Remove-Item -Path (Join-Path $folder 'PrintToImage Settings.lnk') -ErrorAction SilentlyContinue
}
Get-Process PrintToImageSettings -ErrorAction SilentlyContinue | Stop-Process -Force

if (Test-Path $InstallDir) {
    Write-Host "==> Removing $InstallDir" -ForegroundColor Cyan
    Start-Sleep -Seconds 1
    Remove-Item -Path $InstallDir -Recurse -Force
}

if ($RemoveData -and (Test-Path $DataRoot)) {
    Write-Host "==> Removing settings, logs and job archive in $DataRoot" -ForegroundColor Cyan
    Remove-Item -Path $DataRoot -Recurse -Force
}

Write-Host ''
Write-Host 'PrintToImage has been removed.' -ForegroundColor Green
if (-not $RemoveData) { Write-Host "  Kept: $DataRoot (settings, logs, archive). Use -RemoveData to delete it." }
if ($outputRoot) { Write-Host "  Kept: $outputRoot (converted documents). Delete it yourself if it is no longer needed." }
