<#
.SYNOPSIS
Runner-asserted verification that all four charter worlds (play, dive, kart,
jump) boot WINDOWED with a healthy binding surface: no binding-vocabulary
narration, a forced seat recompose that is not rejected, and a document that
round-trips through `world.save`.

.DESCRIPTION
A binding value-kind mismatch is FATAL at recompose but only NARRATED at boot.
`WorldSeatBindings.ValidateAffordancesLoudly` sweeps a seat's composed document
at boot and prints `[player.bindings] <label>: <error>` per finding on stderr —
and then carries on. `WorldSeatBindings.RecomposeSeat` is where the same
document is actually compiled, and there a bad entry rejects the WHOLE seat
document (`[player.bindings] <label> recompose rejected …; keeping the prior
mapping.`) and keeps the previous mapping. Nothing fails, nothing exits
non-zero, and every later `player.bind` on that seat is silently discarded —
the seat keeps answering with a mapping that no longer reflects what was asked
of it.

Booting alone would not see that: the boot sweep narrates and continues, and a
run that never recomposes never reaches the fatal path. So each world's script
FORCES one recompose (`player.bind 1 keyboard.p editor.status`) and this runner
asserts both halves of the answer — the success echo is REQUIRED on stdout (the
positive control: without it the "no recompose rejected" absence assertion
would pass vacuously on a run where the bind never fired) and the whole
`[player.bindings]` prefix is FORBIDDEN on stderr (it carries every form of
binding-vocabulary narration at once: the boot sweep's findings, the
unregistered-command skips, and both recompose-rejection forms).

These runs are WINDOWED on purpose — no `--headless`. `editor.status` is now
CORE-registered (2026-08-09: the whole editor/sculpt verb surface moved into
`AddWorldAuthoritativeCore` for command-vocabulary parity — every boot shape
must see the SAME vocabulary the document validators check against), so it
answers identically in both shapes today; the destination stays `editor.status`
regardless, and `'names no registered command'` stays asserted FORBIDDEN as a
general health check on this recompose, not a windowed-vs-headless probe.

Each world is a fresh process with its own `--state-dir`, small window, and its
own stdout/stderr capture — this battery's claims are stream-specific, so the
two streams are never merged. The split is the console's own: the boot origin
line and every binding narration go to stderr, and a submitted line's verdict
is separated by `IsError` (accepted echoes to stdout, refusals to stderr), so
each assertion below sits on the stream its line can actually appear on.

.EXAMPLE
pwsh -File docs/verification/four-world-boot-smoke/run.ps1
#>

$ErrorActionPreference = 'Stop'

# The engine emits em-dashes and arrows; under an OEM console codepage the captured transcript mangles them and
# the assertions below false-FAIL. Pin the whole pipe to UTF-8 — but WITHOUT a BOM (the posture
# docs/verification/undo-all-or-nothing/run.ps1 records): [System.Text.Encoding]::UTF8 carries a preamble that gets
# written once at the start of the redirected pipe into the native dotnet process, landing a BOM glyph in front of
# the FIRST piped stdin line and making the wire parser reject it outright — silently dropping the run's first
# command rather than failing loudly.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
[Console]::OutputEncoding = $utf8NoBom
$OutputEncoding = $utf8NoBom

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

# The four-world charter's whole roster. Every one of these must boot windowed with a clean binding surface.
$worlds = @('play', 'dive', 'kart', 'jump')

foreach ($id in $worlds) {
    $worldPath = Join-Path $repoRoot "src\Puck.World\Assets\worlds\$id.world.json"

    if (-not (Test-Path $worldPath)) {
        Write-Error "world document missing: $worldPath"
        exit 1
    }
}

# Scratch is UNIQUE PER RUN. Concurrent agent sessions run these batteries on one machine, and a fixed
# scratch name plus a blind Remove-Item collides with a sibling run — measured both as a startup failure
# against a file another process still holds open, and as the quieter corruption of deleting a live run's
# artifacts out from under it. Prior runs' directories are swept best-effort only once they are old enough
# that no live run can still own them; a locked or fresh sibling survives untouched, and a sweep failure is
# never this run's failure.
$scratchPrefix = 'puck-four-world-boot-smoke'
$scratchDir = Join-Path $env:TEMP ('{0}-{1:yyyyMMdd-HHmmss}-{2}' -f $scratchPrefix, (Get-Date), $PID)

Get-ChildItem -Path $env:TEMP -Directory -Filter ($scratchPrefix + '*') -ErrorAction SilentlyContinue |
    Where-Object { $_.CreationTimeUtc -lt [DateTime]::UtcNow.AddHours(-6) } |
    ForEach-Object { try { Remove-Item -Recurse -Force -Path $_.FullName -ErrorAction Stop } catch { } }

New-Item -ItemType Directory -Force -Path $scratchDir | Out-Null

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

$transcripts = @()

Write-Output "---- $($worlds.Count) charter worlds must boot windowed with a healthy binding surface ----"

foreach ($id in $worlds) {
    $worldPath = Join-Path $repoRoot "src\Puck.World\Assets\worlds\$id.world.json"
    $savedPath = Join-Path $scratchDir "saved-$id.world.json"
    $stateDir = Join-Path $scratchDir "state-$id"
    $stdinPath = Join-Path $scratchDir "stdin-$id.txt"
    $outPath = Join-Path $scratchDir "out-$id.log"
    $errPath = Join-Path $scratchDir "err-$id.log"

    # Blank lines and '#' comments are skipped by the engine's stdin reader, so the script is annotated in place.
    Set-Content -Path $stdinPath -Value @"
    # A harmless leading Immediate read-back: a leading world.wait would silently swallow every line behind it.
    replay.status

    # The forced recompose. A value-kind mismatch anywhere in this seat's composed binding document rejects the WHOLE
    # seat document HERE (only narrated at boot), so this line is what turns a silent binding failure loud.
    player.bind 1 keyboard.p editor.status

# Control feel is PER SEAT, and every seat's feel must resolve on every charter world — a world whose
# playerDefaults.seatLook failed to load would not refuse here, it would simply answer with someone else's numbers.
# Seat 1 carries a profile (it joins at boot); seat 2 sits at the world's own floor. Both are read BEFORE the live
# edit below so the pair can be compared against the pair after it.
world.view.camera 1
world.view.camera 2

# The live per-seat discriminator: this replaces the WORLD's feel only. Seat 2 (at the floor) must move to
# leftbutton/0.009; seat 1 (carrying its profile's feel) must NOT. Asserting only that something changed would pass
# on a world-wide store that moved both.
world.row.set playerDefaults.seatLook {"yawSensitivity":0.009,"pitchSensitivity":0.009,"invertYaw":true,"invertPitch":false,"arming":"LeftButton","stickLookRate":2.6}
world.wait 2
world.view.camera 1
world.view.camera 2

# The document must still round-trip after the recompose.
world.save $savedPath
"@ -NoNewline:$false

    Push-Location $repoRoot
    try {
        # stdout and stderr are captured SEPARATELY: the assertions below are stream-specific, and merging them
        # would let a line asserted on one stream be satisfied by the other.
        Get-Content $stdinPath | & dotnet run --project src/Puck.World -c Release -- --world $worldPath --exit-after-seconds 8 --width 640 --height 480 --state-dir $stateDir > $outPath 2> $errPath
        # Capture the native process's own exit code IMMEDIATELY — a process that printed every expected line and
        # then crashed or exited nonzero must still fail this runner.
        $exitCode = $LASTEXITCODE
    } finally {
        Pop-Location
    }

    $stdout = if (Test-Path $outPath) { Get-Content -Raw $outPath } else { '' }
    $stderr = if (Test-Path $errPath) { Get-Content -Raw $errPath } else { '' }

    $transcripts += [PSCustomObject]@{ Id = $id; OutPath = $outPath; ErrPath = $errPath; ExitCode = $exitCode }

    # ---- stderr: the boot origin, and the whole binding-narration prefix's absence ----
    Test-Assertion -Name "$id (stderr): boots from the named document ('[world] definition: <path> (--world)')" `
        -Matched ([regex]::IsMatch($stderr, [regex]::Escape("[world] definition: $worldPath (--world)"))) -Require $true
    Test-Assertion -Name "$id (stderr): FORBIDDEN — any '[player.bindings]' narration (boot-sweep findings, unregistered-command skips, or a recompose rejection)" `
        -Matched ([regex]::IsMatch($stderr, [regex]::Escape('[player.bindings]'))) -Require $false
    # Subsumed by the assertion above, and named separately because it IS the pinned invariant: a rejected
    # recompose keeps the prior mapping and silently discards every later player.bind on the seat.
    Test-Assertion -Name "$id (stderr): FORBIDDEN — 'recompose rejected' (the seat would keep its prior mapping and discard later binds)" `
        -Matched ([regex]::IsMatch($stderr, 'recompose rejected')) -Require $false
    Test-Assertion -Name "$id (stderr): FORBIDDEN — no unhandled exception" `
        -Matched ([regex]::IsMatch($stderr, 'Unhandled exception')) -Require $false

    # ---- stderr: the three REFUSAL forms this battery's success echoes have to beat ----
    # These are asserted on STDERR, not stdout: LauncherServiceRegistration's TextCommandSource routes an IsError
    # result to stderr and an accepted one to stdout, so every refusal below lands on stderr and a forbidden-on-
    # stdout assertion for one of them could never fire. Measured — a deliberate --headless control run answered
    # the bind with "names no registered command" on stderr with stdout carrying no player.bind line at all.
    Test-Assertion -Name "$id (stderr): FORBIDDEN — 'names no registered command' (editor.status is core-registered in every boot shape; this must never fire windowed or headless)" `
        -Matched ([regex]::IsMatch($stderr, 'names no registered command')) -Require $false
    Test-Assertion -Name "$id (stderr): FORBIDDEN — world.save 'could not write'" `
        -Matched ([regex]::IsMatch($stderr, 'could not write')) -Require $false
    Test-Assertion -Name "$id (stderr): FORBIDDEN — world.save 'cannot mutate every section'" `
        -Matched ([regex]::IsMatch($stderr, 'cannot mutate every section')) -Require $false

    # ---- stdout: the forced recompose actually happened, and world.save wrote ----
    # The '→' and '—' glyphs are spanned with '.*' rather than pinned as literals (console-codepage mangling
    # posture, same as undo-all-or-nothing's refusal pattern).
    Test-Assertion -Name "$id (stdout): the forced seat-1 recompose SUCCEEDED (player.bind success echo — SetSessionRebind has already committed by the time it prints)" `
        -Matched ([regex]::IsMatch($stdout, "\[player\.bind: seat 1 'keyboard\.p'.*'editor\.status'.*unsaved")) -Require $true
    Test-Assertion -Name "$id (stdout): world.save wrote the snapshot" `
        -Matched ([regex]::IsMatch($stdout, [regex]::Escape("[world.save: $savedPath ("))) -Require $true

    # ---- stdout: per-seat control feel resolves, and a world-floor edit moves ONLY the seat sitting at the floor ----
    # Ordered parse rather than four independent regexes: the four echoes differ only in their numbers, so matching
    # them positionally is what lets the before/after PAIR be compared. Fewer than four means the run did not get far
    # enough, which must fail rather than silently satisfy a subset.
    $orbits = [regex]::Matches($stdout, '\[world\.view\.camera: player=(\d)[^\]]*? arming=(\w+) yawReference=\w+ yawSensitivity=([-0-9.]+)')
    Test-Assertion -Name "$id (stdout): four world.view.camera echoes (seat 1 and 2, before and after the live edit)" `
        -Matched ($orbits.Count -eq 4) -Require $true

    if ($orbits.Count -eq 4) {
        # Before: both seats sit at the world's authored feel, so both read the same.
        Test-Assertion -Name "$id (stdout): before the edit, seat 1 and seat 2 agree (both at the world's authored feel)" `
            -Matched (($orbits[0].Groups[2].Value -eq $orbits[1].Groups[2].Value) -and ($orbits[0].Groups[3].Value -eq $orbits[1].Groups[3].Value)) -Require $true

        # After: seat 2 took the edit...
        Test-Assertion -Name "$id (stdout): after the edit, seat 2 (at the world's floor) TOOK the new feel (leftbutton/0.009)" `
            -Matched (($orbits[3].Groups[2].Value -eq 'leftbutton') -and ($orbits[3].Groups[3].Value -eq '0.009')) -Require $true

        # ...and seat 1 did NOT. This is the discriminator: a world-wide store would move both, and every other
        # assertion here would still pass.
        Test-Assertion -Name "$id (stdout): after the edit, seat 1 (carrying its profile's feel) was UNTOUCHED" `
            -Matched (($orbits[2].Groups[2].Value -eq $orbits[0].Groups[2].Value) -and ($orbits[2].Groups[3].Value -eq $orbits[0].Groups[3].Value)) -Require $true

        # The seats must actually DIFFER afterwards — the negative above is vacuous if the edit never applied at all.
        Test-Assertion -Name "$id (stdout): after the edit, the two seats genuinely differ (the edit was not a no-op)" `
            -Matched ($orbits[2].Groups[2].Value -ne $orbits[3].Groups[2].Value) -Require $true
    }

    # ---- process and filesystem ----
    Test-Assertion -Name "${id}: dotnet run exited 0" -Matched ($exitCode -eq 0) -Require $true

    $savedLength = if (Test-Path $savedPath) { (Get-Item $savedPath).Length } else { 0 }

    Test-Assertion -Name "${id}: the saved document exists and is non-empty" -Matched ($savedLength -gt 0) -Require $true
}

Write-Output "---- transcripts: $scratchDir ----"
foreach ($transcript in $transcripts) {
    Write-Output "  $($transcript.Id) -> $($transcript.OutPath) / $($transcript.ErrPath) (exit $($transcript.ExitCode))"
}

Write-Output "---- assertions: $assertionCount, failures: $($failures.Count) ----"

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }

    exit 1
}

Write-Output "PASS: all four charter worlds boot windowed with a healthy binding surface (all $assertionCount assertions held)."
exit 0
