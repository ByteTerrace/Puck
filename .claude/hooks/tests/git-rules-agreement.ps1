#Requires -Version 7.0
# Checks that .codex/rules/git.rules and git-safety agree on a Codex lane's commands: every shared row gets the same
# verdict from `codex execpolicy check` (forbidden is a refusal; prompt and no match are not) and from the dispatcher
# with a Codex payload. The hook-only rows are commands no prefix rule can express; the hook must refuse them and
# the rules must stay silent, so a rule that starts covering one moves it to the shared table. Needs the codex CLI.
$ErrorActionPreference = 'Stop'
$hooks = Split-Path -Parent $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $hooks)
$rules = Join-Path $root '.codex/rules/git.rules'
. "$hooks/command-policy.ps1"
if (-not (Get-Command codex -ErrorAction SilentlyContinue)) { 'SKIP: no codex CLI on PATH.'; exit 0 }

$shared = @(
    @('deny', 'git push origin main'), @('deny', 'git push origin HEAD:refs/heads/topic'), @('deny', 'git push'),
    @('deny', 'git --no-pager push --dry-run origin HEAD:main'), @('deny', 'git stash push -m wip'), @('deny', 'git stash pop'),
    @('deny', 'git stash drop'), @('deny', 'git stash -u'), @('deny', 'git branch -D x'), @('deny', 'git branch -Df x'),
    @('deny', 'git branch -m a b'), @('deny', 'git checkout -B x'), @('deny', 'git switch -C x'),
    @('deny', 'git switch --force-create x'), @('deny', 'git fetch --prune'), @('deny', 'git fetch -p origin'),
    @('deny', 'git pull --prune'), @('deny', 'git worktree add ../x'), @('deny', 'git worktree remove ../x'), @('deny', 'git gc'),
    @('deny', 'git rebase main'), @('deny', 'git update-ref refs/heads/x HEAD'), @('deny', 'git reflog expire --all'),
    @('deny', 'git tag -d v1'), @('deny', 'git tag -fa v1'), @('deny', 'git remote add o ../x'), @('deny', 'git notes add -m x'),
    @('deny', 'git config --unset user.name'), @('deny', 'git config set user.name x'), @('deny', 'git -C ../x status'),
    @('deny', 'git -c foo=bar status'), @('deny', 'git symbolic-ref -d HEAD'), @('deny', 'git replace -d x'),
    @('deny', 'git maintenance run'), @('deny', 'gh pr merge 1'), @('deny', 'gh pr create --fill'), @('deny', 'gh repo sync'),
    @('deny', 'gh api -X POST repos/o/r/merges'), @('deny', 'gh workflow run deploy'),
    @('pass', 'git stash list'), @('pass', 'git stash show -p'), @('pass', 'git status'), @('pass', 'git branch -a'),
    @('pass', 'git branch new'), @('pass', 'git checkout -b new'), @('pass', 'git switch -c new'), @('pass', 'git fetch origin'),
    @('pass', 'git worktree list'), @('pass', 'git tag v1'), @('pass', 'git reflog show'), @('pass', 'git config --get user.name'),
    @('pass', 'git config --list'), @('pass', 'git config user.name'), @('pass', 'gh pr view 1'), @('pass', 'gh api repos/o/r/pulls'),
    @('pass', 'git commit -m wip'), @('pass', 'git log'), @('pass', 'git remote -v'), @('pass', 'git symbolic-ref HEAD'),
    @('pass', 'git replace -l'), @('pass', 'git reset --hard HEAD')
)
$hookOnly = @(
    'git stash', 'git config user.name x', 'git config alias.p push', 'git fetch origin main:main', 'git -C../x status',
    'git branch --quiet -D x', 'gh api repos/o/r/merges -f base=main'
)

function Get-HookVerdict {
    param([string[]] $Argv)
    $payload = @{ turn_id = 't'; tool_name = 'exec_command'; tool_input = @{ cmd = $Argv }; cwd = $root }
    $reply = Invoke-AgentPolicy $payload 'codex'
    if (-not $reply) { return 'pass' }
    return (ConvertFrom-Json $reply -AsHashtable).hookSpecificOutput.permissionDecision
}
function Get-RulesVerdict {
    param([string[]] $Argv)
    $result = (& codex execpolicy check --rules $rules @Argv) | ConvertFrom-Json -AsHashtable
    if ($LASTEXITCODE) { throw "codex execpolicy check failed for $($Argv -join ' ')" }
    return ($result['decision'] -eq 'forbidden') ? 'deny' : 'pass'
}

$failures = [Collections.Generic.List[string]]::new()
foreach ($row in $shared) {
    $argv = @(Split-PolicyWords $row[1])
    $hook = Get-HookVerdict $argv
    $rule = Get-RulesVerdict $argv
    if ($hook -ne $row[0] -or $rule -ne $row[0]) { $failures.Add("shared: expected $($row[0]), hook $hook, rules $rule for $($row[1])") }
}
foreach ($command in $hookOnly) {
    $argv = @(Split-PolicyWords $command)
    $hook = Get-HookVerdict $argv
    $rule = Get-RulesVerdict $argv
    if ($hook -ne 'deny' -or $rule -ne 'pass') { $failures.Add("hook-only: expected hook deny and rules pass, hook $hook, rules $rule for $command") }
}
if ($failures.Count) {
    $failures | ForEach-Object { "FAIL: $_" }
    throw "$($failures.Count) git.rules agreement rows failed."
}
"PASS: $($shared.Count) shared and $($hookOnly.Count) hook-only git.rules agreement rows."
