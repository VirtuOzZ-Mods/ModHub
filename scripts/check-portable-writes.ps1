# Checks that the portable build writes only into its own folder.
# Copies the built folder into a fresh sandbox, runs gmm.exe and starts ModHub.exe for a while, then looks for
# anything new named like ModHub in %LOCALAPPDATA%, %APPDATA%, %TEMP%, Documents and the HKCU registry.
# MODHUB_NO_MIGRATION=1 keeps the sandbox copy from moving the user's real data from older versions.
# Usage: .\scripts\check-portable-writes.ps1 [-Build <folder with Mod Hub.exe>] [-Seconds 15]
param(
    [string]$Build = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build\ModHub-portable\Mod Hub by VirtuOzZ'),
    [int]$Seconds = 15
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Build 'Program\ModHub.exe'))) { throw "No portable build in '$Build'. Run scripts\publish-portable.ps1 first." }

$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('modhub-write-check-' + [guid]::NewGuid().ToString('N').Substring(0, 8))
Copy-Item $Build $sandbox -Recurse
$pattern = 'modhub|gtamodmanager|^gmm'
$watched = @(
    $env:LOCALAPPDATA,
    $env:APPDATA,
    [IO.Path]::GetTempPath(),
    [Environment]::GetFolderPath('MyDocuments')
) | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { (Resolve-Path $_).Path.TrimEnd('\') } | Sort-Object -Unique

function Snapshot {
    $items = foreach ($dir in $watched) {
        Get-ChildItem -LiteralPath $dir -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match $pattern -and $_.FullName -notlike "$sandbox*" } |
            ForEach-Object { '{0}|{1:o}' -f $_.FullName, $_.LastWriteTimeUtc }
    }
    $keys = Get-ChildItem 'HKCU:\Software\Classes' -ErrorAction SilentlyContinue |
        Where-Object { $_.PSChildName -match 'GTAModManager|ModHub' } | ForEach-Object { 'HKCU|' + $_.PSChildName }
    @($items) + @($keys)
}

$before = Snapshot
$env:MODHUB_NO_MIGRATION = '1'
try {
    $gmm = Join-Path $sandbox 'Program\gmm.exe'
    & $gmm | Out-Null
    & $gmm backup-info | Out-Null
    $app = Start-Process (Join-Path $sandbox 'Program\ModHub.exe') -PassThru
    Start-Sleep -Seconds $Seconds
    if (-not $app.HasExited) {
        $null = $app.CloseMainWindow()
        if (-not $app.WaitForExit(10000)) { $app.Kill() }
    }
} finally {
    Remove-Item Env:\MODHUB_NO_MIGRATION -ErrorAction SilentlyContinue
}
$after = Snapshot
$changed = @(Compare-Object @($before) @($after) -PassThru | Where-Object { $_ -in $after })

Write-Host 'Written inside the program folder:'
Get-ChildItem $sandbox -Force | Where-Object { $_.Name -notin 'Program', 'Mod Hub.exe', 'README RU.txt', 'README EN.txt' } |
    ForEach-Object { Write-Host "  $($_.Name)" }
Remove-Item $sandbox -Recurse -Force -ErrorAction SilentlyContinue
if ($changed.Count) {
    Write-Host 'Written OUTSIDE the program folder:' -ForegroundColor Red
    $changed | ForEach-Object { Write-Host "  $($_.Split('|')[0])" -ForegroundColor Red }
    exit 1
}
Write-Host 'OK: nothing was written outside the program folder.' -ForegroundColor Green
