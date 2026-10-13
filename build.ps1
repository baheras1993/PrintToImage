<#
.SYNOPSIS
    Builds PrintToImage and creates the package to copy to the server: .\dist\PrintToImage

      PrintToImage\
        Install PrintToImage.bat          double-click to install or upgrade
        Open PrintToImage Settings.bat    double-click to open the settings app
        Uninstall PrintToImage.bat        double-click to remove
        README.txt                        the guide (generated from docs\MANUAL.md)
        files\                            programs and setup scripts used by the .bat files

    powershell -ExecutionPolicy Bypass -File .\build.ps1 [-SkipTests]
#>
[CmdletBinding()]
param([switch]$SkipTests)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist\PrintToImage'
$files = Join-Path $dist 'files'

$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
$localSdk = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet-sdk8\dotnet.exe'
if (Test-Path $localSdk) { $dotnet = $localSdk }
if (-not $dotnet) { throw 'The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0' }

if (-not $SkipTests) {
    Write-Host '==> Running tests' -ForegroundColor Cyan
    & $dotnet test (Join-Path $root 'PrintToImage.sln') -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}

# Retry: antivirus scanners briefly lock freshly built executables.
for ($i = 1; (Test-Path $dist) -and $i -le 5; $i++) {
    try { Remove-Item $dist -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Seconds 2 }
}
if (Test-Path $dist) { throw "Could not clear $dist (a file in it is in use)." }
New-Item -ItemType Directory -Force $files | Out-Null

Write-Host '==> Publishing PrintToImage (the service; self-contained, no .NET install needed on the server)' -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\PrintToImage\PrintToImage.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=false -p:DebugType=None -o $files
if ($LASTEXITCODE -ne 0) { throw 'Publishing PrintToImage failed.' }

Write-Host '==> Publishing PrintToImageSettings (settings app)' -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\PrintToImageSettings\PrintToImageSettings.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o $files
if ($LASTEXITCODE -ne 0) { throw 'Publishing PrintToImageSettings failed.' }

Write-Host '==> Publishing SendTestPrint (test tool)' -ForegroundColor Cyan
& $dotnet publish (Join-Path $root 'src\SendTestPrint\SendTestPrint.csproj') -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:PublishTrimmed=true -p:DebugType=None -o $files
if ($LASTEXITCODE -ne 0) { throw 'Publishing SendTestPrint failed.' }

Write-Host '==> Adding scripts and documentation' -ForegroundColor Cyan
$scripts = Join-Path $root 'scripts'
# What the user double-clicks goes at the top; everything the .bat files use goes into files\.
Copy-Item (Join-Path $scripts '*.bat') $dist
foreach ($f in 'install.ps1', 'uninstall.ps1', 'shared-printer.ps1', 'settings.sample.json', 'WHAT ARE THESE FILES.txt') {
    Copy-Item (Join-Path $scripts $f) $files
}
& (Join-Path $root 'tools\md2txt.ps1') -Source (Join-Path $root 'docs\MANUAL.md') -Target (Join-Path $dist 'README.txt')

# License of PrintToImage and the notices of the bundled open-source components (kept current in the repository too).
& (Join-Path $root 'tools\make-notices.ps1') -Target (Join-Path $root 'THIRD-PARTY-NOTICES.txt')
Copy-Item (Join-Path $root 'THIRD-PARTY-NOTICES.txt') $files
Copy-Item (Join-Path $root 'LICENSE') (Join-Path $files 'LICENSE.txt')

Write-Host ''
Write-Host "Done: $dist" -ForegroundColor Green
Get-ChildItem $dist -Recurse | Select-Object @{n = 'Path'; e = { $_.FullName.Substring($dist.Length + 1) } }, Length | Format-Table -AutoSize
