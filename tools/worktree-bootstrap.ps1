# Worktree bootstrap — the mandatory first action for any agent working in a
# git worktree of this repo. Converts the "worktree spawned stale" hazard from
# a judgment call into a mechanical step:
#   - HEAD already at the expected base -> proceed (exit 0).
#   - Clean tree, wrong base            -> reset --hard to the base (exit 0).
#   - Dirty tree, wrong base            -> refuse loudly (exit 1): a human or
#     orchestrator must decide; resetting would destroy work.
#
# Usage (from anywhere): pwsh tools/worktree-bootstrap.ps1 -ExpectedBase <sha-or-ref> [-WorktreePath <path>]
param(
    [Parameter(Mandatory = $true)]
    [string]$ExpectedBase,
    [string]$WorktreePath = (Get-Location).Path
)

$ErrorActionPreference = 'Stop'

function Get-Git([string[]]$GitArgs) {
    $result = git -C $WorktreePath @GitArgs 2>&1
    if ($LASTEXITCODE -ne 0) { throw "git $($GitArgs -join ' ') failed: $result" }
    return $result
}

$head = (Get-Git @('rev-parse', 'HEAD')).Trim()
$base = (Get-Git @('rev-parse', "$ExpectedBase^{commit}")).Trim()
$dirty = (Get-Git @('status', '--porcelain')) | Where-Object { $_ }

if ($head -eq $base) {
    Write-Host "worktree-bootstrap: OK — HEAD already at expected base $($base.Substring(0,7))."
    exit 0
}

$headLine = (Get-Git @('log', '-1', '--oneline')).Trim()
if ($dirty) {
    Write-Host "worktree-bootstrap: REFUSING — HEAD is $headLine (expected $($base.Substring(0,7))) and the tree is DIRTY:"
    $dirty | ForEach-Object { Write-Host "  $_" }
    Write-Host "A stale base with uncommitted work needs a human/orchestrator decision. Do not reset by hand unless you understand what the changes are."
    exit 1
}

Write-Host "worktree-bootstrap: stale base detected — HEAD was $headLine; resetting clean tree to $($base.Substring(0,7))."
Get-Git @('reset', '--hard', $base) | Out-Null
Write-Host "worktree-bootstrap: now at $((Get-Git @('log', '-1', '--oneline')).Trim())."
exit 0
