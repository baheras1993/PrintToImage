<#
.SYNOPSIS
    Creates (or repairs, or removes) the shared printer on this server that the PCs print to.

.DESCRIPTION
    The printer "PrintToImage" uses the PostScript driver built into Windows and a port that sends every job to the
    PrintToImage service on this computer (127.0.0.1:9100). It is shared as \\<this server>\PrintToImage.
    The Print Spooler service is enabled and the "File and Printer Sharing" firewall rules are turned on for the
    domain and private networks (not public), which shared printers need.

    Used by install.ps1 and by the settings app; can also be run on its own (elevated):
        powershell -ExecutionPolicy Bypass -File .\shared-printer.ps1 [-Name PrintToImage] [-PaperSize A4]
        powershell -ExecutionPolicy Bypass -File .\shared-printer.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$Name = 'PrintToImage',
    [int]$RawPort = 9100,
    [ValidateSet('A4', 'Letter')][string]$PaperSize = 'A4',
    [string]$ServiceAccount = 'NT SERVICE\PrintToImage',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$portName = "PrintToImage_127.0.0.1_$RawPort"

if ($Remove) {
    if (Get-Service Spooler -ErrorAction SilentlyContinue | Where-Object Status -eq 'Running') {
        Get-Printer -Name $Name -ErrorAction SilentlyContinue | Remove-Printer
        Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue | Remove-PrinterPort
    }
    Write-Output 'Shared printer removed.'
    return
}

# Print Spooler (often disabled on servers for security; shared printing needs it).
$spooler = Get-Service Spooler
if ($spooler.StartType -eq 'Disabled') { Set-Service Spooler -StartupType Automatic }
if ($spooler.Status -ne 'Running') { Start-Service Spooler; (Get-Service Spooler).WaitForStatus('Running', [TimeSpan]::FromSeconds(30)) }

# Port: Standard TCP/IP, raw, to the PrintToImage service on this computer.
if (-not (Get-PrinterPort -Name $portName -ErrorAction SilentlyContinue)) {
    Add-PrinterPort -Name $portName -PrinterHostAddress '127.0.0.1' -PortNumber $RawPort
}

# Driver: a PostScript driver that is part of Windows.
$driver = $null
# Colour PostScript driver first: the class driver reports a black-and-white printer, so browsers and other
# programs would print in grey. 'MS Publisher Color Printer' is part of Windows (also on older Windows versions).
foreach ($candidate in 'MS Publisher Color Printer', 'Microsoft PS Class Driver', 'MS Publisher Imagesetter') {
    if (Get-PrinterDriver -Name $candidate -ErrorAction SilentlyContinue) { $driver = $candidate; break }
    try { Add-PrinterDriver -Name $candidate; $driver = $candidate; break } catch { }
}
if (-not $driver) { throw 'No built-in PostScript printer driver could be installed on this server.' }

$comment = 'Saves every print job as a PDF, JPG or PNG file (PrintToImage).'
$existing = Get-Printer -Name $Name -ErrorAction SilentlyContinue
if ($existing -and $existing.DriverName -ne $driver) {
    # Windows cannot switch a printer between driver types in place: recreate it (same name and share).
    Remove-Printer -Name $Name -ErrorAction Stop
    $existing = $null
}
if ($existing) {
    Set-Printer -Name $Name -PortName $portName -Shared $true -ShareName $Name -Comment $comment -ErrorAction Stop
} else {
    Add-Printer -Name $Name -DriverName $driver -PortName $portName -Shared -ShareName $Name -Comment $comment -Location $env:COMPUTERNAME -ErrorAction Stop
}
try { Set-PrintConfiguration -PrinterName $Name -PaperSize $PaperSize -Color $true } catch { Write-Warning "Paper size not set: $($_.Exception.Message)" }
# The service reads who sent each job and its document name from the queue. Windows hides the names of other users'
# documents unless the reader may "Manage documents", so grant exactly that to the PrintToImage service account.
try {
    $sid = (New-Object Security.Principal.NTAccount($ServiceAccount)).Translate([Security.Principal.SecurityIdentifier]).Value
    $sddl = (Get-Printer -Name $Name -Full).PermissionSDDL
    if ($sddl -notmatch [regex]::Escape(";;;$sid)")) {
        Set-Printer -Name $Name -PermissionSDDL ($sddl + "(A;;SWRC;;;$sid)(A;OIIO;RPWPSDRCWDWO;;;$sid)")
    }
} catch { Write-Warning "Could not give $ServiceAccount the 'Manage documents' permission: $($_.Exception.Message)" }

# "Start printing after last page is spooled": a job reaches PrintToImage only when it is complete and has its final name.
Start-Process -FilePath rundll32.exe -ArgumentList 'printui.dll,PrintUIEntry', '/Xs', '/n', $Name, 'attributes', '+queued' -Wait

# File and Printer Sharing (SMB) for domain and private networks.
Get-NetFirewallRule -Group '@FirewallAPI.dll,-28502' -ErrorAction SilentlyContinue |
    Where-Object { ($_.Profile.ToString() -notmatch '^Public$') } |
    Enable-NetFirewallRule

Write-Output "Shared printer ready: \\$env:COMPUTERNAME\$Name  (driver: $driver, port: 127.0.0.1:$RawPort)"
