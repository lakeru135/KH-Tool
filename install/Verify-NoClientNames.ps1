<#
.SYNOPSIS
  Fails the build if any source, script or doc names a real client, project, site or hub.

.DESCRIPTION
  This repository is public. Client and model names must never land in it - not in code, test
  fixtures, comments, docs or sample data. Use neutral names instead (SITE1, BLDGA, Test Model).

  The list below holds names that have leaked before. Add to it; never remove from it. A hit prints
  file:line and the build stops.

  Constrained-Language safe: Select-String and plain cmdlets only.
#>
param()

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$self = $MyInvocation.MyCommand.Path

# Case-insensitive: names that are wrong in any spelling.
$anyCase = @(
    'gdc', 'google', 'aurecon', 'solar farm', 'smallbimpro', 'small bim pro', 'vdcref'
)
# Case-sensitive: project numbers, site and model codes, ids seen in real data.
$exactCase = @(
    '\bNKET\b', '529210', '56499', '\bCHB\d', '\bCRN\d', '\bRGDC', '\bRG-DC\b',
    '\bEYD\b', '\bMYD\b', '\bDCH\b', '\bTLA\b', '\bFSA\b', '\bT1410\b',
    'a1d138cf', 'pwUVAl2N', '799dbd08', 'a7d61ea9'
)

# tools\ifc2frag\node and \dist are git-ignored downloads/builds of third-party code (node.exe's
# own licence text names its upstream authors); like bin and node_modules they never reach the repo.
$skipDirs = '\\(bin|obj|\.git|\.vs|Output|node_modules)\\|\\tools\\ifc2frag\\(node|dist)\\'
$binary = @('.rfa', '.rvt', '.png', '.ico', '.dll', '.exe', '.pdb', '.zip', '.jpg', '.gif', '.xlsx')

$files = Get-ChildItem $root -Recurse -File |
    Where-Object { $_.FullName -notmatch $skipDirs -and
                   $binary -notcontains $_.Extension.ToLowerInvariant() -and
                   $_.FullName -ne $self }

$hits = @()
foreach ($p in $anyCase) {
    $hits += $files | Select-String -Pattern $p -SimpleMatch:$false
}
foreach ($p in $exactCase) {
    $hits += $files | Select-String -Pattern $p -CaseSensitive
}

if ($hits.Count -gt 0) {
    $hits | Sort-Object Path, LineNumber -Unique |
        ForEach-Object { Write-Host ("{0}:{1}: {2}" -f $_.Path.Substring($root.Length + 1), $_.LineNumber, $_.Line.Trim()) }
    throw "Verify-NoClientNames: $($hits.Count) client/model name hit(s). Replace them with neutral names."
}
Write-Host "Verify-NoClientNames: OK ($($files.Count) files, no client or model names)."
