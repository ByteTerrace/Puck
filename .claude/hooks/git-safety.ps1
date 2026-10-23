#Requires -Version 7.0
# Refuses git that touches what every lane shares, for a tool whose own rules cannot stop it headless: Antigravity's
# auto mode runs sandboxed commands without consulting its ask list, and cannot prompt the owner when headless. The
# set is .codex/rules/git.rules' set plus rebase and config, which a lane never needs; Claude keeps its own ask rules and classifier and does not register this hook.
. "$PSScriptRoot/command-policy.ps1"
$policy = Read-PolicyInput
if ($null -eq $policy) { exit 0 }

$redirects = @('-C', '-c', '--git-dir', '--work-tree', '--namespace', '--config-env')

foreach ($tokens in Get-PolicyInvocations $policy.Command) {
    for ($i = 0; $i -lt $tokens.Count; $i++) {
        if (-not (Test-PolicyExecutablePosition $tokens $i)) { continue }
        if ((Get-PolicyExecutable $tokens[$i]) -ne 'git') { continue }
        $sub = -1
        for ($j = $i + 1; $j -lt $tokens.Count; $j++) {
            if ($tokens[$j] -in $redirects -or $tokens[$j] -match '^--(git-dir|work-tree|namespace|config-env)=') {
                Deny-PolicyCommand $policy 'A lane runs git in its own worktree; -C, -c, --git-dir and --work-tree are refused because they reach past these rules into another lane''s work.'
            }
            if ($tokens[$j].StartsWith('-')) { continue }
            $sub = $j
            break
        }
        if ($sub -lt 0) { continue }
        $verb = $tokens[$sub]
        $rest = @($tokens | Select-Object -Skip ($sub + 1))
        switch ($verb) {
            'push' { Deny-PolicyCommand $policy 'A lane never pushes: leave the work for the lead, who lands and pushes it (a push to main prompts the owner).' }
            'stash' {
                if ($rest.Count -gt 0 -and $rest[0] -in @('list', 'show')) { break }
                Deny-PolicyCommand $policy 'The stash stack is shared by every worktree; set work aside with a WIP commit on your own branch.'
            }
            'branch' {
                if (@($rest | Where-Object { $_ -cmatch '^(-[^-]*[dDmMcCf]|--delete|--force|--move|--copy)' }).Count) {
                    Deny-PolicyCommand $policy 'Branch refs are shared by every lane; the lead deletes, moves and copies branches.'
                }
            }
            { $_ -in @('checkout', 'switch') } {
                if (@($rest | Where-Object { $_ -cmatch '^(-B|-C|--force-create)$' }).Count) {
                    Deny-PolicyCommand $policy 'Resetting a branch by name can overwrite another lane''s branch; create a new branch or reset your own with git reset.'
                }
            }
            'fetch' {
                if (@($rest | Where-Object { $_ -cmatch '^(-p|--prune|--prune-tags)$' -or $_ -match '^[^-].*:' }).Count) {
                    Deny-PolicyCommand $policy 'Pruning or fetching into a local ref rewrites refs every lane shares; fetch without a destination and merge.'
                }
            }
            'worktree' {
                if ($rest.Count -gt 0 -and $rest[0] -in @('remove', 'prune', 'move', 'add', 'lock', 'unlock', 'repair')) {
                    Deny-PolicyCommand $policy 'The worktree list is shared by every lane; the lead manages worktrees.'
                }
            }
            'tag' {
                if (@($rest | Where-Object { $_ -cmatch '^(-[df]|--delete|--force)$' }).Count) {
                    Deny-PolicyCommand $policy 'Tags are shared by every lane; the lead deletes and moves tags.'
                }
            }
            'reflog' {
                if ($rest.Count -gt 0 -and $rest[0] -in @('expire', 'delete')) {
                    Deny-PolicyCommand $policy 'The reflog is the shared recovery log; the lead decides.'
                }
            }
            { $_ -in @('update-ref', 'filter-branch', 'filter-repo', 'replace', 'gc', 'prune', 'rebase', 'config') } {
                Deny-PolicyCommand $policy "git $verb rewrites shared history, refs or configuration; the lead decides."
            }
        }
    }
}
Complete-PolicyCommand $policy
