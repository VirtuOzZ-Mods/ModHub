# Updates the tester's Mod Hub in place (pre-release testing on the developer's own PC).
# Builds the release layout without tests and without a zip, then replaces only the program itself:
# Program\, Mod Hub.exe and the README files. Settings.json, Data\ and user translations in Languages\ are kept,
# so the tester continues with the same settings, keys and history.
# Run the full tests separately (dotnet test GTAModManager.slnx) or build a release with publish-portable.ps1.
# Usage: .\scripts\update-test-build.ps1 [-Target "C:\Mod Hub by VirtuOzZ"]
param([string]$Target = 'C:\Mod Hub by VirtuOzZ')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$staging = Join-Path $root 'build\test-build'

# Mod Hub (or its gmm.exe) must not run from the target: its files would be locked half-way through.
$targetFull = [IO.Path]::GetFullPath($Target).TrimEnd('\') + '\'
$running = Get-Process -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and $_.Path.StartsWith($targetFull, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
}
if ($running) {
    throw "Закройте Mod Hub перед обновлением (запущено: $(($running | Select-Object -Expand ProcessName -Unique) -join ', '))."
}

& (Join-Path $PSScriptRoot 'publish-portable.ps1') -Output $staging -SkipTests -NoZip
$built = Join-Path $staging 'Mod Hub by VirtuOzZ'

New-Item -ItemType Directory -Force $Target | Out-Null
# Program\ is replaced as a whole: removed files must not linger from an older build.
$program = Join-Path $Target 'Program'
if (Test-Path $program) { Remove-Item $program -Recurse -Force }
Copy-Item (Join-Path $built 'Program') $program -Recurse
foreach ($file in 'Mod Hub.exe', 'README RU.txt', 'README EN.txt') {
    Copy-Item (Join-Path $built $file) (Join-Path $Target $file) -Force
}
$languages = New-Item -ItemType Directory -Force (Join-Path $Target 'Languages')
foreach ($file in 'README RU.txt', 'README EN.txt') {
    Copy-Item (Join-Path $built "Languages\$file") (Join-Path $languages $file) -Force
}
Remove-Item $staging -Recurse -Force
$label = (Get-Item (Join-Path $program 'ModHub.dll')).VersionInfo.ProductVersion
Write-Host "Тестовая установка обновлена: $Target (сборка $label)" -ForegroundColor Green
