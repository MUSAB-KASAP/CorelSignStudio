<#
.SYNOPSIS
    Release acceptance for Corel AI Operatoru: one command, one report.

.DESCRIPTION
    clean -> restore -> Release build -> unit tests -> real CorelDRAW tests -> real AI tests (only when the
    application already has a credential) -> publish -> published self-tests -> ZIP -> installer (when
    Inno Setup 6 is installed) -> install / launch / uninstall -> checksums -> summary.

    The report is written to artifacts\acceptance\v<version>\. Every line of acceptance-summary.txt says
    PASS, FAIL or SKIPPED; a step that did not run is never reported as passed.

    No secret is passed on a command line or written to a report. The AI tests read the credential the
    same way the application does (its encrypted settings, or the provider's environment variable).

    The CorelDRAW steps need CorelDRAW 2026. Have it open and idle on screen: with the trial version, an
    instance started hidden by automation can be blocked by trial dialogs.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build\acceptance.ps1
    powershell -ExecutionPolicy Bypass -File build\acceptance.ps1 -SkipCorel -SkipInstallTest
#>
param(
    [switch]$SkipCorel,
    [switch]$SkipInstallTest,
    [int]$HangTimeoutMinutes = 4
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $root 'CorelSignStudio.sln'
$tests = Join-Path $root 'CorelSignStudio.Tests\CorelSignStudio.Tests.csproj'
[xml]$props = Get-Content (Join-Path $root 'Directory.Build.props')
$version = ($props.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$release = Join-Path $root 'artifacts\release'
$published = Join-Path $release 'CorelAI-Operator'
$exe = Join-Path $published 'CorelSignStudio.App.exe'
$zip = Join-Path $release "CorelAI-Operator-$version-win-x64.zip"
$setup = Join-Path $release "installer\CorelAI-Operator-$version-Setup.exe"
$out = Join-Path $root "artifacts\acceptance\v$version"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Force $out | Out-Null

$results = [ordered]@{}
function Set-Result([string]$name, [string]$status, [string]$detail = '') {
    $results[$name] = if ($detail) { "$status ($detail)" } else { $status }
    Write-Host ("{0,-34} {1}" -f $name, $results[$name])
}

function Invoke-Native([string]$log, [scriptblock]$action) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { & $action *> (Join-Path $out $log) } finally { $ErrorActionPreference = $previous }
    return $LASTEXITCODE
}

function Get-CorelIds { @(Get-Process CorelDRW -ErrorAction SilentlyContinue | ForEach-Object Id) }
$corelBefore = Get-CorelIds
function Stop-StrayCorel {
    Get-Process CorelDRW -ErrorAction SilentlyContinue | Where-Object { $corelBefore -notcontains $_.Id } | Stop-Process -Force -ErrorAction SilentlyContinue
}

# Hang dumps and per-run folders are large and carry machine names; only the .trx files are kept.
function Remove-RunFolders { Get-ChildItem $out -Directory -ErrorAction SilentlyContinue | Remove-Item -Recurse -Force -ErrorAction SilentlyContinue }

# ---- TRX helpers -------------------------------------------------------------------------------
function Read-Trx([string]$file) {
    $path = Join-Path $out $file
    if (-not (Test-Path $path)) { return @() }
    [xml]$trx = Get-Content $path
    return @($trx.TestRun.Results.UnitTestResult | ForEach-Object { [pscustomobject]@{ Name = $_.testName; Outcome = $_.outcome } })
}

function Get-Counts($rows) {
    [pscustomobject]@{
        Total   = @($rows).Count
        Passed  = @($rows | Where-Object Outcome -eq 'Passed').Count
        Failed  = @($rows | Where-Object Outcome -eq 'Failed').Count
        Skipped = @($rows | Where-Object Outcome -eq 'NotExecuted').Count
    }
}

# PASS only when at least one matching test ran and none failed; SKIPPED when none ran.
function Get-Outcome($rows, [string]$pattern) {
    $matching = @($rows | Where-Object { $_.Name -match $pattern })
    if (@($matching | Where-Object Outcome -eq 'Failed').Count -gt 0) { return 'FAIL' }
    if (@($matching | Where-Object Outcome -eq 'Passed').Count -gt 0) { return 'PASS' }
    if ($matching.Count -eq 0) { return 'FAIL' } # the test this line depends on no longer exists
    return 'SKIPPED'
}

# ---- Source ------------------------------------------------------------------------------------
Push-Location $root
try {
    $head = (git rev-parse HEAD).Trim()
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    $baseMaster = (git rev-parse origin/master 2>$null)
    $dirty = [bool](git status --porcelain)
}
finally { Pop-Location }

# ---- Clean, restore, build ---------------------------------------------------------------------
Get-ChildItem $root -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -notmatch '\\artifacts\\' -and $_.FullName -notmatch '\\\.git\\' } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

Set-Result 'Restore' $(if ((Invoke-Native 'restore.log' { dotnet restore $solution --nologo }) -eq 0) { 'PASS' } else { 'FAIL' })
$buildExit = Invoke-Native 'build.log' { dotnet build $solution -c Release --no-restore --nologo -v minimal }
$warnings = @(Select-String -Path (Join-Path $out 'build.log') -Pattern ': warning ' -SimpleMatch).Count
Set-Result 'Release build' $(if ($buildExit -eq 0) { 'PASS' } else { 'FAIL' }) "$warnings warning(s)"
if ($buildExit -ne 0) { throw 'The Release build failed; see build.log.' }

# ---- Unit tests (no CorelDRAW, no network) -----------------------------------------------------
$env:COREL_INTEGRATION = $null
$unitExit = Invoke-Native 'unit-tests.log' { dotnet test $tests -c Release --no-build --nologo --logger 'trx;LogFileName=unit-tests.trx' --results-directory $out }
$unit = Read-Trx 'unit-tests.trx'
$unitCounts = Get-Counts $unit
Set-Result 'Unit tests' $(if ($unitExit -eq 0 -and $unitCounts.Failed -eq 0 -and $unitCounts.Passed -gt 0) { 'PASS' } else { 'FAIL' }) "$($unitCounts.Passed) passed, $($unitCounts.Failed) failed, $($unitCounts.Skipped) skipped (opt-in CorelDRAW and AI tests)"

# ---- Real CorelDRAW ----------------------------------------------------------------------------
$corel = @()
$corelInstalled = Test-Path 'Registry::HKEY_CLASSES_ROOT\CorelDRAW.Application.27'
if ($SkipCorel -or -not $corelInstalled) {
    Set-Result 'Corel integration' 'SKIPPED' $(if ($SkipCorel) { 'switched off' } else { 'CorelDRAW 2026 is not installed' })
}
else {
    $env:COREL_INTEGRATION = '1'
    # One class per run: each run attaches to CorelDRAW on its own, so a blocked run cannot take the others with it.
    foreach ($class in 'CorelOperatorIntegrationTests', 'CorelReleaseAcceptanceTests', 'CorelDrawIntegrationTests') {
        Invoke-Native "corel-$class.log" {
            dotnet test $tests -c Release --no-build --nologo --filter "FullyQualifiedName~$class" `
                --blame-hang-timeout "${HangTimeoutMinutes}m" --logger "trx;LogFileName=corel-$class.trx" --results-directory $out
        } | Out-Null
        $rows = Read-Trx "corel-$class.trx"
        if (@($rows).Count -eq 0) { $rows = @([pscustomobject]@{ Name = "$class (run did not finish: blocked or crashed)"; Outcome = 'Failed' }) }
        $corel += $rows
        Stop-StrayCorel
        Remove-RunFolders
    }

    $env:COREL_INTEGRATION = $null
    $corelCounts = Get-Counts $corel
    Set-Result 'Corel integration' $(if ($corelCounts.Failed -eq 0 -and $corelCounts.Passed -gt 0) { 'PASS' } else { 'FAIL' }) "$($corelCounts.Passed) passed, $($corelCounts.Failed) failed, $($corelCounts.Skipped) skipped"
}

# ---- Real AI (only with a credential the application itself can read) --------------------------
# The connection test needs no CorelDRAW; when it is skipped there is no credential and nothing else is started.
$env:COREL_INTEGRATION = $null
Invoke-Native 'real-ai.log' {
    dotnet test $tests -c Release --no-build --nologo --filter 'FullyQualifiedName~RealAiAcceptanceTests' `
        --logger 'trx;LogFileName=real-ai.trx' --results-directory $out
} | Out-Null
$ai = Read-Trx 'real-ai.trx'
$credential = @($ai | Where-Object { $_.Name -match 'provider_answers' -and $_.Outcome -ne 'NotExecuted' }).Count -gt 0
if ($credential -and -not $SkipCorel -and $corelInstalled) {
    $env:COREL_INTEGRATION = '1'
    Invoke-Native 'real-ai.log' {
        dotnet test $tests -c Release --no-build --nologo --filter 'FullyQualifiedName~RealAiAcceptanceTests' `
            --blame-hang-timeout '10m' --logger 'trx;LogFileName=real-ai.trx' --results-directory $out
    } | Out-Null
    $env:COREL_INTEGRATION = $null
    $ai = Read-Trx 'real-ai.trx'
    Stop-StrayCorel
    Remove-RunFolders
}

$aiLines = @(
    "Real AI connection:            $(Get-Outcome $ai 'provider_answers')"
    "Real AI planner:               $(Get-Outcome $ai 'turkish_instruction')"
    "Real AI clarification:         $(Get-Outcome $ai 'ambiguous_instruction')"
    "Real AI vision:                $(Get-Outcome $ai 'synthetic_sign')"
    "Real visual comparison:        $(Get-Outcome $ai 'synthetic_sign')"
    "Real AI automatic improvement: $(Get-Outcome $ai 'synthetic_sign')"
)
$aiLines = @("Credential available: $(if ($credential) { 'YES' } else { 'NO' })") + $aiLines
if (-not $credential) { $aiLines += 'Reason: SKIPPED - no configured AI credential. AI network acceptance remains unverified.' }
$aiLines | Set-Content (Join-Path $out 'real-ai-results.txt') -Encoding utf8
$aiSummary = Join-Path $root 'artifacts\acceptance\work\real-ai-vision\real-ai-summary.txt'
if (Test-Path $aiSummary) { Copy-Item $aiSummary (Join-Path $out 'real-ai-vision-summary.txt') }

# ---- Publish and the published copy -------------------------------------------------------------
$publishExit = Invoke-Native 'publish.log' { powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'publish.ps1') }
$publishOk = $publishExit -eq 0 -and (Test-Path $exe)
$sizeMb = if ($publishOk) { [math]::Round(((Get-ChildItem $published -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1) } else { 0 }
Set-Result 'win-x64 publish' $(if ($publishOk) { 'PASS' } else { 'FAIL' }) "$sizeMb MB"

function Invoke-SelfTest([string]$executable, [string]$report, [switch]$Corel) {
    if (-not (Test-Path $executable)) { return 'FAIL' }
    Remove-Item $report -ErrorAction SilentlyContinue
    $arguments = @('--selftest', "`"$report`"")
    if ($Corel) { $arguments += '--corel' }
    $process = Start-Process -FilePath $executable -ArgumentList $arguments -PassThru
    if (-not $process.WaitForExit(240000)) { $process.Kill(); return 'FAIL' }
    if ((Test-Path $report) -and (Select-String -Path $report -Pattern '^RESULT: PASS$' -Quiet)) { return 'PASS' }
    return 'FAIL'
}

Set-Result 'Published EXE self-test' (Invoke-SelfTest $exe (Join-Path $out 'published-selftest.txt'))
if ($SkipCorel -or -not $corelInstalled) { Set-Result 'Published EXE Corel self-test' 'SKIPPED' }
else { Set-Result 'Published EXE Corel self-test' (Invoke-SelfTest $exe (Join-Path $out 'published-corel-selftest.txt') -Corel) }

# ---- Portable ZIP -------------------------------------------------------------------------------
Remove-Item $zip -ErrorAction SilentlyContinue
if ($publishOk) {
    Compress-Archive -Path (Join-Path $published '*') -DestinationPath $zip -CompressionLevel Optimal
    $extracted = Join-Path $env:TEMP ("CorelAI-Operator-zip-" + [guid]::NewGuid().ToString('N'))
    Expand-Archive $zip $extracted
    $zipStatus = Invoke-SelfTest (Join-Path $extracted 'CorelSignStudio.App.exe') (Join-Path $out 'zip-selftest.txt')
    Remove-Item $extracted -Recurse -Force -ErrorAction SilentlyContinue
    Set-Result 'ZIP' $zipStatus "$([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB, extracted copy self-tested"
}
else { Set-Result 'ZIP' 'FAIL' }

# ---- Installer ----------------------------------------------------------------------------------
if (Test-Path $setup) { Set-Result 'Installer build' 'PASS' "$([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB" }
else { Set-Result 'Installer build' 'SKIPPED' 'Inno Setup 6 (ISCC.exe) is not installed; the script exists, no setup EXE was built' }

$installLines = 'Install', 'Start Menu shortcut', 'Installed EXE launch', 'Uninstall', 'User data preserved'
if (-not (Test-Path $setup)) { $installLines | ForEach-Object { Set-Result $_ 'SKIPPED' 'no setup EXE' } }
elseif ($SkipInstallTest) { $installLines | ForEach-Object { Set-Result $_ 'SKIPPED' 'switched off' } }
else {
    # A real per-user install on this machine, then a real uninstall.
    $installDir = Join-Path $env:LOCALAPPDATA 'Programs\CorelAI-Operator'
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'Corel AI Operatörü\Corel AI Operatörü.lnk'
    $userData = Join-Path $env:LOCALAPPDATA 'CorelSignStudio'
    $marker = Join-Path $userData 'acceptance-marker.txt'
    New-Item -ItemType Directory -Force $userData | Out-Null
    Set-Content $marker 'kept across uninstall'

    $install = Start-Process $setup -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', "/LOG=`"$(Join-Path $out 'install.log')`"" -PassThru -Wait
    $installedExe = Join-Path $installDir 'CorelSignStudio.App.exe'
    Set-Result 'Install' $(if ($install.ExitCode -eq 0 -and (Test-Path $installedExe)) { 'PASS' } else { 'FAIL' }) $installDir
    Set-Result 'Start Menu shortcut' $(if (Test-Path $shortcut) { 'PASS' } else { 'FAIL' })
    Set-Result 'Installed EXE launch' (Invoke-SelfTest $installedExe (Join-Path $out 'installed-selftest.txt'))

    $uninstaller = Get-ChildItem $installDir -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($uninstaller) {
        Start-Process $uninstaller.FullName -ArgumentList '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART' -Wait
        Start-Sleep -Seconds 3
        Set-Result 'Uninstall' $(if (-not (Test-Path $installedExe) -and -not (Test-Path $shortcut)) { 'PASS' } else { 'FAIL' })
    }
    else { Set-Result 'Uninstall' 'FAIL' 'no uninstaller was installed' }

    Set-Result 'User data preserved' $(if (Test-Path $marker) { 'PASS' } else { 'FAIL' })
    Remove-Item $marker -ErrorAction SilentlyContinue
}

# ---- Checksums ----------------------------------------------------------------------------------
$sums = Join-Path $release 'SHA256SUMS.txt'
$hashed = @($zip, $setup) | Where-Object { Test-Path $_ }
@($hashed | ForEach-Object { "{0}  {1}" -f (Get-FileHash $_ -Algorithm SHA256).Hash.ToLowerInvariant(), (Split-Path $_ -Leaf) }) | Set-Content $sums -Encoding ascii
if (Test-Path $sums) { Copy-Item $sums (Join-Path $out 'SHA256SUMS.txt') }
Set-Result 'SHA256SUMS' $(if ($hashed.Count -gt 0) { 'PASS' } else { 'FAIL' }) "$($hashed.Count) file(s)"

# ---- Summary ------------------------------------------------------------------------------------
$all = @($unit) + @($corel)
function Line([string]$label, [string]$status) { "- {0,-34} {1}" -f ($label + ':'), $status }
$summary = @(
    "COREL AI OPERATORU $version - RELEASE ACCEPTANCE"
    "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm')"
    ''
    'SOURCE'
    "- master base SHA:    $baseMaster"
    "- branch:             $branch"
    "- branch SHA:         $head$(if ($dirty) { ' (with uncommitted changes)' })"
    "- version:            $version"
    ''
    'BUILD'
    (Line 'restore' $results['Restore'])
    (Line 'Release build' $results['Release build'])
    ''
    'TESTS'
    (Line 'unit' $results['Unit tests'])
    (Line 'Corel integration' $results['Corel integration'])
    (Line 'reconstruction' (Get-Outcome $corel 'rebuilt_as_editable|workflow_runs_end_to_end'))
    (Line 'structural comparison' (Get-Outcome $corel 'Measured_differences'))
    (Line 'automatic improvement' (Get-Outcome $corel 'Measured_differences|workflow_runs_end_to_end'))
    (Line 'preflight' (Get-Outcome $corel 'Production_check_cases'))
    (Line 'bitmap DPI' (Get-Outcome $corel 'Bitmap_resolution'))
    (Line 'Arabic' (Get-Outcome $corel 'Arabic_text_survives'))
    (Line 'PDF (vector import)' (Get-Outcome $corel 'single_page_pdf'))
    (Line 'PDF (multi-page selection)' (Get-Outcome $unit 'Three_pages_are_detected|real_renderer_returns'))
    (Line 'XLSX' (Get-Outcome $corel 'Excel_and_csv_batches'))
    (Line 'CSV' (Get-Outcome $corel 'Excel_and_csv_batches|Recipe_batch_produces'))
    (Line 'legacy sign template' (Get-Outcome $corel 'CorelDraw27_connects|CorelDrawIntegrationTests'))
    (Line 'AI credential available' $(if ($credential) { 'YES' } else { 'NO' }))
    (Line 'real AI connection' (Get-Outcome $ai 'provider_answers'))
    (Line 'real AI planner' (Get-Outcome $ai 'turkish_instruction'))
    (Line 'real AI clarification' (Get-Outcome $ai 'ambiguous_instruction'))
    (Line 'real AI vision' (Get-Outcome $ai 'synthetic_sign'))
    (Line 'real visual comparison' (Get-Outcome $ai 'synthetic_sign'))
    ''
    'PUBLISH'
    (Line 'win-x64 publish' $results['win-x64 publish'])
    (Line 'published EXE selftest' $results['Published EXE self-test'])
    (Line 'published EXE Corel selftest' $results['Published EXE Corel self-test'])
    ''
    'INSTALLER'
    (Line 'build' $results['Installer build'])
    (Line 'install' $results['Install'])
    (Line 'Start Menu shortcut' $results['Start Menu shortcut'])
    (Line 'launch' $results['Installed EXE launch'])
    (Line 'uninstall' $results['Uninstall'])
    (Line 'user data preserved' $results['User data preserved'])
    ''
    'ARTIFACTS'
    (Line 'ZIP' $results['ZIP'])
    (Line 'setup EXE' $results['Installer build'])
    (Line 'SHA256SUMS' $results['SHA256SUMS'])
)
$failed = @($all + @($ai) | Where-Object Outcome -eq 'Failed')
if ($failed.Count -gt 0) { $summary += ''; $summary += 'FAILED TESTS'; $summary += ($failed | ForEach-Object { "- $($_.Name)" }) }
Stop-StrayCorel
Remove-RunFolders
$summary | Set-Content (Join-Path $out 'acceptance-summary.txt') -Encoding utf8
Write-Host ''
Write-Host "Acceptance report: $out"
if (@($summary | Where-Object { $_ -match ':\s+FAIL' }).Count -gt 0) { exit 1 }
