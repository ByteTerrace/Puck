#Requires -Version 7.0
# git-safety: the git and gh commands that reach what every lane shares. One table for all three tools:
#
# | Command                                                     | Claude lead | Lane (Claude subagent, Codex, Antigravity) |
# |-------------------------------------------------------------|-------------|--------------------------------------------|
# | Push or gh write reaching main, force, delete, mirror, all, | ask         | deny                                       |
# | prune, or a destination that cannot be resolved             |             |                                            |
# | Any other push, gh pr create, gh release writes             | abstain     | deny                                       |
# | Shared state: stash but list/show, branch/tag delete, move, | abstain     | deny                                       |
# | copy or force, checkout -B, switch -C, worktree changes,    |             |                                            |
# | fetch --prune or into a local ref, reflog expire/delete,    |             |                                            |
# | update-ref, symbolic-ref writes, remote and notes writes,   |             |                                            |
# | gc, prune, repack, maintenance, pack-refs, filter-branch,   |             |                                            |
# | filter-repo, replace, rebase, config writes; git -C, -c,    |             |                                            |
# | --git-dir, --work-tree and GIT_DIR-style variables          |             |                                            |
#
# Abstain means the tool's own rules decide: Claude's settings ask rules, Codex's git.rules (which matches the lane
# column where a prefix rule can say it). A remote partner pushing its own branch from its own session is the prose
# exception in AGENTS.md; nothing here carves it out.

$script:GitLanePush = 'A lane never pushes: leave the work for the lead, who lands and pushes it (a push to main prompts the owner).'

# Returns 'main' when a push reaches main, rewrites or deletes remote refs, pushes every branch, or lands somewhere
# it cannot resolve; otherwise 'branch'.
function Get-GitPushTarget {
    param([hashtable] $Git)
    $rest = $Git.Rest
    if (Test-GitOption $rest 'fd' 'o' @('--all', '--branches', '--mirror', '--delete', '--force', '--force-with-lease', '--force-if-includes', '--prune') @('--repo', '--push-option', '--receive-pack', '--exec')) {
        return 'main'
    }
    $operands = @(Get-GitOperands $rest 'o' @('--repo', '--push-option', '--receive-pack', '--exec'))
    $refspecs = @($operands | Select-Object -Skip 1)
    if (-not $refspecs.Count) {
        # A bare push follows configuration: a configured push refspec or push.default=matching can name any branch.
        $configured = Invoke-PolicyGit $Git.Cwd $Git.Globals @('config', '--get-regexp', '^remote\..*\.push$')
        if ($configured.Code -eq 0 -and $configured.Out.Trim()) { return 'main' }
        $default = Invoke-PolicyGit $Git.Cwd $Git.Globals @('config', '--get', 'push.default')
        if ($default.Out.Trim() -eq 'matching') { return 'main' }
        $push = Invoke-PolicyGit $Git.Cwd $Git.Globals @('rev-parse', '--abbrev-ref', '--symbolic-full-name', '@{push}')
        if ($push.Code -ne 0) { return 'main' }
        $branch = ($push.Out.Trim() -split '/', 2)[-1]
        return ($branch -eq 'main') ? 'main' : 'branch'
    }
    foreach ($refspec in $refspecs) {
        if ($refspec.StartsWith('+') -or $refspec -eq ':' -or $refspec.Contains('*')) { return 'main' }
        $parts = $refspec -split ':', 2
        if ($parts.Count -eq 2 -and $parts[0] -eq '') { return 'main' }
        $destination = $parts[-1]
        if ($parts.Count -eq 1) {
            $mapped = Invoke-PolicyGit $Git.Cwd $Git.Globals @('config', '--get-regexp', '^remote\..*\.push$')
            if ($mapped.Code -eq 0 -and $mapped.Out.Trim()) { return 'main' }
            if ($destination -in @('HEAD', '@')) {
                $head = Invoke-PolicyGit $Git.Cwd $Git.Globals @('symbolic-ref', '--short', '-q', 'HEAD')
                if ($head.Code -ne 0) { return 'main' }
                $destination = $head.Out.Trim()
            }
        }
        if (($destination -replace '^refs/heads/', '') -eq 'main') { return 'main' }
    }
    return 'branch'
}

# Returns 'main', 'branch' or $null for a gh call: merges, ref and contents writes reach main without a git push.
function Get-GhPushTarget {
    param($Invocation)
    $argv = $Invocation.Argv
    if ($argv.Count -lt 2) { return $null }
    $group = $argv[1]
    $verb = ($argv.Count -gt 2) ? $argv[2] : ''
    if (($group -eq 'pr' -and $verb -eq 'merge') -or ($group -eq 'repo' -and $verb -in @('sync', 'delete', 'rename', 'archive')) -or ($group -eq 'workflow' -and $verb -eq 'run')) {
        return 'main'
    }
    if (($group -eq 'pr' -and $verb -eq 'create') -or ($group -eq 'release' -and $verb -in @('create', 'delete', 'edit', 'upload', 'delete-asset'))) {
        return 'branch'
    }
    if ($group -ne 'api') { return $null }
    $method = $null
    $fields = [Collections.Generic.List[string]]::new()
    $hasInput = $false
    $endpoint = $null
    for ($j = 2; $j -lt $argv.Count; $j++) {
        $t = $argv[$j]
        if ($t -cin @('-X', '--method')) { $method = $argv[++$j] }
        elseif ($t -cmatch '^(-X|--method=)(.+)$') { $method = $Matches[2] }
        elseif ($t -cin @('-f', '-F', '--field', '--raw-field')) { $fields.Add([string]$argv[++$j]) }
        elseif ($t -cmatch '^(-f|-F|--field=|--raw-field=)(.+)$') { $fields.Add($Matches[2]) }
        elseif ($t -ceq '--input' -or $t.StartsWith('--input=')) { $hasInput = $true; if ($t -ceq '--input') { $j++ } }
        elseif ($t -cin @('-H', '--header', '-q', '--jq', '-t', '--template', '--cache', '--hostname', '-p', '--preview')) { $j++ }
        elseif (-not $t.StartsWith('-') -and $null -eq $endpoint) { $endpoint = $t }
    }
    if ($endpoint -eq 'graphql') {
        if ($hasInput -or @($fields | Where-Object { $_ -match '\bmutation\b' }).Count) { return 'main' }
        return $null
    }
    # Fields and --input make gh send a POST unless -X says otherwise.
    $verbUsed = $method ?? (($fields.Count -or $hasInput) ? 'POST' : 'GET')
    if ($verbUsed.ToUpperInvariant() -in @('GET', 'HEAD')) { return $null }
    return 'main'
}

# A gh call passes its known target; a git push is resolved only for the lead, since a lane's push is refused anyway.
function Get-GitPushDecision {
    param([hashtable] $Context, $Invocation, [hashtable] $Git, [string] $Known)
    if ($Context.Role -eq 'lane') { return New-PolicyDecision 'deny' $script:GitLanePush }
    $resolved = if ($Known) { $Known } else { Get-GitPushTarget $Git }
    if ($resolved -eq 'main') {
        return New-PolicyDecision 'ask' "This push reaches main, rewrites or deletes remote refs, or lands where git-safety cannot resolve it: $($Invocation.Text). A push to main deploys; confirm it is the owner's."
    }
    return $null
}

# Returns the reason a lane may not run this git verb, or $null.
function Get-GitSharedStateReason {
    param([hashtable] $Git)
    $rest = $Git.Rest
    $first = $rest.Count ? $rest[0] : $null
    switch -CaseSensitive ($Git.Verb) {
        'stash' {
            if ($first -in @('list', 'show')) { return $null }
            return 'The stash stack is shared by every worktree; set work aside with a WIP commit on your own branch.'
        }
        'branch' {
            if (Test-GitOption $rest 'dDmMcCf' 'u' @('--delete', '--force', '--move', '--copy') @('--set-upstream-to', '--contains', '--no-contains', '--merged', '--no-merged', '--points-at', '--format', '--sort')) {
                return 'Branch refs are shared by every lane; the lead deletes, moves, copies and force-resets branches.'
            }
        }
        { $_ -in @('checkout', 'switch') } {
            $flag = ($Git.Verb -eq 'checkout') ? 'B' : 'C'
            if (Test-GitOption $rest $flag 'bc' @('--force-create')) {
                return 'Resetting a branch by name can overwrite another lane''s branch; create a new branch or reset your own with git reset.'
            }
        }
        { $_ -in @('fetch', 'pull') } {
            $values = 'jo'
            $longValues = @('--depth', '--deepen', '--shallow-since', '--shallow-exclude', '--refmap', '--server-option', '--jobs', '--upload-pack', '--negotiation-tip', '--filter', '--recurse-submodules-default', '--strategy', '--strategy-option')
            if (Test-GitOption $rest 'pP' $values @('--prune', '--prune-tags') $longValues) {
                return 'Pruning rewrites the remote-tracking refs every lane shares; fetch without --prune and merge.'
            }
            if (-not (Test-GitOption $rest '' $values @('--all', '--multiple') $longValues)) {
                foreach ($refspec in @(Get-GitOperands $rest $values $longValues | Select-Object -Skip 1)) {
                    $parts = $refspec.TrimStart('+') -split ':', 2
                    if ($parts.Count -eq 2 -and $parts[1]) {
                        return 'Fetching into a local ref rewrites a ref every lane shares; fetch without a destination and merge.'
                    }
                }
            }
        }
        'worktree' {
            if ($first -in @('add', 'remove', 'prune', 'move', 'lock', 'unlock', 'repair')) { return 'The worktree list is shared by every lane; the lead manages worktrees.' }
        }
        'tag' {
            if (Test-GitOption $rest 'df' 'mFun' @('--delete', '--force') @('--message', '--file', '--local-user', '--sort', '--format', '--contains', '--no-contains', '--points-at', '--merged', '--no-merged', '--cleanup')) {
                return 'Tags are shared by every lane; the lead deletes and moves tags.'
            }
        }
        'reflog' {
            if ($first -in @('expire', 'delete')) { return 'The reflog is the shared recovery log; the lead decides.' }
        }
        'symbolic-ref' {
            if ((Test-GitOption $rest 'd' '' @('--delete')) -or @(Get-GitOperands $rest 'm').Count -ge 2) { return 'Symbolic refs are shared by every lane; the lead decides.' }
        }
        'remote' {
            if ($first -in @('add', 'remove', 'rm', 'rename', 'set-url', 'set-head', 'set-branches', 'prune', 'update')) { return 'Remote configuration is shared by every lane; the lead decides.' }
        }
        'notes' {
            $sub = @(Get-GitOperands $rest '' @('--ref')) | Select-Object -First 1
            if ($sub -in @('add', 'copy', 'append', 'edit', 'merge', 'remove', 'prune')) { return 'Notes refs are shared by every lane; the lead decides.' }
        }
        'config' {
            if (Test-GitConfigWrite $rest) { return 'Repository configuration is shared by every lane; read it with --get or --list, and let the lead change it.' }
        }
        { $_ -in @('update-ref', 'filter-branch', 'filter-repo', 'replace', 'gc', 'prune', 'repack', 'maintenance', 'pack-refs', 'rebase') } {
            if ($Git.Verb -eq 'replace' -and ($rest.Count -eq 0 -or $first -in @('-l', '--list'))) { return $null }
            return "git $($Git.Verb) rewrites shared history, refs or objects; the lead decides."
        }
    }
    return $null
}

# True when git config writes: a set or unset form, an editor, or a key with a value.
function Test-GitConfigWrite {
    param([string[]] $Rest)
    $reads = @('get', 'list', 'get-all', 'get-regexp', 'get-urlmatch', 'get-color', 'get-colorbool')
    $writes = @('set', 'unset', 'rename-section', 'remove-section', 'edit')
    $first = $Rest.Count ? $Rest[0] : $null
    if ($first -in $reads) { return $false }
    if ($first -in $writes) { return $true }
    $scopeValues = @('--file', '-f', '--blob', '--type', '--default', '--comment', '--value')
    if (Test-GitOption $Rest 'e' 'f' @('--unset', '--unset-all', '--add', '--replace-all', '--rename-section', '--remove-section', '--edit') $scopeValues) { return $true }
    if (Test-GitOption $Rest 'l' 'f' @('--get', '--get-all', '--get-regexp', '--get-urlmatch', '--get-color', '--get-colorbool', '--list') $scopeValues) { return $false }
    return @(Get-GitOperands $Rest 'f' $scopeValues).Count -ge 2
}

function Test-GitSafetyPolicy {
    param([hashtable] $Context, $Invocation, [int] $Depth = 0)
    if ($Invocation.Name -eq 'gh') {
        $target = Get-GhPushTarget $Invocation
        if ($null -eq $target) { return }
        return Get-GitPushDecision $Context $Invocation $null $target
    }
    if ($Invocation.Name -ne 'git') { return }
    $git = Split-GitInvocation $Invocation
    if ($Context.Role -eq 'lane' -and $git.Redirected) {
        return New-PolicyDecision 'deny' 'A lane runs git in its own worktree with its own configuration; -C, -c, --git-dir, --work-tree and GIT_DIR-style variables reach past these rules into another lane''s work.'
    }
    $git = Resolve-GitAlias $git
    if ($git.ContainsKey('Shell')) {
        if ($Depth -ge 3) { return New-PolicyRoleDecision $Context 'A git alias nests too deeply to inspect.' 'A git alias nests too deeply to inspect.' }
        $words = @($git.Rest | ForEach-Object { "'" + $_.Replace("'", "'\''") + "'" })
        foreach ($inner in Get-PolicyInvocations ((@($git.Shell) + $words) -join ' ') 'posix' $git.Cwd) {
            Test-GitSafetyPolicy $Context $inner ($Depth + 1)
        }
        return
    }
    if ($git.Verb -eq 'push') { return Get-GitPushDecision $Context $Invocation $git }
    if ($Context.Role -ne 'lane') { return }
    $reason = Get-GitSharedStateReason $git
    if ($reason) { return New-PolicyDecision 'deny' $reason }
}
