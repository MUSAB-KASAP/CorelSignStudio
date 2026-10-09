<#
.SYNOPSIS
    Builds the releasable Windows application.

.DESCRIPTION
    Restores, builds and tests the solution in Release, then publishes a self-contained win-x64 copy of
    Corel AI Operatoru to artifacts\release\CorelAI-Operator. When Inno Setup 6 is installed it also
    builds the installer to artifacts\release\installer.

    Nothing from the user's machine goes into the package: no settings, API keys, logs or outputs.
    CorelDRAW 2026 is a prerequisite and is never bundled.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\publish.ps1
    powershell -ExecutionPolicy Bypass -File build\publish.ps1 -SkipTests
#>
param(
    [switch]$SkipTests,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'CorelSignStudio.sln'
$project = Join-Path $root 'CorelSignStudio.App\CorelSignStudio.App.csproj'
$release = Join-Path $root 'artifacts\release'
$output = Join-Path $release 'CorelAI-Operator'
$installerOutput = Join-Path $release 'installer'

if (-not (Test-Path $solution)) { throw "CorelSignStudio.sln was not found next to the build folder ($root)." }

function Invoke-Step([string]$name, [scriptblock]$action) {
    Write-Host "==> $name"
    & $action
    if ($LASTEXITCODE -ne 0) { throw "$name failed (exit code $LASTEXITCODE)." }
}

# The version is defined once, in Directory.Build.props.
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
if (-not $version) { throw 'No <Version> in Directory.Build.props.' }
Write-Host "Corel AI Operatoru $version"

Invoke-Step 'Restore' { dotnet restore $solution --nologo }
Invoke-Step 'Build (Release)' { dotnet build $solution -c Release --no-restore --nologo -v minimal }
if (-not $SkipTests) {
    Invoke-Step 'Unit tests' { dotnet test $solution -c Release --no-build --nologo -v minimal }
}

if (Test-Path $output) { Remove-Item $output -Recurse -Force }
Invoke-Step 'Publish (win-x64, self-contained)' {
    dotnet publish $project -c Release -r win-x64 --self-contained true -o $output --nologo -v minimal `
        -p:PublishSingleFile=false -p:DebugType=none -p:DebugSymbols=false
}

# Guard: a release never carries settings, keys, logs, outputs or test data.
$forbidden = Get-ChildItem $output -Recurse -File | Where-Object {
    $_.Name -match '^(ai-settings\.json|.*\.log)$' -or $_.FullName -match '\\(logs|output|data|history|TestResults)\\'
}
if ($forbidden) { throw "Unexpected user data in the publish folder: $($forbidden.FullName -join ', ')" }

$exe = Join-Path $output 'CorelSignStudio.App.exe'
if (-not (Test-Path $exe)) { throw "The published executable is missing: $exe" }
$size = [math]::Round(((Get-ChildItem $output -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Published: $output ($size MB)"

if (-not $SkipInstaller) {
    $iscc = @(
        (Get-Command ISCC.exe -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -First 1),
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1

    if ($iscc) {
        New-Item -ItemType Directory -Force $installerOutput | Out-Null
        Invoke-Step 'Installer (Inno Setup)' {
            & $iscc "/DAppVersion=$version" "/DSourceDir=$output" "/DOutputDir=$installerOutput" (Join-Path $PSScriptRoot 'installer.iss')
        }
        Write-Host "Installer: $(Join-Path $installerOutput "CorelAI-Operator-$version-Setup.exe")"
    }
    else {
        Write-Warning 'Inno Setup 6 (ISCC.exe) is not installed: the installer was NOT built. Install it from https://jrsoftware.org/isdl.php and run this script again.'
    }
}
