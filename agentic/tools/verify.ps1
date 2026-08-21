<#
.SYNOPSIS
    Pre-commit gate: builds the plugin and runs the comment linter.
.DESCRIPTION
    Fails on any build warning, not just errors. Run from anywhere.
#>
[CmdletBinding()]
param(
    [switch]$SkipBuild,
    [switch]$SkipLint,
    [switch]$SkipPayload,
    [switch]$SkipHarness,
    [switch]$ReleaseMode
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $root 'Jellyfin.Plugin.AutoSubSync.csproj'
$linter = Join-Path $PSScriptRoot 'check-comments.mjs'

$failures = New-Object System.Collections.Generic.List[string]

if (-not $SkipBuild) {
    Write-Host "`n=== Build ===" -ForegroundColor Cyan

    $buildOutput = & dotnet build $project --nologo -v minimal
    $buildExit = $LASTEXITCODE
    $buildOutput | Write-Host

    $warnings = @($buildOutput | Select-String -Pattern ': warning [A-Z]+\d+')

    if ($buildExit -ne 0) {
        $failures.Add("Build failed (exit $buildExit)")
    }
    elseif ($warnings.Count -gt 0) {
        $failures.Add("Build produced $($warnings.Count) warning(s)")
    }
    else {
        Write-Host "Build clean: 0 warnings, 0 errors" -ForegroundColor Green
    }
}

if (-not $SkipLint) {
    Write-Host "`n=== Comment lint ===" -ForegroundColor Cyan

    # The linter exempts agentic/ itself. Those comments are working notes, not shipped code.
    & node $linter $root
    if ($LASTEXITCODE -ne 0) {
        $failures.Add('Comment lint failed')
    }
}

if (-not $SkipHarness) {
    Write-Host "`n=== Harnesses ===" -ForegroundColor Cyan

    # Only the self-contained ones. synccheck, formatcheck and killcheck need media or a payload
    # and are run by hand. These link the real sources, so a build failure here means a harness
    # stopped compiling against the code it is supposed to be guarding.
    $harnesses = @(
        'acquirecheck', 'configcheck', 'dedupecheck', 'gatecheck', 'langcheck', 'measurecheck',
        'namingcheck', 'ocrcheck', 'orchestratorcheck', 'payloadcheck', 'placecheck',
        'rollbackcheck', 'stalecheck', 'storecheck', 'subcheck', 'verifycheck', 'vobsubcheck'
    )

    foreach ($harness in $harnesses) {
        $output = & dotnet run --project (Join-Path $PSScriptRoot $harness) --nologo -v quiet
        if ($LASTEXITCODE -ne 0) {
            $output | Write-Host
            $failures.Add("$harness failed")
        }
        else {
            Write-Host "  $(@($output)[-1])" -ForegroundColor Green
        }
    }

    # Node harnesses. simulate-concurrency reads the control-law constants out of
    # AdaptiveConcurrency.cs and fails if they no longer match its own model.
    $nodeHarnesses = @(
        'simulate-concurrency.mjs'
    )

    foreach ($harness in $nodeHarnesses) {
        $output = & node (Join-Path $PSScriptRoot $harness)
        if ($LASTEXITCODE -ne 0) {
            $output | Write-Host
            $failures.Add("$harness failed")
        }
        else {
            Write-Host "  $(@($output)[-1])" -ForegroundColor Green
        }
    }
}

if (-not $SkipPayload) {
    Write-Host "`n=== Vendored tool payloads ===" -ForegroundColor Cyan

    $payloadScript = Join-Path $PSScriptRoot 'check-payload.ps1'
    if ($ReleaseMode) {
        & $payloadScript -ReleaseMode
    }
    else {
        & $payloadScript
    }

    if ($LASTEXITCODE -ne 0) {
        $failures.Add('Payload check failed')
    }
}

Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host '=== FAILED ===' -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}

Write-Host '=== All checks passed ===' -ForegroundColor Green
exit 0
