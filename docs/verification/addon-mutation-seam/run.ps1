<#
.SYNOPSIS
Runner-asserted verification of the Phase-3 plan's L6 landing — the addon
mutation seam (verb masks, the timing contract, the six-stage dispatch
door, boot-anchored replay arming).

.DESCRIPTION
Two phases, both driven over stdin against a Release build, both against
the SAME code (this landing carries no sabotage patch yet):

  1. GRANT-DOOR OUTCOME MATRIX (console-issued grants only, the shipped
     default world): the verb-mask legality rules, the metered-budget
     requirement, world.grants'/world.why's echo, and the null-mask-clears
     rule, plus replay arming SUCCEEDING on this boot — the shipped worlds
     mount no addons, so nothing has ever pumped and the boot anchor is open.

  2. GUEST END TO END (wasm/puck-addon-hudbuilder/worlds/hudbuilder-world.json):
     a REAL compiled WASM guest asks a Mutate/section:hud handle, submits
     UpsertHudPanel, and — only after reading back Applied — submits a
     chained UpsertHudElement, then goes quiet. world.hud is read back and
     asserted against the final document shape, then replay arming is REFUSED
     against the same boot's pumped guest and the tape is left idle.

Together the two phases give boot-anchored arming both directions: open where
no addon has ever pumped, closed once one has.

Each phase's dotnet-run exit code is checked independently; a process that
printed the right lines and then crashed or exited nonzero still fails this
runner. --state-dir isolates each phase's persisted state so a run never
reads another run's leftovers.

.EXAMPLE
pwsh -File docs/verification/addon-mutation-seam/run.ps1
#>

$ErrorActionPreference = 'Stop'

# The engine emits em-dashes; under an OEM console codepage the captured transcript mangles them and the
# assertions below false-FAIL. Pin the whole pipe to UTF-8 — but WITHOUT a BOM: a BOM'd pin writes its
# preamble into the piped stdin and silently corrupts the FIRST command (docs/verification's own established
# gotcha — see undo-all-or-nothing/run.ps1's identical comment).
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$worldProject = Join-Path $repoRoot 'src\Puck.World\Puck.World.csproj'
$hudbuilderWorld = Join-Path $repoRoot 'wasm\puck-addon-hudbuilder\worlds\hudbuilder-world.json'

# Scratch is UNIQUE PER RUN. Concurrent agent sessions run these batteries on one machine, and a fixed
# scratch name plus a blind Remove-Item collides with a sibling run — measured both as a startup failure
# against a file another process still holds open, and as the quieter corruption of deleting a live run's
# artifacts out from under it. Prior runs' directories are swept best-effort only once they are old enough
# that no live run can still own them; a locked or fresh sibling survives untouched, and a sweep failure is
# never this run's failure.
$scratchPrefix = 'puck-addon-mutation-seam'
$scratchDir = Join-Path $env:TEMP ('{0}-{1:yyyyMMdd-HHmmss}-{2}' -f $scratchPrefix, (Get-Date), $PID)

Get-ChildItem -Path $env:TEMP -Directory -Filter ($scratchPrefix + '*') -ErrorAction SilentlyContinue |
    Where-Object { $_.CreationTimeUtc -lt [DateTime]::UtcNow.AddHours(-6) } |
    ForEach-Object { try { Remove-Item -Recurse -Force -Path $_.FullName -ErrorAction Stop } catch { } }

New-Item -ItemType Directory -Force -Path $scratchDir | Out-Null

$failures = @()

# Needles are matched as ORDINAL LITERALS, never as wildcard patterns: PowerShell's -like reads `[` and `]`
# as a character class, so a bracketed needle such as '[replay.status: idle]' would match any transcript at
# all and the assertion would assert nothing. Engine echoes are bracketed, so this constraint is load-bearing.
function Assert-Contains {
    param([string]$Text, [string]$Needle, [string]$Label)

    if (-not $Text.Contains($Needle, [System.StringComparison]::Ordinal)) {
        $script:failures += "MISS ($Label): expected to find `"$Needle`""
        Write-Host "  [FAIL] $Label — expected: $Needle" -ForegroundColor Red
    } else {
        Write-Host "  [ pass ] $Label" -ForegroundColor Green
    }
}

function Assert-NotContains {
    param([string]$Text, [string]$Needle, [string]$Label)

    if ($Text.Contains($Needle, [System.StringComparison]::Ordinal)) {
        $script:failures += "UNEXPECTED ($Label): found `"$Needle`""
        Write-Host "  [FAIL] $Label — unexpectedly present: $Needle" -ForegroundColor Red
    } else {
        Write-Host "  [ pass ] $Label" -ForegroundColor Green
    }
}

Write-Host "== Building src/Puck.World (Release) ==" -ForegroundColor Cyan
& dotnet build $worldProject -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error "build failed (exit $LASTEXITCODE)"
    exit 1
}

# ---------------------------------------------------------------------------
# PHASE 1 — the grant-door outcome matrix + the never-pumped arming direction.
# The shipped worlds mount no addons, so nothing has ever pumped on this boot
# and replay.record MUST arm. That is the control the pumped direction needs:
# without it, phase 2's refusal would not discriminate between the boot anchor
# working and replay.record simply never arming at all.
# ---------------------------------------------------------------------------
Write-Host "`n== Phase 1: grant-door outcome matrix + arming on a never-pumped boot ==" -ForegroundColor Cyan

$phase1Script = @'
world.wait 5
world.grant addon:hudtest mutate section:hud verbs:UpsertHudPanel,RemoveHudPanel budget:4
world.grants addon:hudtest
world.why addon:hudtest mutate section:hud verbs:UpsertHudPanel,RemoveHudElement
world.revoke addon:hudtest mutate section:hud
world.grants addon:hudtest
world.grant addon:hudtest mutate section:hud verbs:UpsertHudPanel budget:4
world.grant addon:hudtest mutate section:hud budget:4
world.grants addon:hudtest
world.grant addon:bad mutate section:hud verbs:UpsertKit budget:2
world.grant seat1 mutate section:hud verbs:UpsertHudPanel
world.grants seat1
world.grant seat1 mutate section:hud
world.grants seat1
world.grant addon:budgetless mutate section:hud verbs:UpsertHudPanel
replay.record seamphase1
replay.status
'@

$phase1StateDir = Join-Path $scratchDir 'phase1-state'
$phase1Out = Join-Path $scratchDir 'phase1.out.txt'

$phase1Script | & dotnet run --project $worldProject -c Release --no-build -- --state-dir $phase1StateDir --exit-after-seconds 6 2>&1 |
    Tee-Object -FilePath $phase1Out | Out-Null
$phase1Exit = $LASTEXITCODE

$phase1Text = Get-Content -Raw $phase1Out

Write-Host "dotnet run exit: $phase1Exit"

if ($phase1Exit -ne 0) {
    $failures += "phase 1 process exited $phase1Exit"
}

Assert-Contains $phase1Text '[world.grant: addon:hudtest mutate section:hud]' 'concrete verb-mask grant accepted'
Assert-Contains $phase1Text 'verbs:UpsertHudPanel,RemoveHudPanel' 'world.grants echoes the mask by NAME (never a hex lane)'
Assert-Contains $phase1Text 'UpsertHudPanel:admitted, RemoveHudElement:denied-by-mask' 'world.why reports per-kind mask coverage'
Assert-Contains $phase1Text '[world.revoke: addon:hudtest mutate section:hud]' 'revoke clears the row'
Assert-Contains $phase1Text 'names a mutation kind outside section:hud' 'a mask bit outside the section kind-set is refused (the badkind case)'
Assert-Contains $phase1Text 'an untrusted mutate grant to addon:budgetless requires an explicit budget' 'Mutate joined the metered positive list — untrusted grant with no budget refused'
Assert-Contains $phase1Text 'an untrusted mutate grant to addon:hudtest over section:hud requires an explicit verbs:' 'the NARROWING: a maskless untrusted mutate section row is refused at the grant door'
Assert-Contains $phase1Text "[replay.record: recording 'seamphase1'" 'replay.record arms on a boot where no addon has ever pumped'
Assert-Contains $phase1Text "[replay.status: recording 'seamphase1'" 'the tape is genuinely armed'
Assert-NotContains $phase1Text 'refused to arm' 'nothing refuses the arm on a never-pumped boot'

# The null-mask-clears-on-regrant rule: re-granting the SAME row with no verbs: token drops the
# mask entirely (world.grants for that principal shows no verbs: segment at all on the mutate row).
#
# Proved on a TRUSTED principal (seat1), because the maskless shape is now legal only there: an
# untrusted mutate section:<name> row REQUIRES verbs: at the grant door (the deliberate narrowing
# asserted just above), so the addon:hudtest bare re-grant on line 102 of the script is refused
# rather than clearing anything — which is itself why that line's own assertion is the refusal,
# not a cleared mask.
$regrantSection = ($phase1Text -split "`n" | Select-String -Pattern 'world\.grants: seat1' | Select-Object -Last 1)

if ($null -eq $regrantSection) {
    $failures += 'MISS (null-mask regrant): no final world.grants seat1 echo found'
    Write-Host "  [FAIL] null-mask regrant clears the mask — no echo found" -ForegroundColor Red
} elseif ($regrantSection.ToString() -like '*verbs:*') {
    $failures += "UNEXPECTED (null-mask regrant): mask still present after a bare re-grant: $regrantSection"
    Write-Host "  [FAIL] null-mask regrant clears the mask — mask still echoed" -ForegroundColor Red
} else {
    Write-Host "  [ pass ] a bare re-grant (no verbs:) clears a previously-recorded mask" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# PHASE 2 — a REAL compiled WASM guest driving the seam end to end.
# ---------------------------------------------------------------------------
Write-Host "`n== Phase 2: puck-addon-hudbuilder end to end (a real WASM guest) ==" -ForegroundColor Cyan

if (-not (Test-Path $hudbuilderWorld)) {
    $failures += "hudbuilder world document missing: $hudbuilderWorld"
    Write-Host "  [FAIL] $hudbuilderWorld not found — build wasm/puck-addon-hudbuilder first (cargo build --release, from wasm/)" -ForegroundColor Red
} else {
    $phase2Script = @'
world.wait 5
world.grant addon:hudbuilder mutate section:hud verbs:UpsertHudPanel,UpsertHudElement budget:4
world.wait 15
world.hud
world.addons
replay.record seamphase2
replay.status
'@

    $phase2StateDir = Join-Path $scratchDir 'phase2-state'
    $phase2Out = Join-Path $scratchDir 'phase2.out.txt'

    $phase2Script | & dotnet run --project $worldProject -c Release --no-build -- --world $hudbuilderWorld --state-dir $phase2StateDir --exit-after-seconds 8 2>&1 |
        Tee-Object -FilePath $phase2Out | Out-Null
    $phase2Exit = $LASTEXITCODE

    $phase2Text = Get-Content -Raw $phase2Out

    Write-Host "dotnet run exit: $phase2Exit"

    if ($phase2Exit -ne 0) {
        $failures += "phase 2 process exited $phase2Exit"
    }

    Assert-Contains $phase2Text 'mounted hudbuilder' 'the guest mounts'
    Assert-Contains $phase2Text "world.mutation: UpsertHudPanel 'hudbuilder' applied" 'the first act (UpsertHudPanel) applied'
    Assert-Contains $phase2Text "world.mutation: UpsertHudElement 'hudbuilder'.'line2' applied" 'the CHAINED second act (UpsertHudElement) applied only after reading back Applied'
    Assert-Contains $phase2Text 'world.hud.panel ''hudbuilder'' layer=over style=panel' 'world.hud reads back the panel the guest built'
    Assert-Contains $phase2Text "elements=2/24" 'both elements landed on the same panel'
    Assert-Contains $phase2Text "'hudbuilder'.'line1'" 'the first element is present'
    Assert-Contains $phase2Text "'hudbuilder'.'line2'" 'the second (chained) element is present'
    Assert-NotContains $phase2Text 'FAULTED' 'the guest never faults across the whole run'

    # The pumped direction of boot-anchored arming. TryBeginRecording checks the addon condition FIRST, so
    # this world naming the addon refusal is the point: it authors screens too, and the screen refusal would
    # be the wrong evidence that a guest's admitted execution is what closed the door.
    Assert-Contains $phase2Text 'refused to arm — an addon has already had an admitted execution attempted' 'replay.record refuses to arm once the guest has pumped (boot-anchored arming)'
    Assert-Contains $phase2Text '[replay.status: idle]' 'the refused arm left the tape idle'
    Assert-NotContains $phase2Text '[replay.record: recording' 'arming never succeeds after a guest has pumped'
}

# ---------------------------------------------------------------------------
Write-Host ''
if ($failures.Count -gt 0) {
    Write-Host "FAILED — $($failures.Count) assertion(s) missed:" -ForegroundColor Red
    foreach ($failure in $failures) {
        Write-Host "  - $failure" -ForegroundColor Red
    }
    Write-Host "`nTranscripts: $scratchDir" -ForegroundColor Yellow
    exit 1
}

Write-Host "PASSED — all assertions held." -ForegroundColor Green
exit 0
