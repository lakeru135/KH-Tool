<#
.SYNOPSIS
  Downloads the pinned official Node.js runtime that ships with the IFC-to-FRAG converter.

.DESCRIPTION
  Puts node.exe in tools\ifc2frag\node\ (git-ignored). KhBatchRunner.csproj copies it, with
  dist\, into the add-in's IfcToFrag\ folder. The zip is checked against the SHA-256 Node.js
  publishes, and the extracted exe must carry a valid OpenJS Foundation signature, because a
  freshly built or unsigned exe is exactly what locked-down PCs refuse to start.

  Change $Version only together with a re-test of the converter (see README.md here).
#>
param(
    [string]$Version = 'v24.22.1'
)

$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$target  = Join-Path $here 'node'
$work    = Join-Path $target 'download'
$zipName = "node-$Version-win-x64.zip"
$zip     = Join-Path $work $zipName

if (Test-Path (Join-Path $target 'node.exe')) {
    $current = & (Join-Path $target 'node.exe') --version
    if ($current -eq $Version) { Write-Host "node $Version already present."; return }
}

New-Item -ItemType Directory -Force $work | Out-Null
Write-Host "Downloading $zipName ..."
Invoke-WebRequest -Uri "https://nodejs.org/dist/$Version/$zipName" -OutFile $zip -UseBasicParsing
$sums = Invoke-WebRequest -Uri "https://nodejs.org/dist/$Version/SHASUMS256.txt" -UseBasicParsing

$line = ($sums.Content -split "`n") | Where-Object { $_ -match [regex]::Escape($zipName) } | Select-Object -First 1
if (-not $line) { throw "SHASUMS256.txt has no entry for $zipName" }
$expected = ($line.Trim() -split '\s+')[0].ToLower()
$actual   = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLower()
if ($expected -ne $actual) { throw "SHA-256 mismatch for $zipName (expected $expected, got $actual)" }

Expand-Archive $zip -DestinationPath $work -Force
$exe = Join-Path $work "node-$Version-win-x64\node.exe"
$sig = Get-AuthenticodeSignature $exe
if ($sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'OpenJS Foundation') {
    throw "node.exe signature is not a valid OpenJS Foundation signature ($($sig.Status))"
}

Copy-Item $exe (Join-Path $target 'node.exe') -Force
Copy-Item (Join-Path $work "node-$Version-win-x64\LICENSE") (Join-Path $target 'node.LICENSE.txt') -Force
Remove-Item $work -Recurse -Force
Write-Host "node $Version ready in $target"
