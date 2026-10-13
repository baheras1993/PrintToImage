<#
.SYNOPSIS
    Writes THIRD-PARTY-NOTICES.txt from the license files that ship inside the NuGet packages PrintToImage bundles
    (.NET runtime and libraries, SkiaSharp). Run by build.ps1; the result is also committed to the repository.
#>
param([Parameter(Mandatory)][string]$Target)

$ErrorActionPreference = 'Stop'
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

function Latest([string]$package) {
    $dir = Join-Path $nuget $package
    if (-not (Test-Path $dir)) { throw "Package $package not found in $nuget (build the solution first)." }
    Get-ChildItem $dir -Directory | Sort-Object { [version]($_.Name -replace '-.*$', '') } | Select-Object -Last 1
}
function Read([string]$package, [string]$file) {
    $d = Latest $package
    $p = Get-ChildItem $d.FullName -File | Where-Object Name -ieq $file | Select-Object -First 1
    if (-not $p) { throw "$file not found in $($d.FullName)" }
    return [IO.File]::ReadAllText($p.FullName).TrimEnd()
}

$netVersion = (Latest 'microsoft.netcore.app.runtime.win-x64').Name
$skiaVersion = (Latest 'skiasharp').Name
$line = '=' * 79

$text = @"
PrintToImage - third-party software notices
$line

PrintToImage itself is free for noncommercial use under the PolyForm Noncommercial License 1.0.0 (see LICENSE.txt);
commercial use needs a commercial license: https://github.com/baheras1993/PrintToImage/blob/main/COMMERCIAL.md

The PrintToImage programs include the following third-party software. All of it is free to use, also
commercially. Their licenses only require that these notices are passed on when the software is
distributed; this file does that.

  Component                                              License
  -----------------------------------------------------  --------------------------------------------
  .NET $netVersion runtime (built into the .exe files)          MIT (.NET Foundation and Contributors)
  Windows Desktop runtime (settings app)                 MIT (.NET Foundation and Contributors)
  Microsoft.Extensions.Hosting / Logging / WindowsServices,
  System.ServiceProcess.ServiceController                MIT (.NET Foundation and Contributors)
  SkiaSharp $skiaVersion and libSkiaSharp.dll (Skia graphics)  MIT (Microsoft, Xamarin) and BSD-3-Clause
                                                         (Google), with the components listed in its
                                                         notices below (FreeType, libpng, zlib,
                                                         libjpeg-turbo, HarfBuzz, expat, libwebp, ...)

Ghostscript is NOT used: PrintToImage has its own PostScript renderer.

Windows components used at run time (Print Spooler, the "MS Publisher Color Printer" driver, the
installed fonts) are part of Windows and covered by the Windows license of the computer.

The complete license texts and notices follow.


$line
.NET runtime and libraries (Microsoft.NETCore.App $netVersion, Microsoft.WindowsDesktop.App,
Microsoft.Extensions.*, System.ServiceProcess.ServiceController)
$line

$(Read 'microsoft.netcore.app.runtime.win-x64' 'LICENSE.TXT')

$(Read 'microsoft.netcore.app.runtime.win-x64' 'THIRD-PARTY-NOTICES.TXT')


$line
SkiaSharp $skiaVersion (including the native library libSkiaSharp.dll)
$line

$(Read 'skiasharp' 'LICENSE.txt')

$(Read 'skiasharp.nativeassets.win32' 'THIRD-PARTY-NOTICES.txt')
"@

$text = $text -replace "`r?`n", "`r`n"
[IO.File]::WriteAllText($Target, $text + "`r`n", (New-Object Text.UTF8Encoding($false)))
