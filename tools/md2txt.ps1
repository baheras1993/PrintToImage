<#
.SYNOPSIS
    Converts docs\MANUAL.md to a plain-text README.txt that reads well in Notepad (used by build.ps1).
#>
param([Parameter(Mandatory)][string]$Source, [Parameter(Mandatory)][string]$Target)

$out = New-Object System.Collections.Generic.List[string]
$inCode = $false
$tableHeader = $null

function Clean([string]$s) {
    # Text inside `code` is kept as written; markdown escapes and emphasis are removed only outside it.
    $parts = $s -split '`'
    for ($i = 0; $i -lt $parts.Count; $i += 2) {
        $p = [regex]::Replace($parts[$i], '\[([^\]]+)\]\(([^)]+)\)', '$1 ($2)')   # links
        $p = $p -replace '\*\*', '' -replace '\\\\', '\' -replace '<br>', ' '
        $p = [regex]::Replace($p, '(?<![\w*])\*([^*\s][^*]*?)\*(?![\w*])', '$1')   # *italic*
        $parts[$i] = $p
    }
    return $parts -join ''
}

foreach ($line in [IO.File]::ReadAllLines($Source)) {
    if ($line -match '^\s*```') {
        $inCode = -not $inCode
        if (-not $inCode) { $out.Add('') }
        continue
    }
    if ($inCode) { $out.Add('    ' + $line); continue }

    # tables: header row, separator row, data rows
    if ($line -match '^\s*\|') {
        $cells = ($line.Trim().Trim('|') -split '\|') | ForEach-Object { (Clean $_).Trim() }
        if ($cells -join '' -match '^[-:\s]+$') { continue }        # separator row
        if ($null -eq $tableHeader) { $tableHeader = $cells; continue }
        $indent = ([regex]::Match($line, '^\s*')).Value
        if ($cells.Count -eq 2) { $out.Add("$indent  * $($cells[0]): $($cells[1])") }
        else {
            $parts = for ($i = 0; $i -lt $cells.Count; $i++) {
                if ($i -eq 0) { $cells[0] } elseif ($cells[$i]) { "$($tableHeader[$i]): $($cells[$i])" }
            }
            $out.Add("$indent  * " + ($parts -join ' -- '))
        }
        continue
    }
    $tableHeader = $null

    if ($line -match '^(#{1,4})\s+(.*)$') {
        $title = Clean $Matches[2]
        $level = $Matches[1].Length
        if ($out.Count -gt 0 -and $out[$out.Count - 1] -ne '') { $out.Add('') }
        $out.Add($title)
        if ($level -eq 1) { $out.Add('=' * $title.Length) }
        elseif ($level -eq 2) { $out.Add('-' * $title.Length) }
        continue
    }
    if ($line -match '^---\s*$') { $out.Add(''); continue }
    if ($line -match '^\s*>\s?(.*)$') { $out.Add('    ' + (Clean $Matches[1])); continue }
    $out.Add((Clean $line))
}

$text = ($out -join "`r`n") -replace "(`r`n){3,}", "`r`n`r`n"
[IO.File]::WriteAllText($Target, $text + "`r`n", (New-Object Text.UTF8Encoding($true)))
