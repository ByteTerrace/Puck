<#
.SYNOPSIS
Runner-asserted link and path check over the world-project documentation set.

.DESCRIPTION
The Puck.World README decomposition (this runner landed with it) rewrote
src/Puck.World/README.md as an entry point and moved its depth into per-project
and per-folder READMEs. Every one of those documents cites files by path —
markdown links and backticked repository paths — and a citation that stops
resolving is exactly the drift the decomposition was meant to remove.

For each document in the checked set the runner extracts and verifies:

  (a) every relative markdown link target ([text](path)) — external schemes
      and pure in-page anchors are skipped, a fragment is stripped — resolved
      against the document's own directory, then the repository root. Always
      ENFORCED.
  (b) every backticked rooted repository path (`src/...`, `docs/...`,
      `verification/...`, `build/...`, `tests/...` with a file extension),
      resolved the same way. Always ENFORCED.
  (c) every backticked bare filename with a source-ish extension
      (`WorldServer.cs`, `run.ps1`, ...), looked up in an index of every
      filename under src/, docs/, verification/, build/, and .claude/skills/
      (docs legitimately cite skill reference files by name). ENFORCED for
      documents under src/ (a project README cites its neighbors); advisory
      for documents under docs/, which legitimately name out-of-repo files.

It fails (exit 1) listing every citation that does not resolve. It asserts one
control first — a deliberately nonexistent path must fail resolution — so an
all-green run proves the checker can actually turn red.

.PARAMETER Documents
Repository-relative markdown files to check. Defaults to the world
documentation set this runner landed with.

.EXAMPLE
pwsh -File docs/verification/doc-links/run.ps1
#>
param(
    [string[]]$Documents = @(
        'src/Puck.World/README.md',
        'src/Puck.World/Client/README.md',
        'src/Puck.World/Audio/README.md',
        'src/Puck.World.Data/README.md',
        'src/Puck.World.Server/README.md',
        'docs/README.md',
        'docs/agent-guide.md',
        'docs/project-map.md',
        'docs/capability-channels-plan.md',
        'docs/capability-channels-STATE.md'
    )
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..\..')).Path

# ---- The filename index: every filename under the trees a doc may cite by bare name. ----
$fileNameIndex = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)

foreach ($tree in @('src', 'docs', 'verification', 'build', '.claude/skills')) {
    $treePath = Join-Path $repoRoot $tree

    if (Test-Path -LiteralPath $treePath) {
        foreach ($file in (Get-ChildItem -LiteralPath $treePath -Recurse -File)) {
            [void]$fileNameIndex.Add($file.Name)
        }
    }
}

# Root-level files (CLAUDE.md, Puck.slnx, Directory.Build.props, ...) are citable too.
foreach ($file in (Get-ChildItem -LiteralPath $repoRoot -File)) {
    [void]$fileNameIndex.Add($file.Name)
}

# Deliberately historical paths: CLAUDE.md rule 1 pins these as existing only in git history, and
# docs/project-map.md states exactly that where it names them. Their non-resolution is correct.
$historicalCitations = @('src/Puck', 'src/Puck.Avatars')

# Deliberately historical filenames: documents superseded and deleted, cited only as the thing a
# living document replaced. Their absence from the index is correct.
$historicalFileNames = @('addon-input-plan.md')

# Resolves one cited relative target against the citing document's directory, then the repository root.
function Test-Citation {
    param(
        [Parameter(Mandatory)] [string]$Target,
        [Parameter(Mandatory)] [string]$DocumentDirectory
    )

    foreach ($candidate in @((Join-Path $DocumentDirectory $Target), (Join-Path $repoRoot $Target))) {
        if (Test-Path -LiteralPath $candidate) {
            return $true
        }
    }

    return $false
}

# Extracts every checkable citation from one document. Returns objects of (Line, Kind, Target).
function Get-Citations {
    param(
        [Parameter(Mandatory)] [AllowEmptyString()] [string[]]$Lines
    )

    $citations = @()
    $lineNumber = 0

    foreach ($line in $Lines) {
        $lineNumber++

        # (a) Markdown links.
        foreach ($match in [regex]::Matches($line, '\[[^\]]*\]\(([^)\s]+)\)')) {
            $target = $match.Groups[1].Value

            if ($target -match '^[a-z][a-z0-9+.-]*:') { continue }   # http:, https:, mailto:, ...
            if ($target.StartsWith('#')) { continue }                 # in-page anchor

            $target = ($target -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($target)) { continue }

            $citations += [PSCustomObject]@{ Line = $lineNumber; Kind = 'link'; Target = $target }
        }

        # (b)/(c) Backticked repository paths and bare filenames.
        foreach ($match in [regex]::Matches($line, '`([^`]+)`')) {
            $token = $match.Groups[1].Value

            if ($token -match '^(src|docs|verification|build|tests|\.claude)/[A-Za-z0-9._/\-]+\.[A-Za-z0-9]+$') {
                $citations += [PSCustomObject]@{ Line = $lineNumber; Kind = 'path'; Target = $token }
            } elseif ($token -match '^[A-Za-z0-9._\-]+\.(cs|md|json|ps1|csproj|props|slnx|wasm|wat)$') {
                $citations += [PSCustomObject]@{ Line = $lineNumber; Kind = 'file'; Target = $token }
            }
        }
    }

    return $citations
}

# ---- Control: the checker must be able to fail. ----
if ((Test-Citation -Target 'src/Puck.World/this-file-does-not-exist.md' -DocumentDirectory $repoRoot) -or
    $fileNameIndex.Contains('this-file-does-not-exist.md')) {
    Write-Error 'CONTROL FAILED: a nonexistent path resolved — the checker cannot discriminate.'
    exit 1
}

$failures = @()
$advisories = @()
$checkedCount = 0

foreach ($document in $Documents) {
    $documentPath = Join-Path $repoRoot $document

    if (-not (Test-Path -LiteralPath $documentPath)) {
        $failures += "${document}: the document itself does not exist"
        continue
    }

    $documentDirectory = Split-Path -Parent $documentPath
    $enforceBareFileNames = $document.StartsWith('src/')
    $citations = Get-Citations -Lines (Get-Content -LiteralPath $documentPath)

    foreach ($citation in $citations) {
        $checkedCount++

        if ($citation.Kind -eq 'file') {
            if (($historicalFileNames -contains $citation.Target) -or $fileNameIndex.Contains($citation.Target)) {
                continue
            }

            $message = "${document}:$($citation.Line): cited filename '$($citation.Target)' exists nowhere under src/, docs/, verification/, build/, or .claude/skills/"

            if ($enforceBareFileNames) {
                $failures += $message
            } else {
                $advisories += $message
            }

            continue
        }

        if ($historicalCitations -contains $citation.Target) {
            continue
        }

        if (-not (Test-Citation -Target $citation.Target -DocumentDirectory $documentDirectory)) {
            $failures += "${document}:$($citation.Line): $($citation.Kind) '$($citation.Target)' does not resolve"
        }
    }
}

Write-Output "---- documents: $($Documents.Count); citations checked: $checkedCount; failures: $($failures.Count); advisories: $($advisories.Count) ----"

foreach ($advisory in $advisories) {
    Write-Output "note: $advisory"
}

if ($failures.Count -gt 0) {
    foreach ($failure in $failures) {
        Write-Output "FAIL: $failure"
    }

    exit 1
}

Write-Output 'PASS: every relative link and cited repository path in the checked documents resolves.'
exit 0
