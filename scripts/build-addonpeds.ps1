param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'publish\ModHub-AddonPeds' }
$cmakeCommand = Get-Command cmake -ErrorAction SilentlyContinue
if ($cmakeCommand) { $cmakePath = $cmakeCommand.Source }
else {
    $vswherePath = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswherePath)) { throw 'CMake / Visual Studio C++ tools not found.' }
    $cmakePath = & $vswherePath -latest -products '*' -find 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe' | Select-Object -First 1
    if (!$cmakePath) { throw 'Install Visual Studio C++ and CMake tools.' }
}
$nativeSource = Join-Path $projectRoot 'native\GMMPedSelector'
$nativeBuild = Join-Path $nativeSource 'build'
& $cmakePath -S $nativeSource -B $nativeBuild -A x64
if ($LASTEXITCODE) { throw 'Native configuration failed.' }
& $cmakePath --build $nativeBuild --config Release
if ($LASTEXITCODE) { throw 'Native build failed.' }
& (Join-Path (Split-Path $cmakePath) 'ctest.exe') --test-dir $nativeBuild -C Release --output-on-failure
if ($LASTEXITCODE) { throw 'Native tests failed.' }
$componentDirectory = Join-Path $projectRoot 'src\GTAModManager.App\Components\Legacy'
New-Item -ItemType Directory -Path $componentDirectory -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $nativeBuild 'Release\GMMPedSelector.asi') -Destination (Join-Path $componentDirectory 'GMMPedSelector.asi') -Force
dotnet test (Join-Path $projectRoot 'GTAModManager.slnx') --nologo --verbosity minimal
if ($LASTEXITCODE) { throw 'Core tests failed.' }
dotnet publish (Join-Path $projectRoot 'src\GTAModManager.App\GTAModManager.App.csproj') -c Release -o $OutputDirectory --nologo --verbosity minimal
if ($LASTEXITCODE) { throw 'ModHub publish failed.' }
