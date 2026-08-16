<#
.SYNOPSIS
Runner-asserted verification for strict `puck.world.def.v1` deserialization
("world/player document deserialization is strict everywhere").

.DESCRIPTION
`WorldJsonContext` (src/Puck.World/WorldDefinitionSerialization.cs) now sets
`UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow` context-wide, so
an unmapped member on ANY row in the `puck.world.def.v1` graph — not just the
handful of row types (WorldMotionDefaults, WorldChannel) that previously opted
in one at a time via their own `[JsonUnmappedMemberHandling]` attribute — is a
hard parse failure naming the member and the .NET row type, instead of being
silently dropped. `WorldAddonRow` is the row the gap was named against (it
carried no attribute and no extension-data bag); this runner's misspelled-
member case targets it directly rather than a row that was already covered.

The one carve-out is the document ROOT: `WorldDefinition.Extensions` carries
`[JsonExtensionData]`, which System.Text.Json always prefers over the ambient
Disallow default, so an unmapped TOP-LEVEL member still round-trips into that
bag and is judged by `Puck.Abstractions.Documents.DocumentExtensionsPolicy`
instead (a reserved '$'/'_' prefix passes; any other key is a validator
rejection, unchanged by this closure). This runner proves that carve-out still
holds under the new default: it boots a document with a reserved-prefix root
key, drives `world.save` over stdin, and asserts the saved file still carries
that key.

This runner also proves the closure did not regress anything already shipped:
every `*.world.json` document under `src/Puck.World/Assets/worlds` and
`src/Puck.World/Assets/scenarios`, plus the one wasm-crate battery world that
boots through its own recipe, still boots clean under the strict default.

Boots `Puck.World` once per world file (each boot is a fresh process — a
malformed `--world` document ends the boot with a non-zero exit before a
window ever opens, so these cannot share one process the way the sdf-decode
runner's in-process command stream does) and asserts required/forbidden
regexes against the captured stdout+stderr transcript, plus each process's own
exit code. `world.save`'s round-trip case additionally re-reads the file it
wrote.

.EXAMPLE
pwsh -File docs/verification/strict-definition-parse/run.ps1
#>

$ErrorActionPreference = 'Stop'

# The engine emits em-dashes; under an OEM console codepage the captured transcript mangles them and
# the assertions below false-FAIL. Pin the whole pipe to UTF-8 (matching docs/verification/sdf-decode-sign-refusal).
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding = [System.Text.Encoding]::UTF8

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$fixtures = Join-Path $PSScriptRoot 'fixtures'
$misspelledFixture = Join-Path $fixtures 'misspelled-addon-member.world.json'
$rootExtensionFixture = Join-Path $fixtures 'root-extension-roundtrip.world.json'
$controlWorld = Join-Path $repoRoot 'src\Puck.World\Assets\worlds\play.world.json'

foreach ($required in @($misspelledFixture, $rootExtensionFixture, $controlWorld)) {
    if (-not (Test-Path $required)) {
        Write-Error "fixture missing: $required"
        exit 1
    }
}

# Scratch is UNIQUE PER RUN. Concurrent agent sessions run these batteries on one machine, and a fixed
# scratch name plus a blind Remove-Item collides with a sibling run — measured both as a startup failure
# against a file another process still holds open, and as the quieter corruption of deleting a live run's
# artifacts out from under it. Prior runs' directories are swept best-effort only once they are old enough
# that no live run can still own them; a locked or fresh sibling survives untouched, and a sweep failure is
# never this run's failure.
$scratchPrefix = 'puck-strict-definition-parse'
$scratchDir = Join-Path $env:TEMP ('{0}-{1:yyyyMMdd-HHmmss}-{2}' -f $scratchPrefix, (Get-Date), $PID)

Get-ChildItem -Path $env:TEMP -Directory -Filter ($scratchPrefix + '*') -ErrorAction SilentlyContinue |
    Where-Object { $_.CreationTimeUtc -lt [DateTime]::UtcNow.AddHours(-6) } |
    ForEach-Object { try { Remove-Item -Recurse -Force -Path $_.FullName -ErrorAction Stop } catch { } }

New-Item -ItemType Directory -Force -Path $scratchDir | Out-Null

$buildPath = Join-Path $scratchDir 'build.log'

Push-Location $repoRoot
try {
    & dotnet build src/Puck.World/Puck.World.csproj -c Release --no-restore *> $buildPath
    $buildExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

if ($buildExitCode -ne 0) {
    Get-Content $buildPath
    Write-Error "Puck.World Release build failed with exit code $buildExitCode"
    exit 1
}

# ---- Every shipped world/scenario document, plus the wasm-crate battery world that boots through its own recipe
# (wasm/puck-addon-channelwalk/README.md). Each must boot through its exact path — the loud
# "[world] definition: <path> (--world)" line, never a fallback — and each process must exit 0.
$shippedWorlds = @(
    @{ Id = 'play'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\play.world.json') },
    @{ Id = 'dive'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\dive.world.json') },
    @{ Id = 'kart'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\kart.world.json') },
    @{ Id = 'jump'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\jump.world.json') },
    @{ Id = 'studio'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\studio.world.json') },
    # quilt-base is the composition template the five quilt deltas name as `basis`; it is itself a complete,
    # bootable document, so it boots here like any other. The deltas exercise the basis-composition load path.
    @{ Id = 'quilt-base'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-base.world.json') },
    @{ Id = 'quilt-ne'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-ne.world.json') },
    @{ Id = 'quilt-nw'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-nw.world.json') },
    @{ Id = 'quilt-se'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-se.world.json') },
    @{ Id = 'quilt-sw'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-sw.world.json') },
    @{ Id = 'quilt-island'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\worlds\quilt-island.world.json') },
    @{ Id = 'scenario-combat'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\scenarios\combat.world.json') },
    @{ Id = 'scenario-reconnect'; Path = (Join-Path $repoRoot 'src\Puck.World\Assets\scenarios\reconnect.world.json') },
    @{ Id = 'wasm-channelwalk'; Path = (Join-Path $repoRoot 'wasm\puck-addon-channelwalk\worlds\channel-walk-world.json') }
)

$failures = @()
$assertionCount = 0

function Test-Assertion {
    param(
        [string]$Name,
        [bool]$Matched,
        [bool]$Require
    )

    $script:assertionCount++

    if ($Require -and -not $Matched) {
        $script:failures += "MISSING (required): $Name"
    } elseif ((-not $Require) -and $Matched) {
        $script:failures += "PRESENT (forbidden): $Name"
    }
}

Write-Output "---- $($shippedWorlds.Count) shipped/battery worlds must boot clean under strict deserialization ----"

foreach ($world in $shippedWorlds) {
    $outPath = Join-Path $scratchDir "boot-$($world.Id).log"
    $stateDir = Join-Path $scratchDir "state-$($world.Id)"

    Push-Location $repoRoot
    try {
        & dotnet run --no-build --no-restore --project src/Puck.World -c Release -- --headless --world $world.Path --exit-after-seconds 3 --state-dir $stateDir *> $outPath
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    $transcript = Get-Content -Raw $outPath

    Test-Assertion -Name "$($world.Id): boots via --world (loud origin line, not the baked-default fallback)" `
        -Matched ([regex]::IsMatch($transcript, [regex]::Escape("[world] definition: $($world.Path) (--world)"))) -Require $true
    Test-Assertion -Name "$($world.Id): FORBIDDEN — never falls back to the baked default" `
        -Matched ([regex]::IsMatch($transcript, 'baked default')) -Require $false
    Test-Assertion -Name "$($world.Id): dotnet run exited 0" -Matched ($exitCode -eq 0) -Require $true
}

# ---- Case: one misspelled member on a WorldAddonRow — the exact row the gap was named against — is
# REFUSED, naming both the member and the .NET row type. The control is the class of evidence just
# above: every shipped world (which carries the identical strict-parse posture on every row, including
# any WorldAddonRow it declares) already asserted booting clean.
Write-Output "---- misspelled-member refusal (WorldAddonRow) + control ----"

$misspelledOutPath = Join-Path $scratchDir 'boot-misspelled.log'
$misspelledStateDir = Join-Path $scratchDir 'state-misspelled'

Push-Location $repoRoot
try {
    & dotnet run --no-build --no-restore --project src/Puck.World -c Release -- --headless --world $misspelledFixture --exit-after-seconds 3 --state-dir $misspelledStateDir *> $misspelledOutPath
    $misspelledExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

$misspelledTranscript = Get-Content -Raw $misspelledOutPath

Test-Assertion -Name 'misspelled fixture: refused, naming the member (bogusField) and the row type (WorldAddonRow)' `
    -Matched ([regex]::IsMatch($misspelledTranscript, [regex]::Escape("The JSON property 'bogusField' could not be mapped to any .NET member contained in type 'Puck.World.WorldAddonRow'."))) -Require $true
Test-Assertion -Name 'misspelled fixture: boot never reaches the window (FORBIDDEN: "Application started")' `
    -Matched ([regex]::IsMatch($misspelledTranscript, 'Application started')) -Require $false
Test-Assertion -Name 'misspelled fixture: dotnet run exited non-zero (a typo must fail the boot, not run it)' `
    -Matched ($misspelledExitCode -ne 0) -Require $true

# ---- Case: a root-level reserved-prefix key ('$note') is the intentional DocumentExtensionsPolicy
# escape hatch, unaffected by the context-wide Disallow default (WorldDefinition's own
# [JsonExtensionData] carve-out). Boots the fixture, drives world.save over stdin, and re-reads the
# saved file to confirm the key survived the round-trip.
Write-Output "---- root-Extensions round-trip (reserved-prefix key survives world.save) ----"

$savedPath = Join-Path $scratchDir 'saved-root-extension.world.json'
$stdinPath = Join-Path $scratchDir 'roundtrip-stdin.txt'
$roundtripOutPath = Join-Path $scratchDir 'boot-roundtrip.log'
$roundtripStateDir = Join-Path $scratchDir 'state-roundtrip'

# replay.status leads (a harmless Immediate read-back) per the documented stdin-driving trap: a
# leading world.wait silently swallows every line behind it. Not used here, but the same posture.
Set-Content -Path $stdinPath -Value @"
replay.status
world.save $savedPath
"@ -NoNewline:$false

Push-Location $repoRoot
try {
    Get-Content $stdinPath | & dotnet run --no-build --no-restore --project src/Puck.World -c Release -- --headless --world $rootExtensionFixture --exit-after-seconds 8 --state-dir $roundtripStateDir *> $roundtripOutPath
    $roundtripExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

$roundtripTranscript = Get-Content -Raw $roundtripOutPath

Test-Assertion -Name 'root-extension fixture: boots (reserved-prefix root key does not fail validation)' `
    -Matched ([regex]::IsMatch($roundtripTranscript, [regex]::Escape("[world] definition: $rootExtensionFixture (--world)"))) -Require $true
Test-Assertion -Name 'root-extension fixture: world.save wrote the snapshot' `
    -Matched ([regex]::IsMatch($roundtripTranscript, [regex]::Escape("[world.save: $savedPath ("))) -Require $true
Test-Assertion -Name 'root-extension fixture: dotnet run exited 0' -Matched ($roundtripExitCode -eq 0) -Require $true

$savedContent = if (Test-Path $savedPath) { Get-Content -Raw $savedPath } else { '' }

Test-Assertion -Name 'root-Extensions round-trip: the saved file still carries "$note" (DocumentExtensionsPolicy escape hatch survives strict mode)' `
    -Matched ([regex]::IsMatch($savedContent, [regex]::Escape('"$note": "root-extension-roundtrip-probe"'))) -Require $true

# ---- FORBIDDEN across every transcript captured above: a rejection must never escape as a raw,
# unhandled stack trace — matching docs/verification/sdf-decode-sign-refusal's same posture.
foreach ($log in (Get-ChildItem -Path $scratchDir -Filter 'boot-*.log')) {
    $content = Get-Content -Raw $log.FullName
    Test-Assertion -Name "$($log.Name): FORBIDDEN — no unhandled exception" -Matched ([regex]::IsMatch($content, 'Unhandled exception')) -Require $false
}

Write-Output "---- scratch dir: $scratchDir ----"
Write-Output "---- assertions: $assertionCount, failures: $($failures.Count) ----"

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }

    exit 1
}

Write-Output "PASS: strict puck.world.def.v1 deserialization verified (all $assertionCount assertions held)."
exit 0
