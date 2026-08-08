<#
.SYNOPSIS
Runner-asserted verification for the sdf.decode sign-validation closure.

.DESCRIPTION
SdfDocumentDecoder now refuses a negative radius/half-extent/round or material
channel AT DECODE (SdfRefusal.NumberNegative), mirroring EVERY
SdfProgramBuilder.RequireNonNegative call site this door reaches: sphere,
capsule, and cylinder radii; cylinder half-height; torus major/minor radii;
box half-extents and round (eight shape fields); and all four material
channels (albedo, emissive, specular, shininess) — instead of letting a sign
violation reach the builder and surface as BuilderRejectedOp/
BuilderRejectedMaterial.

This is TABLE-DRIVEN over all twelve fields, not just two of them: a runner
that only exercised sphere.radius and albedo would still PASS if the box/
capsule/cylinder/torus/emissive/specular/shininess checks were deleted. Every
negative case places its bad value at INDEX 1 (a valid op/material occupies
index 0 first), so a decoder that hard-codes index 0 in its context string
(or that only checks the first entry of an array) is caught, not just one
that fails to check sign at all. Each case also asserts, BY THE CASE'S OWN
UNIQUE FIXTURE FILENAME, that the composed/Replay path was never reached for
that document — proving the refusal happened before world.sdf.load composed
anything, not just that SOME text happened to appear in the transcript.

Proving the fired REASON is NumberNegative (not merely that some refusal
fired): world.sdf.load's output is exception.Message alone — the engine does
not echo SdfRefusal.Reason on the wire — so this runner does not read the
enum name off that line. Instead it asserts the EXACT message text
ReadNonNegativeFloat/ReadNonNegativeVector3 generate ("... must be
non-negative."), and that string is produced by NO OTHER code path in
SdfDocumentDecoder.cs (grep confirms a single call site per helper family) —
matching it is equivalent to proving NumberNegative fired, not just proving
*a* refusal fired. The world.refusals sdf.decode row (asserted separately)
proves a different, narrower fact: that the catalog DECLARES the row and its
condition text — it does not by itself prove any particular run fired it,
which is why both assertions exist and neither substitutes for the other.

Boots Puck.World ONCE, pipes every case through the console as one stdin
script (Immediate commands; results echo to stdout on success and stderr on
refusal — this runner merges both streams, exactly as docs/agent-guide.md's
"driver merging the two streams" describes), and asserts the transcript
against required and forbidden regexes. Also asserts the dotnet process's own
exit code is 0 — a process that printed every expected line and then crashed
or exited nonzero must still fail this runner, not silently pass.

.EXAMPLE
pwsh -File docs/verification/sdf-decode-sign-refusal/run.ps1
#>

$ErrorActionPreference = 'Stop'

# The engine emits em-dashes; under an OEM console codepage (e.g. a pwsh
# spawned from Git Bash) the captured transcript mangles them and the
# assertions below false-FAIL. Pin the whole pipe to UTF-8 — BOM-LESS: a
# BOM'd encoding writes its preamble into the piped stdin and corrupts the
# FIRST command (see the undo-all-or-nothing runner, which caught this).
[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
$OutputEncoding = [System.Text.UTF8Encoding]::new($false)

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path
$fixtures = Join-Path $PSScriptRoot 'fixtures'
$validControl = Join-Path $fixtures 'valid-control.sdf.json'

if (-not (Test-Path $validControl)) {
    Write-Error "fixture missing: $validControl"
    exit 1
}

# Scratch is UNIQUE PER RUN. Concurrent agent sessions run these batteries on one machine, and a fixed
# scratch name plus a blind Remove-Item collides with a sibling run — measured both as a startup failure
# against a file another process still holds open, and as the quieter corruption of deleting a live run's
# artifacts out from under it. Prior runs' directories are swept best-effort only once they are old enough
# that no live run can still own them; a locked or fresh sibling survives untouched, and a sweep failure is
# never this run's failure.
$scratchPrefix = 'puck-sdf-decode-sign-refusal'
$scratchDir = Join-Path $env:TEMP ('{0}-{1:yyyyMMdd-HHmmss}-{2}' -f $scratchPrefix, (Get-Date), $PID)

Get-ChildItem -Path $env:TEMP -Directory -Filter ($scratchPrefix + '*') -ErrorAction SilentlyContinue |
    Where-Object { $_.CreationTimeUtc -lt [DateTime]::UtcNow.AddHours(-6) } |
    ForEach-Object { try { Remove-Item -Recurse -Force -Path $_.FullName -ErrorAction Stop } catch { } }

New-Item -ItemType Directory -Force -Path $scratchDir | Out-Null

# ---- Table: all eight builder-mirrored shape fields. Each case's negative value sits at ops[1]; ops[0] is a
# harmless valid sphere occupying index 0, so a decoder that only checks the FIRST array entry (or that hard-codes
# "ops[0]" in its context string) fails these, not just a decoder that never checks sign at all.
$shapeCases = @(
    @{ Id = 'sphere-radius';       OpJson = '{"op":"sphere","radius":-1,"material":0}';                              ExpectedMessage = 'ops[1].radius: -1 must be non-negative.' },
    @{ Id = 'capsule-radius';      OpJson = '{"op":"capsule","endpoint":[0,1,0],"radius":-1,"material":0}';          ExpectedMessage = 'ops[1].radius: -1 must be non-negative.' },
    @{ Id = 'cylinder-radius';     OpJson = '{"op":"cylinder","radius":-1,"halfHeight":1,"material":0}';             ExpectedMessage = 'ops[1].radius: -1 must be non-negative.' },
    @{ Id = 'cylinder-halfheight'; OpJson = '{"op":"cylinder","radius":1,"halfHeight":-1,"material":0}';             ExpectedMessage = 'ops[1].halfHeight: -1 must be non-negative.' },
    @{ Id = 'torus-majorradius';   OpJson = '{"op":"torus","majorRadius":-1,"minorRadius":1,"material":0}';          ExpectedMessage = 'ops[1].majorRadius: -1 must be non-negative.' },
    @{ Id = 'torus-minorradius';   OpJson = '{"op":"torus","majorRadius":1,"minorRadius":-1,"material":0}';          ExpectedMessage = 'ops[1].minorRadius: -1 must be non-negative.' },
    @{ Id = 'box-halfextents';     OpJson = '{"op":"box","halfExtents":[-1,1,1],"material":0}';                      ExpectedMessage = 'ops[1].halfExtents: [-1, 1, 1] every component must be non-negative.' },
    @{ Id = 'box-round';           OpJson = '{"op":"box","halfExtents":[1,1,1],"round":-1,"material":0}';            ExpectedMessage = 'ops[1].round: -1 must be non-negative.' }
)

# ---- Table: all four material channels. Each case's negative value sits at materials[1]; materials[0] is a valid
# entry occupying index 0, same index-plumbing argument as the shape cases above. ops is legal-empty ([]) — Decode()
# runs DecodeMaterials before DecodeOps, so a material refusal never needs a shape op to reach it.
$materialCases = @(
    @{ Id = 'material-albedo';    MaterialJson = '{"albedo":[-1,0,0]}';                    ExpectedMessage = 'materials[1].albedo: [-1, 0, 0] every component must be non-negative.' },
    @{ Id = 'material-emissive';  MaterialJson = '{"albedo":[1,1,1],"emissive":-1}';        ExpectedMessage = 'materials[1].emissive: -1 must be non-negative.' },
    @{ Id = 'material-specular';  MaterialJson = '{"albedo":[1,1,1],"specular":-1}';        ExpectedMessage = 'materials[1].specular: -1 must be non-negative.' },
    @{ Id = 'material-shininess'; MaterialJson = '{"albedo":[1,1,1],"shininess":-1}';       ExpectedMessage = 'materials[1].shininess: -1 must be non-negative.' }
)

$cases = @()

foreach ($shapeCase in $shapeCases) {
    $fixturePath = Join-Path $scratchDir "case-$($shapeCase.Id).sdf.json"
    $document = @"
{
  "schema": "puck.sdf.v1",
  "materials": [ { "albedo": [1, 1, 1] } ],
  "ops": [ { "op": "sphere", "radius": 1, "material": 0 }, $($shapeCase.OpJson) ]
}
"@
    Set-Content -Path $fixturePath -Value $document -NoNewline:$false
    $cases += @{ Id = $shapeCase.Id; Path = $fixturePath; ExpectedMessage = $shapeCase.ExpectedMessage }
}

foreach ($materialCase in $materialCases) {
    $fixturePath = Join-Path $scratchDir "case-$($materialCase.Id).sdf.json"
    $document = @"
{
  "schema": "puck.sdf.v1",
  "materials": [ { "albedo": [1, 1, 1] }, $($materialCase.MaterialJson) ],
  "ops": []
}
"@
    Set-Content -Path $fixturePath -Value $document -NoNewline:$false
    $cases += @{ Id = $materialCase.Id; Path = $fixturePath; ExpectedMessage = $materialCase.ExpectedMessage }
}

Write-Output "---- $($cases.Count) negative sign-validation cases + 1 control ----"

$stdinLines = @()

foreach ($case in $cases) {
    $stdinLines += "world.sdf.load $($case.Path)"
}

$stdinLines += "world.sdf.load $validControl"
$stdinLines += "world.refusals sdf.decode"

$scriptPath = Join-Path $scratchDir 'stdin.txt'
$outPath = Join-Path $scratchDir 'out.txt'
Set-Content -Path $scriptPath -Value ($stdinLines -join "`n") -NoNewline:$false

Push-Location $repoRoot
try {
    Get-Content $scriptPath | & dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 8 *> $outPath
    # HAZARD fix: capture the native process's own exit code IMMEDIATELY — a process that printed every expected
    # line and then crashed or exited nonzero must still fail this runner, not silently pass because the transcript
    # happened to look right. Must be read before any other command (Pop-Location included) can overwrite it.
    $dotnetExitCode = $LASTEXITCODE
} finally {
    Pop-Location
}

$transcript = Get-Content -Raw $outPath

# Each assertion is (name, pattern, requireMatch). requireMatch=$true means the pattern MUST appear; $false means it
# MUST NOT appear (a forbidden regex). Every entry here is capable of failing — this is not a smoke test that
# always exits 0; a broken decoder (any ONE of the twelve fields, or the control, or the exit code) fails the run.
$assertions = @()

foreach ($case in $cases) {
    $assertions += @{
        Name = "case $($case.Id): refuses NumberNegative, naming the field, at index 1"
        Pattern = [regex]::Escape("[world.sdf.load: $($case.ExpectedMessage)]")
        Require = $true
    }
    $assertions += @{
        Name = "case $($case.Id) FORBIDDEN: this exact fixture must never reach the composed/Replay path"
        Pattern = [regex]::Escape($case.Path) + '.*composed'
        Require = $false
    }
}

$assertions += @{
    Name = 'CONTROL: a previously-valid document still loads (world.sdf.load succeeds)'
    Pattern = [regex]::Escape($validControl) + [regex]::Escape("' — 1 op(s), 1 material(s), fnv1a") + '.* — composed\]'
    Require = $true
}
$assertions += @{
    Name = 'world.refusals sdf.decode declares the NumberNegative row and its condition text (catalog fact, not proof of a specific firing — see .DESCRIPTION)'
    Pattern = [regex]::Escape("sdf.decode/NumberNegative [verdict] a decoded number that must be non-negative (a shape's radius/half-extent/round, or a material channel) is negative")
    Require = $true
}
$assertions += @{
    Name = 'FORBIDDEN: no unhandled exception (a document rejection must never escape as a raw stack trace)'
    Pattern = 'Unhandled exception'
    Require = $false
}

$failures = @()

foreach ($assertion in $assertions) {
    $matched = [regex]::IsMatch($transcript, $assertion.Pattern)

    if ($assertion.Require -and -not $matched) {
        $failures += "MISSING (required): $($assertion.Name)"
    } elseif ((-not $assertion.Require) -and $matched) {
        $failures += "PRESENT (forbidden): $($assertion.Name)"
    }
}

if ($dotnetExitCode -ne 0) {
    $failures += "dotnet run exited $dotnetExitCode (expected 0) — a crash or nonzero exit must fail this runner even if the transcript otherwise looks right"
}

Write-Output "---- transcript: $outPath ----"
Write-Output "---- dotnet exit code: $dotnetExitCode ----"
Write-Output "---- assertions: $($assertions.Count), failures: $($failures.Count) ----"

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }

    exit 1
}

Write-Output "PASS: sdf.decode sign-validation closure verified (all $($assertions.Count) assertions, $($cases.Count) fields, held)."
exit 0
