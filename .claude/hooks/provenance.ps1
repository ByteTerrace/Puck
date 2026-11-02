#Requires -Version 7.0

<#
.SYNOPSIS
Warns at session start when the agent configuration Claude Code loaded comes from another checkout than the one
the session works in.

.DESCRIPTION
A SessionStart hook for startup, resume and compact. Claude Code loads .claude/agents, the unscoped skills and these
hooks from the session's project root, and puck-mcp and ancestor walks reach the main checkout, while the lead works
in its own checkout. This runs `puck agents provenance`, which computes each checkout's configuration keys now and
records nothing, and on a difference shows the owner and the session the expected and actual paths and branches.
Silent when they agree.

The check is the verb; this hook only finds a CLI build that has it: the working checkout's, then the project
root's, then the main checkout's.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/cli-build.ps1"
. "$PSScriptRoot/command-policy.ps1"

$raw = Read-PolicyStdin
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
$payload = ConvertFrom-Json -InputObject $raw -AsHashtable
$cwd = $payload['cwd']
if ([string]::IsNullOrEmpty($cwd) -or -not (Test-Path -LiteralPath $cwd -PathType Container)) { exit 0 }
$projectRoot = $env:CLAUDE_PROJECT_DIR
if ([string]::IsNullOrEmpty($projectRoot)) { $projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../..')).Path }

$working = & git -C $cwd rev-parse --show-toplevel 2>$null
if (($LASTEXITCODE -ne 0) -or -not $working) { exit 0 }
$candidates = @($working, $projectRoot, (Get-MainCheckout $cwd)) | Where-Object { $_ } | Select-Object -Unique

function Write-Reply([string] $text) {
    ConvertTo-Json -Compress -Depth 4 -EscapeHandling EscapeNonAscii -InputObject @{
        systemMessage      = $text
        hookSpecificOutput = @{ hookEventName = 'SessionStart'; additionalContext = $text }
    }
}

$tried = [Collections.Generic.List[string]]::new()
foreach ($checkout in $candidates) {
    $build = Get-CliBuild $checkout
    if ($null -eq $build) { continue }
    $tried.Add($build.Replace('\', '/'))
    $output = @(& dotnet (Join-Path $build 'Puck.Cli.dll') agents provenance --checkout $cwd --project-root $projectRoot 2>&1 | ForEach-Object { "$_" })
    $code = $LASTEXITCODE
    # A build older than the verb answers with a parse error instead; the next candidate may be newer.
    if (-not @($output | Where-Object { $_.StartsWith('agents provenance:') -or $_.StartsWith('puck agents provenance:') }).Count) { continue }
    if ($code -eq 0) { exit 0 }
    Write-Reply ((@("Agent configuration provenance ($($payload['source'] ?? 'start')):") + $output) -join "`n")
    exit 0
}

$where = $tried.Count ? "the builds at $($tried -join ', ') predate it" : "no checkout among $($candidates -join ', ') has a Release build of src/Puck.Cli"
Write-Reply "Agent configuration provenance was not checked: $where. Build src/Puck.Cli in Release in the working checkout, then run ``puck agents provenance`` before dispatching lanes."
exit 0
