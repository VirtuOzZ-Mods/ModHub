# Self-contained Mod Hub by VirtuOzZ build for end users: .NET is bundled, no installation required.
# Layout (a single folder inside the archive):
#   Mod Hub by VirtuOzZ\
#     Mod Hub.exe      — launcher (native/ModHubLauncher)
#     README RU.txt, README EN.txt — user instructions (docs\user-readme.*.txt)
#     Languages\       — user translation packs; README RU/EN.txt (docs\languages-readme.*.txt)
#     Program\         — the app, gmm, the .NET runtime and libraries
#   Created on first run: Settings.json and Data\ (keys, cache, browser, downloads, temp files).
#   ModHub never writes outside this folder except next to the game (X:\ModHub on the game's drive) and,
#   only if the user turns it on, the .oiv file association in the registry.
# Output: build\ModHub-portable\ and build\ModHub-portable.zip
# -SkipTests -NoZip: quick test build (scripts\update-test-build.ps1); a release always runs the tests and makes the zip.
param(
    [string]$Output = (Join-Path (Split-Path -Parent $PSScriptRoot) 'build\ModHub-portable'),
    [switch]$SkipTests,
    [switch]$NoZip
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$product = 'Mod Hub by VirtuOzZ'
$folder = Join-Path $Output $product
$bin = Join-Path $folder 'Program'
if (Test-Path $Output) { Remove-Item $Output -Recurse -Force }

if (-not $SkipTests) {
    dotnet test (Join-Path $root 'GTAModManager.slnx') --nologo --verbosity minimal
    if ($LASTEXITCODE) { throw 'Тесты не прошли — сборка для пользователей не создаётся.' }
}
# Build label shown in Settings and crash reports: date, time, commit; "+" = built with uncommitted changes.
$commit = (git -C $root rev-parse --short HEAD).Trim()
$dirty = if (git -C $root status --porcelain) { '+' } else { '' }
$label = '{0:yyyy.MM.dd-HHmm} {1}{2}' -f (Get-Date), $commit, $dirty # ASCII only: passed on the command line
# No debug symbols and no build-machine paths in the release: the PDB path and source paths would otherwise
# carry the builder's folders (user name, drive) into every dll.
$pathMap = "$root\=/_/"
foreach ($proj in 'src\GTAModManager.Cli', 'src\GTAModManager.App') {
    dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -o $bin --nologo --verbosity minimal `
        -p:DebugType=none -p:DebugSymbols=false -p:Deterministic=true "-p:PathMap=$pathMap" `
        "-p:InformationalVersion=$label" -p:IncludeSourceRevisionInInformationalVersion=false
    if ($LASTEXITCODE) { throw "Публикация $proj не удалась." }
}
# Fail the build if anything from the build machine slipped through.
$leaks = Get-ChildItem $folder -Recurse -File | Where-Object {
    $text = [Text.Encoding]::Unicode.GetString([IO.File]::ReadAllBytes($_.FullName)) + [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($_.FullName))
    $text.Contains($root) -or $text.Contains($env:USERNAME + '\') -or $text.Contains($env:USERPROFILE)
}
if ($leaks) { throw ('В сборке остались пути с машины сборки: ' + (($leaks | Select-Object -Expand Name) -join ', ')) }

# Launcher: build a fresh one if Visual Studio's CMake is available, otherwise use the committed binary.
$launcherSrc = Join-Path $root 'native\ModHubLauncher'
$launcher = Join-Path $launcherSrc 'prebuilt\Mod Hub.exe'
$cmake = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
if ($cmake) {
    & $cmake.FullName -S $launcherSrc -B (Join-Path $launcherSrc 'build') -A x64 | Out-Null
    & $cmake.FullName --build (Join-Path $launcherSrc 'build') --config Release | Out-Null
    if ($LASTEXITCODE) { throw 'Сборка запускатора не удалась.' }
    Copy-Item (Join-Path $launcherSrc 'build\Release\Mod Hub.exe') $launcher -Force
}
if (-not (Test-Path $launcher)) { throw 'Нет запускатора Mod Hub.exe (native\ModHubLauncher).' }
Copy-Item $launcher (Join-Path $folder 'Mod Hub.exe')

# Readme files in Russian and English: in the root, and in Languages\ (Compress-Archive drops empty folders anyway).
function Copy-Readme([string]$source, [string]$target) {
    $text = [IO.File]::ReadAllText((Join-Path $root $source), [Text.Encoding]::UTF8).Replace("`r`n", "`n").Replace("`n", "`r`n")
    [IO.File]::WriteAllText($target, $text, [Text.UTF8Encoding]::new($true)) # BOM + CRLF: opens correctly in any Notepad
}
$languages = New-Item -ItemType Directory -Force (Join-Path $folder 'Languages')
foreach ($lang in 'RU', 'EN') {
    Copy-Readme "docs\user-readme.$($lang.ToLower()).txt" (Join-Path $folder "README $lang.txt")
    Copy-Readme "docs\languages-readme.$($lang.ToLower()).txt" (Join-Path $languages "README $lang.txt")
}
# Marker: the program folder (settings, Languages, Data) is one level above Program\.
Set-Content (Join-Path $bin 'ModHub.portable') @(
    'Mod Hub хранит все свои данные в папке программы (на уровень выше этой).',
    'root=..') -Encoding UTF8

if ($NoZip) {
    Write-Host "Готово: $folder (сборка $label)"
    return
}
$zip = "$Output.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path $folder -DestinationPath $zip -CompressionLevel Optimal
Write-Host ("Готово: {0} ({1:N0} МБ, сборка {2})" -f $zip, ((Get-Item $zip).Length / 1MB), $label)
