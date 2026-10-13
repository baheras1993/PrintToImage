<#
.SYNOPSIS
    Installs (or upgrades) the PrintToImage network printer service.

.DESCRIPTION
    - copies the program to the install folder
    - creates C:\ProgramData\PrintToImage (settings, logs, archive) and the output folder
    - restricts both folders to Administrators, SYSTEM, the service and (optionally) the reader account (the program that picks up the files)
    - creates the Windows service (automatic start, restarts on failure) running as "NT SERVICE\PrintToImage"
    - creates the shared printer \\SERVER\PrintToImage that the PCs print to (enables the Print Spooler and
      "File and Printer Sharing"); jobs go from it to the service on this computer
    - adds Windows Firewall rules for TCP 9100 (and 515 if LPD is enabled) for devices that print directly
    - adds "PrintToImage Settings" (the settings app) to the Start menu and the desktop

    Normally started by "Install PrintToImage.bat". To use all options, run it in an elevated PowerShell
    from the package folder:
        powershell -ExecutionPolicy Bypass -File .\files\install.ps1 -OutputRoot D:\PrintToImage\Output -EmrAccount "CLINIC\EMR-Service"

.PARAMETER OutputRoot
    Root of the output folders. Only used when settings.json is created for the first time.
.PARAMETER EmrAccount
    User or group that reads (and removes) the files, e.g. the account an EMR or document system runs as. Gets Modify on the output folder.
.PARAMETER AllowedRemoteAddress
    Who may connect to the printer ports: "LocalSubnet" (default), "DeviceIPs" (the IPs listed in the settings), or a list
    such as "192.168.1.20,192.168.1.21". Stored in settings.json; later runs without this parameter keep the stored value.
.PARAMETER EnableLpd
    Also listen for LPR/LPD jobs on TCP 515 (written to settings.json on first install).
.PARAMETER NoSharedPrinter
    Do not create the shared printer \\SERVER\PrintToImage (then each PC must print directly to TCP 9100).
.PARAMETER PaperSize
    Paper size of the shared printer: A4 (default) or Letter.
.PARAMETER ServiceAccount
    "NT SERVICE\PrintToImage" (default, least privilege) or "LocalSystem".
#>
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\PrintToImage",
    [string]$OutputRoot = 'D:\PrintToImage\Output',
    [string]$EmrAccount = '',
    [string]$AllowedRemoteAddress = 'LocalSubnet',
    [switch]$EnableLpd,
    [switch]$NoSharedPrinter,
    [ValidateSet('A4', 'Letter')][string]$PaperSize = 'A4',
    [string]$ServiceAccount = 'NT SERVICE\PrintToImage'
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'PrintToImage'
$DataRoot = Join-Path $env:ProgramData 'PrintToImage'
$Source = $PSScriptRoot

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

# ---------------------------------------------------------------- checks
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Please run this script in an elevated PowerShell (Run as administrator).'
}
if (-not (Test-Path (Join-Path $Source 'PrintToImage.exe'))) {
    throw "PrintToImage.exe was not found next to this script ($Source)."
}

# ---------------------------------------------------------------- stop old version
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing) {
    Step 'Stopping the existing service (upgrade)'
    if ($existing.Status -ne 'Stopped') { Stop-Service -Name $ServiceName -Force; (Get-Service $ServiceName).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
}

# ---------------------------------------------------------------- program files
Step "Copying program files to $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ((Resolve-Path $Source).Path.TrimEnd('\') -ne (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
    Get-ChildItem -Path $Source -File | Where-Object { $_.Extension -ne '.pdb' } | Copy-Item -Destination $InstallDir -Force
    # The guide sits one level up in the package (next to the .bat files).
    $guide = Join-Path (Split-Path $Source -Parent) 'README.txt'
    if (Test-Path $guide) { Copy-Item $guide $InstallDir -Force }
}
$exe = Join-Path $InstallDir 'PrintToImage.exe'

# ---------------------------------------------------------------- data folders and settings
Step "Creating $DataRoot"
foreach ($d in @($DataRoot, "$DataRoot\logs", "$DataRoot\archive", "$DataRoot\work")) { New-Item -ItemType Directory -Force -Path $d | Out-Null }
# Make everything inside inherit the folder's permissions (also repairs files with broken permissions).
& icacls.exe (Join-Path $DataRoot '*') /reset /T /C /Q | Out-Null
$settingsPath = Join-Path $DataRoot 'settings.json'
if (-not (Test-Path $settingsPath)) {
    Step 'Creating settings.json'
    $sample = Join-Path $Source 'settings.sample.json'
    if (-not (Test-Path $sample)) { throw 'settings.sample.json is missing next to the script.' }
    # Text replacement keeps the sample's layout readable for later editing.
    $text = Get-Content $sample -Raw
    $text = $text -replace '"OutputRoot":\s*"[^"]*"', ('"OutputRoot": "' + $OutputRoot.Replace('\', '\\') + '"')
    $text = $text -replace '"EnableLpd":\s*(true|false)', ('"EnableLpd": ' + ([string][bool]$EnableLpd).ToLower())
    [IO.File]::WriteAllText($settingsPath, $text, (New-Object Text.UTF8Encoding($false)))
} else {
    Write-Host "    Keeping the existing $settingsPath"
}
# Remember the reader account (EmrAccount) and firewall choice in the settings (the settings app shows and changes them).
function Set-SettingText([string]$key, [string]$value) {
    $text = [IO.File]::ReadAllText($settingsPath)
    $json = ($value | ConvertTo-Json)  # JSON-escaped string with quotes
    if ($text -match ('"' + $key + '"\s*:')) {
        $text = [regex]::Replace($text, '"' + $key + '"\s*:\s*"(?:[^"\\]|\\.)*"', { param($m) '"' + $key + '": ' + $json })
    } else {
        $i = $text.IndexOf('{')
        $text = $text.Substring(0, $i + 1) + "`r`n  `"$key`": $json," + $text.Substring($i + 1)
    }
    [IO.File]::WriteAllText($settingsPath, $text, (New-Object Text.UTF8Encoding($false)))
}
if ($PSBoundParameters.ContainsKey('EmrAccount')) { Set-SettingText 'EmrAccount' $EmrAccount }
if ($PSBoundParameters.ContainsKey('AllowedRemoteAddress')) { Set-SettingText 'FirewallRemoteAddress' $AllowedRemoteAddress }

$settings = Get-Content $settingsPath -Raw | ConvertFrom-Json
$OutputRoot = $settings.OutputRoot
if (-not $PSBoundParameters.ContainsKey('EmrAccount') -and $settings.EmrAccount) { $EmrAccount = $settings.EmrAccount }
if (-not $PSBoundParameters.ContainsKey('AllowedRemoteAddress') -and $settings.FirewallRemoteAddress) { $AllowedRemoteAddress = $settings.FirewallRemoteAddress }
if ($AllowedRemoteAddress -eq 'DeviceIPs') {
    $ips = @(); if ($settings.Devices) { $ips = $settings.Devices.PSObject.Properties.Name }
    $AllowedRemoteAddress = if ($ips.Count -gt 0) { $ips -join ',' } else { 'LocalSubnet' }
}
$lpd = [bool]$settings.EnableLpd
$rawPort = if ($settings.RawPort) { [int]$settings.RawPort } else { 9100 }
$lpdPort = if ($settings.LpdPort) { [int]$settings.LpdPort } else { 515 }

Step "Creating the output folders under $OutputRoot"
$folders = @($OutputRoot, (Join-Path $OutputRoot 'Unassigned'), (Join-Path $OutputRoot 'Failed'), (Join-Path $OutputRoot 'UnknownDevice'))
if ($settings.Devices) { foreach ($p in $settings.Devices.PSObject.Properties) { $folders += Join-Path $OutputRoot $p.Value } }
foreach ($d in $folders) { New-Item -ItemType Directory -Force -Path $d | Out-Null }

# ---------------------------------------------------------------- service
if (-not $existing) {
    Step 'Creating the Windows service'
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'PrintToImage network printer' `
        -Description 'Network virtual printer: receives print jobs (shared printer, TCP 9100, LPD) and saves them as PDF, JPG or PNG files.' `
        -StartupType Automatic | Out-Null
} else {
    & sc.exe config $ServiceName binPath= "`"$exe`"" | Out-Null
}
# Virtual service accounts (NT SERVICE\...) have no password; passing one makes sc.exe fail.
& sc.exe config $ServiceName obj= $ServiceAccount | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Could not set the service account to $ServiceAccount (sc.exe exit code $LASTEXITCODE)." }
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/60000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

if (-not [Diagnostics.EventLog]::SourceExists($ServiceName)) {
    New-EventLog -LogName Application -Source $ServiceName
}

# ---------------------------------------------------------------- permissions (confidential data)
Step 'Restricting folder permissions'
$svcAccount = if ($ServiceAccount -eq 'LocalSystem') { '*S-1-5-18' } else { $ServiceAccount }
function Restrict($path, [string[]]$modifyAccounts) {
    # Remove inherited permissions; SYSTEM and Administrators full control; listed accounts modify.
    $grants = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
    foreach ($a in $modifyAccounts) { if ($a) { $grants += "${a}:(OI)(CI)M" } }
    # The folder gets the explicit permissions; everything inside inherits them.
    & icacls.exe $path /inheritance:r /grant:r $grants /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Setting permissions on $path failed (icacls exit code $LASTEXITCODE)." }
    if (Get-ChildItem -Path $path -Force | Select-Object -First 1) {
        & icacls.exe (Join-Path $path '*') /reset /T /C /Q | Out-Null
        if ($LASTEXITCODE -ne 0) { Write-Warning "Resetting permissions inside $path returned $LASTEXITCODE." }
    }
}
Restrict $DataRoot @($svcAccount)
Restrict $OutputRoot @($svcAccount, $EmrAccount)
if (-not $EmrAccount) {
    Write-Warning "No -EmrAccount given: only Administrators and the service can open $OutputRoot. Re-run with -EmrAccount ""DOMAIN\account"" (the account of the program that reads the files)."
}

# ---------------------------------------------------------------- firewall
Step "Adding firewall rules (remote addresses: $AllowedRemoteAddress)"
Get-NetFirewallRule -Group 'PrintToImage' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
$remote = $AllowedRemoteAddress -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ }
New-NetFirewallRule -DisplayName "PrintToImage raw printing (TCP $rawPort)" -Group 'PrintToImage' -Direction Inbound -Action Allow `
    -Protocol TCP -LocalPort $rawPort -Program $exe -RemoteAddress $remote -Profile Any | Out-Null
if ($lpd) {
    New-NetFirewallRule -DisplayName "PrintToImage LPD printing (TCP $lpdPort)" -Group 'PrintToImage' -Direction Inbound -Action Allow `
        -Protocol TCP -LocalPort $lpdPort -Program $exe -RemoteAddress $remote -Profile Any | Out-Null
    $lpdSvc = Get-Service -Name LPDSVC -ErrorAction SilentlyContinue
    if ($lpdSvc -and $lpdSvc.Status -eq 'Running') {
        Write-Warning "The Windows 'LPD Print Service' is running and uses port 515. Stop and disable it, or keep EnableLpd=false."
    }
}

# ---------------------------------------------------------------- start
Step 'Starting the service'
Start-Service -Name $ServiceName
(Get-Service $ServiceName).WaitForStatus('Running', [TimeSpan]::FromSeconds(30))
Start-Sleep -Seconds 2
$listening = Get-NetTCPConnection -LocalPort $rawPort -State Listen -ErrorAction SilentlyContinue
if ($listening) { Write-Host "    Listening on TCP $rawPort." -ForegroundColor Green }
else { Write-Warning "The service is running but nothing listens on TCP $rawPort. Check $DataRoot\logs." }

# ---------------------------------------------------------------- shared printer
$sharedPath = $null
if (-not $NoSharedPrinter) {
    $printerName = if ($settings.SharedPrinterName) { $settings.SharedPrinterName } else { 'PrintToImage' }
    Step "Creating the shared printer \\$env:COMPUTERNAME\$printerName"
    $out = & (Join-Path $InstallDir 'shared-printer.ps1') -Name $printerName -RawPort $rawPort -PaperSize $PaperSize
    Write-Host "    $out"
    $sharedPath = "\\$env:COMPUTERNAME\$printerName"
}

# ---------------------------------------------------------------- shortcuts for the settings app
$settingsApp = Join-Path $InstallDir 'PrintToImageSettings.exe'
if (Test-Path $settingsApp) {
    Step 'Adding "PrintToImage Settings" to the Start menu and the desktop'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($folder in @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('CommonDesktopDirectory'))) {
        $lnk = $shell.CreateShortcut((Join-Path $folder 'PrintToImage Settings.lnk'))
        $lnk.TargetPath = $settingsApp
        $lnk.WorkingDirectory = $InstallDir
        $lnk.Description = 'Change PrintToImage settings, see recent print jobs, start/stop the service'
        $lnk.Save()
    }
}

Write-Host ''
Write-Host 'PrintToImage is installed.' -ForegroundColor Green
Write-Host "  Settings app : Start menu / desktop -> 'PrintToImage Settings'"
if ($sharedPath) { Write-Host "  Printer      : $sharedPath   <- connect the PCs to this printer" }
Write-Host "  Settings : $settingsPath   (restart the service after editing: Restart-Service PrintToImage)"
Write-Host "  Output   : $OutputRoot"
Write-Host "  Logs     : $DataRoot\logs"
Write-Host "  Test     : `"$InstallDir\SendTestPrint.exe`" --title `"Test 12345`""
