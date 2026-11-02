#Requires -Version 7.0
# read-only-agent: holds the shell of a read-only Claude agent to the no-write rule its definition gives its editors.
# An agent type is read-only when its .claude/agents definition (in this hook's checkout, the one the harness loaded
# it from) disallows both Edit and Write, or lists tools without either; scout and reviewer are. Such an agent's
# Bash and PowerShell commands are refused:
#
# - git other than reads: status, diff, show, log, blame, grep, the ls-* and rev-* plumbing, and the listing forms of
#   branch, tag, stash, worktree, config, reflog, remote and notes;
# - a write outside a scratch or findings path (any .claude/scratch/, the temp directory, ~/.claude/jobs/): output
#   redirections, the file commands (rm, mv, cp, mkdir, touch, tee, ln, sed -i, Set-Content, Out-File, New-Item,
#   Remove-Item, Copy-Item, Move-Item and their kin), or a target known only at run time;
# - the generators that rewrite tracked files (`dotnet format`, `dotnet new|add|remove|sln`, and the puck verbs
#   format, locks, schema, registry, vocabulary, baselines, branding, verbs and `agents link` without --check).
#
# Builds stay allowed: bin/ and obj/ are derived outputs git ignores, and a scout may build its CLI copy. What this
# does not see: the hook runs on Bash and PowerShell only, so an MCP tool (a World mutation through puck_exec) is
# outside it, and so is a script file a command runs, an interpreter's inline program (python -c, node -e), or a
# .NET call inside a PowerShell expression. The definition's disallowedTools keeps the editors themselves away.

$script:ReadOnlyWriteRoots = @(
    [IO.Path]::GetTempPath()
    (Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude/jobs')
) | ForEach-Object { $_.Replace('\', '/').TrimEnd('/').ToLowerInvariant() + '/' }
$script:ReadOnlyDevices = @('/dev/null', '/dev/stdout', '/dev/stderr', '/dev/tty', 'nul', '$null')
$script:ReadOnlyAgentTypes = $null

# The git verbs that only read, and the verbs read only in some forms (Test-ReadOnlyGitForm).
$script:ReadOnlyGitVerbs = @(
    'status', 'diff', 'show', 'log', 'blame', 'annotate', 'grep', 'ls-files', 'ls-tree', 'ls-remote', 'rev-parse',
    'rev-list', 'merge-base', 'cat-file', 'describe', 'shortlog', 'for-each-ref', 'show-ref', 'show-branch', 'name-rev',
    'range-diff', 'diff-tree', 'diff-index', 'diff-files', 'check-ignore', 'check-attr', 'check-mailmap',
    'check-ref-format', 'var', 'whatchanged', 'cherry', 'count-objects', 'version', 'help', 'verify-commit', 'verify-tag',
    'column', 'stripspace', 'patch-id'
)

# Reads the agent types whose definitions deny them both editors, from the definitions beside this hook.
function Get-ReadOnlyAgentTypes {
    if ($null -ne $script:ReadOnlyAgentTypes) { return $script:ReadOnlyAgentTypes }
    $types = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $directory = Join-Path $PSScriptRoot '../../agents'
    if (Test-Path -LiteralPath $directory -PathType Container) {
        foreach ($file in Get-ChildItem -LiteralPath $directory -Filter '*.md' -File) {
            $lines = @(Get-Content -LiteralPath $file.FullName -Encoding utf8 -TotalCount 60)
            if (-not $lines.Count -or $lines[0] -ne '---') { continue }
            $fields = @{}
            $key = $null
            for ($k = 1; $k -lt $lines.Count -and $lines[$k] -ne '---'; $k++) {
                if ($lines[$k] -match '^([A-Za-z]+):\s*(.*)$') { $key = $Matches[1]; $fields[$key] = @($Matches[2]) }
                elseif ($key -and $lines[$k] -match '^\s+-\s*(.+)$') { $fields[$key] += $Matches[1] }
            }
            $list = { param($name) @(($fields[$name] -join ',') -split '[,\[\]\s]+' | Where-Object { $_ }) }
            $denied = & $list 'disallowedTools'
            $allowed = & $list 'tools'
            $readOnly = (('Edit' -cin $denied) -and ('Write' -cin $denied)) -or ($fields.ContainsKey('tools') -and $allowed.Count -and -not ('Edit' -cin $allowed) -and -not ('Write' -cin $allowed))
            if ($readOnly -and $fields['name']) { [void]$types.Add(($fields['name'] -join '').Trim().Trim('"', "'")) }
        }
    }
    $script:ReadOnlyAgentTypes = $types
    return $types
}

# True when a resolved path lies where a read-only agent may write: a scratch directory, the temp directory or the
# Claude job directories.
function Test-ReadOnlyWritable {
    param([string] $Value, [bool] $Dynamic, [string] $Cwd)
    if ($Dynamic) { return $false }
    if ($Value.ToLowerInvariant() -in $script:ReadOnlyDevices) { return $true }
    # Git Bash maps /tmp to the temp directory.
    if ($Value.StartsWith('/tmp/') -and -not $Value.Contains('..')) { return $true }
    $path = Resolve-PolicyPath $Value $Cwd
    if (-not $path) { return $false }
    $normal = $path.Replace('\', '/').ToLowerInvariant()
    if ($normal -match '/\.claude/scratch(/|$)') { return $true }
    foreach ($root in $script:ReadOnlyWriteRoots) {
        if (($normal + '/').StartsWith($root)) { return $true }
    }
    return $false
}

# Returns the operands of an argument vector after its program name: words that are not options and not the value of
# an option listed in $Values (case-insensitive, either -name value or -name:value).
function Get-ReadOnlyOperands {
    param([string[]] $Argv, [bool[]] $Dynamic, [string[]] $Values = @())
    $operands = [Collections.Generic.List[object]]::new()
    $afterDashes = $false
    for ($j = 1; $j -lt $Argv.Count; $j++) {
        $t = $Argv[$j]
        if (-not $afterDashes -and $t -eq '--') { $afterDashes = $true; continue }
        if (-not $afterDashes -and $t.Length -gt 1 -and $t.StartsWith('-')) {
            if (($t -split ':', 2)[0] -in $Values -and -not $t.Contains(':')) { $j++ }
            continue
        }
        $operands.Add([pscustomobject]@{ Value = $t; Dynamic = ($j -lt $Dynamic.Count -and $Dynamic[$j]) })
    }
    return $operands.ToArray()
}

# Returns the value of the first named option among $Names (case-insensitive), as @{ Value; Dynamic }, or $null.
function Get-ReadOnlyNamed {
    param([string[]] $Argv, [bool[]] $Dynamic, [string[]] $Names)
    for ($j = 1; $j -lt $Argv.Count; $j++) {
        $parts = $Argv[$j] -split ':', 2
        if ($parts[0] -notin $Names) { continue }
        if ($parts.Count -eq 2) { return [pscustomobject]@{ Value = $parts[1]; Dynamic = ($j -lt $Dynamic.Count -and $Dynamic[$j]) } }
        if ($j + 1 -lt $Argv.Count) { return [pscustomobject]@{ Value = $Argv[$j + 1]; Dynamic = ($j + 1 -lt $Dynamic.Count -and $Dynamic[$j + 1]) } }
    }
    return $null
}

# Returns the paths a file command writes, each @{ Value; Dynamic }, or $null when the program writes no file.
function Get-ReadOnlyWriteTargets {
    param($Invocation)
    $argv = $Invocation.Argv
    $flags = [bool[]]@($Invocation.Dynamic)
    $name = $Invocation.Name.ToLowerInvariant()
    $pwshValues = @('-filter', '-include', '-exclude', '-encoding', '-value', '-itemtype', '-type', '-inputobject', '-credential', '-stream', '-delimiter', '-newname', '-name', '-variable', '-width', '-compressionlevel')
    switch ($name) {
        # Every operand is written or removed.
        { $_ -in @('rm', 'rmdir', 'unlink', 'mkdir', 'touch', 'truncate', 'shred', 'mv', 'move', 'mi', 'move-item', 'remove-item', 'del', 'erase', 'ri', 'rd', 'md', 'ni', 'new-item', 'clear-content', 'clc') } {
            $targets = @(Get-ReadOnlyOperands $argv $flags (@('-m', '-s', '--mode', '--size') + $pwshValues))
            $destination = Get-ReadOnlyNamed $argv $flags @('-destination')
            if ($destination) { $targets += $destination }
            # New-Item -Name writes beside its -Path, or in the working directory.
            if ($name -in @('ni', 'new-item') -and -not $targets.Count) { $targets = @([pscustomobject]@{ Value = '.'; Dynamic = $false }) }
            return $targets
        }
        # The destination: -Destination, -t DIR, or the last operand.
        { $_ -in @('cp', 'copy', 'cpi', 'copy-item', 'install', 'ln') } {
            $named = Get-ReadOnlyNamed $argv $flags @('-destination', '-t', '--target-directory')
            if ($named) { return @($named) }
            $operands = @(Get-ReadOnlyOperands $argv $flags (@('-m', '--mode', '-o', '-g', '-S', '--suffix') + $pwshValues))
            return $operands.Count ? @($operands[-1]) : @()
        }
        # The first operand or the named file: content and archive writers.
        { $_ -in @('tee', 'tee-object', 'set-content', 'sc', 'add-content', 'ac', 'out-file', 'export-csv', 'export-clixml', 'rename-item', 'ren', 'rni', 'expand-archive', 'compress-archive') } {
            if ($name -eq 'tee-object' -and (Get-ReadOnlyNamed $argv $flags @('-variable'))) { return @() }
            $named = Get-ReadOnlyNamed $argv $flags @('-path', '-literalpath', '-lp', '-filepath', '-destinationpath')
            if ($named) { return @($named) }
            $operands = @(Get-ReadOnlyOperands $argv $flags $pwshValues)
            if ($name -eq 'tee') { return $operands }
            if ($name -in @('expand-archive', 'compress-archive')) { return ($operands.Count -ge 2) ? @($operands[1]) : @([pscustomobject]@{ Value = '.'; Dynamic = $false }) }
            return $operands.Count ? @($operands[0]) : @()
        }
        # In-place editors write their file operands.
        { $_ -in @('sed', 'perl') } {
            $inPlace = @($argv | Select-Object -Skip 1 | Where-Object { $_ -cmatch '^(-i|--in-place)' -or ($_ -cmatch '^-[a-zA-Z]*i' -and $name -eq 'perl') }).Count
            if (-not $inPlace) { return $null }
            $scripted = @($argv | Where-Object { $_ -cin @('-e', '-f', '--expression', '--file') }).Count
            $operands = @(Get-ReadOnlyOperands $argv $flags @('-e', '-f', '--expression', '--file'))
            return $scripted ? $operands : @($operands | Select-Object -Skip 1)
        }
        'dd' {
            return @($argv | Where-Object { $_ -like 'of=*' } | ForEach-Object { [pscustomobject]@{ Value = $_.Substring(3); Dynamic = $false } })
        }
        'patch' {
            if (@($argv | Where-Object { $_ -eq '--dry-run' }).Count) { return @() }
            return @([pscustomobject]@{ Value = '.'; Dynamic = $false })
        }
    }
    return $null
}

# Returns why a git invocation is not a read, or $null.
function Get-ReadOnlyGitReason {
    param([hashtable] $Git)
    $verb = $Git.Verb
    $rest = @($Git.Rest)
    $first = $rest.Count ? $rest[0] : $null
    if ($verb -cin $script:ReadOnlyGitVerbs) { return $null }
    $listingValues = @('--contains', '--no-contains', '--merged', '--no-merged', '--points-at', '--format', '--sort', '--column')
    $reads = switch -CaseSensitive ($verb) {
        'branch' {
            -not (Test-GitOption $rest 'dDmMcCfu' '' @('--delete', '--force', '--move', '--copy', '--set-upstream-to', '--unset-upstream', '--edit-description', '--track', '--no-track', '--create-reflog') $listingValues) -and
                ((Test-GitOption $rest 'l' '' @('--list', '--show-current') $listingValues) -or -not @(Get-GitOperands $rest '' $listingValues).Count)
        }
        'tag' {
            -not (Test-GitOption $rest 'dfasmFu' '' @('--delete', '--force', '--annotate', '--sign', '--message', '--file', '--local-user', '--edit') ($listingValues + @('-n'))) -and
                ((Test-GitOption $rest 'l' '' @('--list') $listingValues) -or -not @(Get-GitOperands $rest '' $listingValues).Count)
        }
        'stash' { $first -in @('list', 'show') }
        'worktree' { $first -eq 'list' }
        'config' { -not (Test-GitConfigWrite $rest) }
        'reflog' { $null -eq $first -or $first -in @('show', 'exists') -or $first.StartsWith('-') }
        'remote' { $null -eq $first -or $first -in @('-v', '--verbose', 'show', 'get-url') }
        'notes' { $first -in @('list', 'show') }
        'symbolic-ref' { -not (Test-GitOption $rest 'd' '' @('--delete')) -and @(Get-GitOperands $rest 'm').Count -lt 2 }
        'apply' { (Test-GitOption $rest '' '' @('--check', '--stat', '--numstat', '--summary')) -and -not (Test-GitOption $rest '' '' @('--apply', '--index', '--cached', '--3way', '--reject')) }
        'format-patch' { Test-GitOption $rest '' '' @('--stdout') }
        'archive' { -not (Test-GitOption $rest 'o' '' @('--output')) }
        'hash-object' { -not (Test-GitOption $rest 'w' '' @()) }
        'interpret-trailers' { -not (Test-GitOption $rest '' '' @('--in-place')) }
        'submodule' { $first -in @('status', 'summary') }
        'replace' { $null -eq $first -or $first -in @('-l', '--list') }
        default { $false }
    }
    if ($reads) { return $null }
    return "git $verb writes the index, the worktree, refs or objects"
}

function Test-ReadOnlyAgentPolicy {
    param([hashtable] $Context, $Invocation, [int] $Depth = 0)
    if ($Context.Tool -ne 'claude' -or $Context.Role -ne 'lane') { return }
    $type = $Context.Payload['agent_type']
    if ([string]::IsNullOrEmpty($type) -or -not (Get-ReadOnlyAgentTypes).Contains($type)) { return }
    $refuse = { param($what) New-PolicyDecision 'deny' "A read-only $type lane changes no source, index or ref, and writes only under a .claude/scratch/ directory, the temp directory or ~/.claude/jobs/ ($what): $($Invocation.Text). Return what you found in your hand-back." }

    foreach ($target in @($Invocation.Writes)) {
        if (-not (Test-ReadOnlyWritable $target.Value $target.Dynamic $Invocation.Cwd)) { return & $refuse "a redirection to $($target.Value)" }
    }
    if ($Invocation.Name -eq ':') { return }
    $name = $Invocation.Name.ToLowerInvariant()
    if ($name -eq 'git') {
        $git = Split-GitInvocation $Invocation
        # git-safety refuses a lane's redirected or unresolved git on its own.
        if ($git.Redirected -or $git.Unresolved -or $null -eq $git.Verb) { return }
        $git = Resolve-GitAlias $git
        if ($git.ContainsKey('Shell')) {
            if ($Depth -ge 3) { return & $refuse 'a git alias too deep to inspect' }
            $words = @($git.Rest | ForEach-Object { "'" + $_.Replace("'", "'\''") + "'" })
            foreach ($inner in Get-PolicyInvocations ((@($git.Shell) + $words) -join ' ') 'posix' $git.Cwd) {
                Test-ReadOnlyAgentPolicy $Context $inner ($Depth + 1)
            }
            return
        }
        $reason = Get-ReadOnlyGitReason $git
        if ($reason) { return & $refuse $reason }
        if ($git.Verb -cnotin $script:ReadOnlyGitVerbs -and (Test-GitDynamicRest $git)) { return & $refuse "an argument to git $($git.Verb) known only at run time" }
        return
    }
    if ($name -eq 'dotnet' -and $Invocation.Argv.Count -ge 2) {
        $sub = $Invocation.Argv[1]
        if ($sub -in @('format', 'new', 'add', 'remove', 'sln')) { return & $refuse "dotnet $sub rewrites source or project files" }
    }
    # A puck verb, run as `puck <verb>` or `dotnet <copy>/Puck.Cli.dll <verb>`.
    $verbAt = if ($name -eq 'puck') { 1 } elseif ($name -eq 'dotnet' -and $Invocation.Argv.Count -ge 2 -and $Invocation.Argv[1] -match 'Puck\.Cli\.dll$') { 2 } else { -1 }
    if ($verbAt -gt 0 -and $Invocation.Argv.Count -gt $verbAt) {
        $verb = $Invocation.Argv[$verbAt]
        $checked = $Invocation.Argv -contains '--check'
        $generator = ($verb -in @('format', 'locks', 'schema', 'registry', 'vocabulary', 'baselines', 'branding', 'verbs')) -or ($verb -eq 'agents' -and $Invocation.Argv.Count -gt $verbAt + 1 -and $Invocation.Argv[$verbAt + 1] -eq 'link')
        if ($generator -and -not $checked) { return & $refuse "puck $verb without --check rewrites the files it owns" }
        return
    }
    $targets = Get-ReadOnlyWriteTargets $Invocation
    if ($null -eq $targets) { return }
    foreach ($target in @($targets)) {
        if (-not (Test-ReadOnlyWritable $target.Value $target.Dynamic $Invocation.Cwd)) { return & $refuse "$($Invocation.Name) writes $($target.Value)" }
    }
}
