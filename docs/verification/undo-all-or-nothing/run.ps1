<#
.SYNOPSIS
Runner-asserted verification that world.undo's journal replay is
ALL-OR-NOTHING ("world.undo replayed a failing journal entry into a
half-built install").

.DESCRIPTION
WorldServer.ApplyUndo (src/Puck.World.Server/WorldServer.cs) restores the
world's base document and replays the kept prefix of the mutation journal
through the same per-entry gates a live mutation passes (compose,
whole-document validate, render-envelope capacity, solid-field
buildability). The bug this closes: on a FAILING entry, the old code logged
the failure, broke out of the replay loop, and fell straight through into
the unconditional solid rebuild + Install below it — installing the
successfully-replayed PREFIX and returning true, contradicting its own
comment that promised no half-built state on failure.

This runner drives THREE phases of the SAME dotnet-run-over-stdin pattern
docs/verification/sdf-decode-sign-refusal/run.ps1 established, rebuilding
between phases:

  1. CONTROL — current (fixed) code, no sabotage. Apply world.row.set kits
     three times (moveSpeed 6, 7, 8 on the default world's "promenader"
     kit — UpsertKit is population-affecting only, so its only gates are
     compose+validate: a clean, minimal discriminating case), read
     world.status (dirty 3), world.undo 1 — expected to SUCCEED (drops the
     moveSpeed-8 entry, replays moveSpeed-6 then moveSpeed-7), read
     world.status again (dirty 2, DIFFERENT from the first reading).

  2. SABOTAGED — scripts/sabotage/undo-entry2-revalidation-failure.patch is
     git-applied, the project rebuilt, and the IDENTICAL stdin script from
     phase 1 is replayed. The patch touches ONLY ApplyUndo's replay loop
     (forces its second processed entry, loop index 1, to fail with a
     labeled sabotage reason) — the three live world.row.set kits applies
     are untouched by the patch and still succeed normally, so the journal
     genuinely holds 3 valid entries when world.undo 1 is issued. Expected:
     world.undo REFUSES, naming journal entry 1 and the sabotage reason,
     and the two world.status readings (immediately before and immediately
     after the refused undo) are BYTE-IDENTICAL — dirty 3, undoable 3, both
     times — proving nothing installed and the journal untouched. Under the
     pre-fix code this phase would instead have printed a SUCCESS line and
     left dirty at 1 (the moveSpeed-6-only prefix), silently.

  3. REVERTED — the sabotage patch is git-applied -R, the project rebuilt
     again, and phase 1's script and assertions are re-run verbatim: green,
     proving the round-trip left the tree (and the built behavior) exactly
     as phase 1 found it.

The patch is reverted in a try/finally so a mid-script failure never leaves
the working tree sabotaged. Each phase's dotnet-run exit code is checked
independently; a process that printed the right lines and then crashed or
exited nonzero still fails this runner.

.EXAMPLE
pwsh -File docs/verification/undo-all-or-nothing/run.ps1
#>

$ErrorActionPreference = 'Stop'

# The engine emits em-dashes; under an OEM console codepage the captured transcript mangles them and the
# assertions below false-FAIL. Pin the whole pipe to UTF-8 (same fix as sdf-decode-sign-refusal/run.ps1) — but
# WITHOUT a BOM: [System.Text.Encoding]::UTF8's preamble gets written once at the start of the redirected pipe
# to the native dotnet process, landing a BOM glyph in front of the FIRST piped stdin line and making the wire
# parser reject it outright ("Unrecognized command or argument '<BOM>world.row.set'"), silently dropping the
# first mutation of the run — a false green (or, worse, a miscounted journal) rather than a loud failure.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$patchPath = Join-Path $repoRoot 'scripts\sabotage\undo-entry2-revalidation-failure.patch'
$worldProject = Join-Path $repoRoot 'src\Puck.World\Puck.World.csproj'

if (-not (Test-Path $patchPath)) {
    Write-Error "sabotage patch missing: $patchPath"
    exit 1
}

# The files the sabotage patch touches, read out of the patch itself so this follows the patch rather than a
# hardcoded name. Their content is hashed HERE, before anything is applied, and again at the end: the question
# this runner has to answer is whether the REVERT restored them, which is not the same question as whether the
# tree is pristine. Someone editing one of these files for their own reasons while running this is not sabotage
# residue, and must not read as it.
$patchedPaths = @(Select-String -Path $patchPath -Pattern '^\+\+\+ b/(.+)$' | ForEach-Object { $_.Matches[0].Groups[1].Value })

if ($patchedPaths.Count -eq 0) {
    Write-Error "could not read any patched path out of $patchPath — the residue check would assert nothing"
    exit 1
}

$hashesBefore = @{}
foreach ($rel in $patchedPaths) {
    $full = Join-Path $repoRoot $rel

    if (-not (Test-Path $full)) {
        Write-Error "the sabotage patch names '$rel', which does not exist under $repoRoot"
        exit 1
    }

    $hashesBefore[$rel] = (Get-FileHash -Path $full -Algorithm SHA256).Hash
}

# Scratch is UNIQUE PER RUN. Concurrent agent sessions run these batteries on one machine, and a fixed
# scratch name plus a blind Remove-Item collides with a sibling run — measured both as a startup failure
# against a file another process still holds open, and as the quieter corruption of deleting a live run's
# artifacts out from under it. Prior runs' directories are swept best-effort only once they are old enough
# that no live run can still own them; a locked or fresh sibling survives untouched, and a sweep failure is
# never this run's failure.
$scratchPrefix = 'puck-undo-all-or-nothing'
$scratchDir = Join-Path $env:TEMP ('{0}-{1:yyyyMMdd-HHmmss}-{2}' -f $scratchPrefix, (Get-Date), $PID)

Get-ChildItem -Path $env:TEMP -Directory -Filter ($scratchPrefix + '*') -ErrorAction SilentlyContinue |
    Where-Object { $_.CreationTimeUtc -lt [DateTime]::UtcNow.AddHours(-6) } |
    ForEach-Object { try { Remove-Item -Recurse -Force -Path $_.FullName -ErrorAction Stop } catch { } }

New-Item -ItemType Directory -Force -Path $scratchDir | Out-Null

# The SAME stdin script drives all three phases — only the built binary differs (sabotaged or not) between
# phase 2 and its neighbors. Three world.row.set kits calls before the undo, each a whole-row UpsertKit on the
# default world's "promenader" kit, differing ONLY in moveSpeed (6, 7, 8 in order — all three move it off its
# authored 5, so every apply genuinely changes the document rather than journaling a no-op).
#
# The row below is the document's OWN promenader row, harvested from a world.save of the shipped default world:
# a saved row round-trips through world.row.set by design, so this is the payload the engine itself writes
# rather than a hand-authored guess that could drift from the real schema. Only moveSpeed is substituted.
$promenaderRow = '{"name":"promenader","bodyMotionProgram":"grounded","motion":{"$type":"grounded","moveSpeed":__MS__,"turnSpeed":2.5,"riseGravity":16,"fallGravity":27,"maxFallSpeed":20,"response":[{"gate":{"$type":"now","fact":"Rising"},"engageRate":10,"releaseRate":3},{"gate":{"$type":"now","fact":"Falling"},"engageRate":10,"releaseRate":3},{"engageRate":40,"releaseRate":48}],"sprintMultiplier":1.3,"sprintChannel":"run","moveFrame":"Heading","facingSnap":false,"declaredSprintChannel":"run","declaredMoveFrame":"Heading"},"producers":{"wander":{"scalars":{"forward":0.375,"softRadius":28,"weaveAmplitude":0.5,"inwardGain":1.6,"turnScale":2.5,"weaveFrequencyBase":0.3,"weaveFrequencyRange":0.2,"altitudeGain":0.32,"activityRateBase":2.2,"activityRateRange":1.3,"strafeWave":0,"turnWave":0,"upWave":0,"pitchWave":0,"rollTurn":0,"pressThreshold":0,"altitudeBase":0,"altitudeRange":0},"channels":{}}},"actions":{"jump":{"onPress":{"gate":{"$type":"all","predicates":[{"$type":"recently","fact":"Grounded","windowSeconds":0.09},{"$type":"compareState","state":"jumpUses","comparison":"Less","value":1}]},"latchSeconds":0.1,"effects":[{"$type":"setVerticalVelocity","velocity":7,"target":"Self"},{"$type":"addState","state":"jumpUses","value":1,"target":"Self"}]},"onRelease":{"gate":{"$type":"now","fact":"Rising"},"latchSeconds":0,"effects":[{"$type":"scaleVerticalVelocity","factor":0.5,"target":"Self"}]},"state":[{"name":"jumpUses","kind":"Counter","initial":0,"resetFact":"Grounded","lifetime":"Ephemeral","playerWritable":false}],"onFact":null}},"collider":{"$type":"capsule","endpoint":[0,1,0],"radius":0.35}}'

# world.status brackets the undo attempt so the two readings can be diffed.
$stdinLines = @(
    ('world.row.set kits ' + $promenaderRow.Replace('__MS__', '6')),
    ('world.row.set kits ' + $promenaderRow.Replace('__MS__', '7')),
    ('world.row.set kits ' + $promenaderRow.Replace('__MS__', '8')),
    'world.status',
    'world.undo 1',
    'world.status'
)
$scriptPath = Join-Path $scratchDir 'stdin.txt'
Set-Content -Path $scriptPath -Value ($stdinLines -join "`n") -NoNewline:$false

function Invoke-UndoPhase {
    param(
        [string]$Label
    )

    $outPath = Join-Path $scratchDir "out-$Label.txt"

    Push-Location $repoRoot
    try {
        Get-Content $scriptPath | & dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 8 *> $outPath
        # HAZARD fix (same as sdf-decode-sign-refusal/run.ps1): capture the native process's own exit code
        # IMMEDIATELY — a process that printed every expected line and then crashed or exited nonzero must
        # still fail this runner, not silently pass because the transcript happened to look right.
        $dotnetExitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    return [PSCustomObject]@{
        Label      = $Label
        Transcript = (Get-Content -Raw $outPath)
        ExitCode   = $dotnetExitCode
        OutPath    = $outPath
    }
}

function Get-StatusLines {
    param([string]$Transcript)

    return [regex]::Matches($Transcript, '\[world\.status: [^\]]*\]') | ForEach-Object { $_.Value }
}

$failures = @()
$phases = @{}

Write-Output '---- phase 1: control (current fixed code, no sabotage) ----'
& dotnet build $worldProject -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    Write-Error 'phase 1 build failed'
    exit 1
}
$phases['control'] = Invoke-UndoPhase -Label 'control'

Write-Output '---- phase 2: sabotaged (forces journal entry 1 to fail re-validation during replay) ----'
Push-Location $repoRoot
try {
    git apply --check $patchPath
    if ($LASTEXITCODE -ne 0) {
        Write-Error 'sabotage patch does not apply cleanly against the current tree'
        exit 1
    }
    git apply $patchPath
} finally {
    Pop-Location
}

try {
    & dotnet build $worldProject -c Release | Out-Null
    if ($LASTEXITCODE -ne 0) {
        $failures += 'phase 2 (sabotaged) build failed'
    } else {
        $phases['sabotaged'] = Invoke-UndoPhase -Label 'sabotaged'
    }
} finally {
    # Revert the sabotage NO MATTER WHAT happened above — a mid-script failure must never leave the tree
    # sabotaged for the next thing that touches it.
    Push-Location $repoRoot
    try {
        git apply -R $patchPath
        $revertExit = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    if ($revertExit -ne 0) {
        Write-Error "FAILED TO REVERT THE SABOTAGE PATCH — the tree at $repoRoot is left sabotaged; revert scripts/sabotage/undo-entry2-revalidation-failure.patch by hand"
        exit 1
    }
}

Write-Output '---- phase 3: reverted (rebuild after un-sabotaging, re-run phase 1''s script) ----'
& dotnet build $worldProject -c Release | Out-Null
if ($LASTEXITCODE -ne 0) {
    $failures += 'phase 3 (reverted) build failed'
} else {
    $phases['reverted'] = Invoke-UndoPhase -Label 'reverted'
}

# ---- Assertions ----

foreach ($name in @('control', 'sabotaged', 'reverted')) {
    if (-not $phases.ContainsKey($name)) {
        $failures += "phase '$name' never ran (its build failed above) — cannot assert its transcript"
    }
}

if ($failures.Count -eq 0) {
    $control = $phases['control']
    $sabotaged = $phases['sabotaged']
    $reverted = $phases['reverted']

    foreach ($phase in @($control, $sabotaged, $reverted)) {
        if ($phase.ExitCode -ne 0) {
            $failures += "phase '$($phase.Label)': dotnet run exited $($phase.ExitCode) (expected 0)"
        }
    }

    # ---- Phase 1 (control): undo SUCCEEDS, and the two world.status readings DIFFER (dirty 3 -> dirty 2) ----
    if ($control.Transcript -notmatch [regex]::Escape('[world.undo: dropped 1, 2 remaining]')) {
        $failures += "control: missing the undo SUCCESS line '[world.undo: dropped 1, 2 remaining]'"
    }
    if ($control.Transcript -match 'undo refused') {
        $failures += 'control FORBIDDEN: an undo refusal appeared in the unsabotaged control run'
    }
    $controlStatus = Get-StatusLines -Transcript $control.Transcript
    if ($controlStatus.Count -ne 2) {
        $failures += "control: expected exactly 2 world.status lines, found $($controlStatus.Count)"
    } elseif ($controlStatus[0] -eq $controlStatus[1]) {
        $failures += 'control: the two world.status readings are identical — undo should have changed dirty/undoable from 3 to 2'
    } elseif (($controlStatus[0] -notmatch 'dirty 3 undoable 3') -or ($controlStatus[1] -notmatch 'dirty 2 undoable 2')) {
        $failures += "control: expected dirty 3->2, got '$($controlStatus[0])' then '$($controlStatus[1])'"
    }

    # ---- Phase 2 (sabotaged): undo REFUSES naming journal entry 1, and the two world.status readings are IDENTICAL ----
    # Match on content, not exact glyphs: the engine emits em-dashes as separators, which this pattern spans with
    # ".*" rather than pinning to a literal dash character (avoids console-codepage mangling false-FAILs too).
    $refusalPattern = '\[world\.undo: undo refused: replay failed at journal entry 1 \(UpsertKit .promenader.\).*' +
        'SABOTAGE-INJECTED: forced replay-only re-validation failure for docs/verification/undo-all-or-nothing.*' +
        'never present outside that runner.s sabotage patch\)\]'
    if ($sabotaged.Transcript -notmatch $refusalPattern) {
        $failures += "sabotaged: missing the expected refusal naming journal entry 1 and the sabotage reason (pattern: $refusalPattern)"
    }
    if ($sabotaged.Transcript -match [regex]::Escape('[world.undo: dropped')) {
        $failures += 'sabotaged FORBIDDEN: an undo SUCCESS line appeared — the sabotaged entry should have refused the whole undo'
    }
    $sabotagedStatus = Get-StatusLines -Transcript $sabotaged.Transcript
    if ($sabotagedStatus.Count -ne 2) {
        $failures += "sabotaged: expected exactly 2 world.status lines, found $($sabotagedStatus.Count)"
    } elseif ($sabotagedStatus[0] -ne $sabotagedStatus[1]) {
        $failures += "sabotaged: the definition read-back CHANGED across the refused undo — '$($sabotagedStatus[0])' vs '$($sabotagedStatus[1])' (expected byte-identical: nothing installed)"
    } elseif ($sabotagedStatus[0] -notmatch 'dirty 3 undoable 3') {
        $failures += "sabotaged: expected both world.status readings to show dirty 3 undoable 3 (journal untouched), got '$($sabotagedStatus[0])'"
    }

    # ---- Phase 3 (reverted): byte-identical assertions to phase 1 — the round-trip left the build exactly as it found it ----
    if ($reverted.Transcript -notmatch [regex]::Escape('[world.undo: dropped 1, 2 remaining]')) {
        $failures += "reverted: missing the undo SUCCESS line '[world.undo: dropped 1, 2 remaining]' — the revert+rebuild did not restore the fixed build"
    }
    if ($reverted.Transcript -match 'undo refused') {
        $failures += 'reverted FORBIDDEN: an undo refusal appeared after reverting the sabotage patch'
    }
    $revertedStatus = Get-StatusLines -Transcript $reverted.Transcript
    if ($revertedStatus.Count -ne 2) {
        $failures += "reverted: expected exactly 2 world.status lines, found $($revertedStatus.Count)"
    } elseif (($revertedStatus[0] -notmatch 'dirty 3 undoable 3') -or ($revertedStatus[1] -notmatch 'dirty 2 undoable 2')) {
        $failures += "reverted: expected dirty 3->2, got '$($revertedStatus[0])' then '$($revertedStatus[1])'"
    }

    foreach ($phase in @($control, $sabotaged, $reverted)) {
        if ($phase.Transcript -match 'Unhandled exception') {
            $failures += "phase '$($phase.Label)' FORBIDDEN: an unhandled exception reached the transcript"
        }
    }
}

# Confirm the sabotage was reverted, regardless of outcome above: every patched file is back to the exact
# content it had before the run. Compared by hash against the pre-run snapshot rather than against a clean
# working tree, so the assertion is "the revert restored it" and nothing else.
foreach ($rel in $patchedPaths) {
    $full = Join-Path $repoRoot $rel
    $after = ((Test-Path $full) ? (Get-FileHash -Path $full -Algorithm SHA256).Hash : $null)

    if ($null -eq $after) {
        $failures += "$rel does not exist after the run — the sabotage patch did not revert cleanly"
    } elseif ($after -ne $hashesBefore[$rel]) {
        $failures += "$rel differs from its pre-run content — the sabotage patch did not revert cleanly (revert scripts/sabotage/undo-entry2-revalidation-failure.patch by hand)"
    }
}

Write-Output "---- transcripts: $scratchDir ----"
foreach ($name in @('control', 'sabotaged', 'reverted')) {
    if ($phases.ContainsKey($name)) {
        Write-Output "  $name -> $($phases[$name].OutPath) (exit $($phases[$name].ExitCode))"
    }
}
Write-Output "---- failures: $($failures.Count) ----"

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }

    exit 1
}

Write-Output 'PASS: world.undo journal replay is all-or-nothing (control succeeds + changes state, sabotaged phase refuses naming entry 1 with state unchanged, revert restores green).'
exit 0
