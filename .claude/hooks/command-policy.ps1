#Requires -Version 7.0
# The command-policy library behind agent-policy.ps1: decodes one hook payload for Claude Code, Codex or Antigravity,
# names the caller's role, runs every policy under policies/ over the classified invocations, and formats the one
# reply. Dot-sourced, never registered.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. "$PSScriptRoot/invocations.ps1"
foreach ($file in Get-ChildItem -LiteralPath "$PSScriptRoot/policies" -Filter '*.ps1') { . $file.FullName }

# Policy name to the function that checks one invocation and returns @{ Decision; Reason } or
# nothing. A decision is deny or ask; a policy with nothing to say abstains, so the tool's own rules decide.
$script:PolicyChecks = [ordered]@{
    'git-safety'     = 'Test-GitSafetyPolicy'
    'codex-launch'   = 'Test-CodexLaunchPolicy'
    'no-coauthor'    = 'Test-NoCoauthorPolicy'
    'no-node-reuse'  = 'Test-NoNodeReusePolicy'
    'world-devices'  = 'Test-WorldDevicesPolicy'
    'opaque-command' = 'Test-OpaqueCommandPolicy'
}

function Read-PolicyStdin {
    # Decode stdin as UTF-8 explicitly; the console input encoding would mangle non-ASCII text.
    $reader = [IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.UTF8Encoding]::new($false))
    try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
}

# The effective working directory before the command's own cd or Set-Location: the tool call's directory, then the
# session's, then Antigravity's workspace. Antigravity runs its hooks from <checkout>/.agents.
function Get-PolicyCwd {
    param([hashtable] $Payload, $InputData)
    $session = $Payload['cwd']
    $base = if ([string]::IsNullOrEmpty($session)) { (Get-Location).Path } else { $session }
    $workspaces = @($Payload['workspacePaths'] | Where-Object { $_ })
    foreach ($candidate in @(${InputData}?['workdir'], ${InputData}?['Cwd'], $session, ($workspaces.Count ? $workspaces[0] : $null))) {
        if ($candidate -is [string] -and $candidate.Length) { return Resolve-PolicyPath $candidate $base }
    }
    $here = (Get-Location).Path
    if ((Split-Path -Leaf $here) -eq '.agents') { return Split-Path -Parent $here }
    return $here
}

# Returns @{ Tool; Role; Dialect; Command; Cwd; Payload }, or $null for a payload that carries no command.
# A lane is a Claude subagent (its payload carries agent_id), a Codex session or an Antigravity session; the Claude
# session with no agent_id is the lead.
function New-PolicyContext {
    param([hashtable] $Payload, [string] $Tool)
    if ([string]::IsNullOrEmpty($Tool)) {
        $Tool = if ($Payload.ContainsKey('toolCall')) { 'agy' } elseif ($Payload.ContainsKey('turn_id')) { 'codex' } else { 'claude' }
    }
    $inputData = $Payload['tool_input'] ?? $Payload['toolCall']?['args']
    if ($inputData -is [string]) { $inputData = ConvertFrom-Json -InputObject $inputData -AsHashtable }
    if ($inputData -isnot [hashtable]) { return $null }
    $command = $inputData['command'] ?? $inputData['cmd'] ?? $inputData['CommandLine']
    if ($command -is [array]) { $command = [string[]]@($command) }
    elseif ($command -isnot [string]) { return $null }
    $toolName = $Payload['tool_name'] ?? $Payload['toolCall']?['name']
    $role = if ($Tool -eq 'claude' -and [string]::IsNullOrEmpty($Payload['agent_id'])) { 'lead' } else { 'lane' }
    # A Claude tool names its own shell, and a Codex exec_command may name one in tool_input.shell. Otherwise the shell
    # is unknown (PowerShell on Windows by default, but not always), and the command is read both ways.
    $dialect = if ($Tool -eq 'claude') { ($toolName -eq 'PowerShell') ? 'powershell' : 'posix' } else { 'both' }
    if ($Tool -ne 'claude' -and $inputData['shell'] -is [string]) {
        $shell = (Get-PolicyExecutable $inputData['shell']).ToLowerInvariant()
        if ($shell -in $script:PolicyPosixShells) { $dialect = 'posix' }
        elseif ($shell -in $script:PolicyPwshShells) { $dialect = 'powershell' }
    }
    return @{
        Tool    = $Tool
        Role    = $role
        Dialect = $dialect
        Command = $command
        Cwd     = Get-PolicyCwd $Payload $inputData
        Payload = $Payload
    }
}

function New-PolicyDecision {
    param([ValidateSet('deny', 'ask')] [string] $Decision, [string] $Reason)
    return @{ Decision = $Decision; Reason = $Reason }
}

# The lead is asked where a lane is refused: unknown, main-reaching and permission-changing forms.
function New-PolicyRoleDecision {
    param([hashtable] $Context, [string] $LaneReason, [string] $LeadReason)
    if ($Context.Role -eq 'lane') { return New-PolicyDecision 'deny' $LaneReason }
    return New-PolicyDecision 'ask' $LeadReason
}

function Get-PolicyDecisions {
    param([hashtable] $Context)
    $invocations = @(Get-PolicyInvocations $Context.Command $Context.Dialect $Context.Cwd)
    if ($Context.Dialect -eq 'both' -and $Context.Command -is [string]) {
        # The stricter of the two readings: every invocation the POSIX reading finds counts too, except its opaque
        # ones, since a PowerShell variable or assignment reads as a computed command name there.
        $invocations += @(Get-PolicyInvocations $Context.Command 'posix' $Context.Cwd | Where-Object { -not $_.Opaque })
    }
    foreach ($name in $script:PolicyChecks.Keys) {
        foreach ($invocation in $invocations) {
            $decision = & $script:PolicyChecks[$name] $Context $invocation
            if ($null -ne $decision) { $decision }
        }
    }
}

# Combines decisions deny over ask over abstain and formats the one reply, or returns $null to abstain.
function Format-PolicyReply {
    param([hashtable] $Context, [object[]] $Decisions)
    $Decisions = @($Decisions | Where-Object { $null -ne $_ })
    if (-not $Decisions.Count) { return $null }
    $decision = if (@($Decisions | Where-Object { $_.Decision -eq 'deny' }).Count) { 'deny' } else { 'ask' }
    $reason = (@($Decisions | Where-Object { $_.Decision -eq $decision } | ForEach-Object Reason | Select-Object -Unique) -join "`n")
    # Only the Claude lead is ever asked; a lane's tool could not reach the owner with a prompt.
    if ($Context.Tool -ne 'claude') { $decision = 'deny' }
    if ($Context.Tool -eq 'agy') {
        return ConvertTo-Json -Compress -EscapeHandling EscapeNonAscii -InputObject @{ decision = $decision; reason = $reason }
    }
    return ConvertTo-Json -Compress -Depth 4 -EscapeHandling EscapeNonAscii -InputObject @{
        hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = $decision; permissionDecisionReason = $reason }
    }
}

function Invoke-AgentPolicy {
    param([hashtable] $Payload, [string] $Tool)
    $context = New-PolicyContext $Payload $Tool
    if ($null -eq $context) { return $null }
    try {
        $decisions = @(Get-PolicyDecisions $context)
    } catch {
        $message = "The command policies failed on this command ($($_.Exception.Message)); fix .claude/hooks before relying on them."
        $decisions = @(New-PolicyRoleDecision $context $message $message)
    }
    return Format-PolicyReply $context $decisions
}

# --- git ---------------------------------------------------------------------------------------------------------

$script:GitSeparateValues = @('-C', '-c', '--git-dir', '--work-tree', '--namespace', '--config-env')
$script:GitRedirects = @('-C', '-c', '--git-dir', '--work-tree', '--namespace', '--config-env')
$script:GitRedirectVariables = @('GIT_DIR', 'GIT_WORK_TREE', 'GIT_COMMON_DIR', 'GIT_INDEX_FILE', 'GIT_NAMESPACE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'GIT_CONFIG', 'GIT_CONFIG_PARAMETERS', 'GIT_CONFIG_COUNT', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_SYSTEM')
# git never lets an alias shadow one of these, so only another verb is looked up as an alias.
$script:GitBuiltins = @(
    'add', 'am', 'annotate', 'apply', 'archive', 'backfill', 'bisect', 'blame', 'branch', 'bugreport', 'bundle', 'cat-file',
    'check-attr', 'check-ignore', 'check-mailmap', 'check-ref-format', 'checkout', 'checkout-index', 'cherry', 'cherry-pick',
    'citool', 'clean', 'clone', 'column', 'commit', 'commit-graph', 'commit-tree', 'config', 'count-objects', 'credential',
    'describe', 'diagnose', 'diff', 'diff-files', 'diff-index', 'diff-tree', 'difftool', 'fast-export', 'fast-import',
    'fetch', 'fetch-pack', 'filter-branch', 'filter-repo', 'for-each-ref', 'for-each-repo', 'format-patch', 'fsck', 'gc',
    'grep', 'gui', 'hash-object', 'help', 'hook', 'index-pack', 'init', 'interpret-trailers', 'lfs', 'log', 'ls-files',
    'ls-remote', 'ls-tree', 'mailinfo', 'mailsplit', 'maintenance', 'merge', 'merge-base', 'merge-file', 'merge-tree',
    'mergetool', 'mktag', 'mktree', 'multi-pack-index', 'mv', 'name-rev', 'notes', 'pack-objects', 'pack-refs', 'patch-id',
    'prune', 'prune-packed', 'pull', 'push', 'range-diff', 'read-tree', 'rebase', 'reflog', 'refs', 'remote', 'repack',
    'replace', 'replay', 'request-pull', 'rerere', 'reset', 'restore', 'rev-list', 'rev-parse', 'revert', 'rm',
    'send-email', 'shortlog', 'show', 'show-branch', 'show-index', 'show-ref', 'sparse-checkout', 'stage', 'stash',
    'status', 'stripspace', 'submodule', 'switch', 'symbolic-ref', 'tag', 'unpack-objects', 'update-index', 'update-ref',
    'update-server-info', 'var', 'verify-commit', 'verify-pack', 'verify-tag', 'version', 'whatchanged', 'worktree',
    'write-tree'
)

# Runs git for a policy's own read (never a candidate command): @{ Code; Out; Err }.
function Invoke-PolicyGit {
    param([string] $Cwd, [string[]] $Globals, [string[]] $Arguments)
    $start = [Diagnostics.ProcessStartInfo]::new('git')
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $before = @()
    if ($Cwd -and (Test-Path -LiteralPath $Cwd -PathType Container)) { $before = @('-C', $Cwd) }
    foreach ($argument in @($before) + @($Globals) + @($Arguments)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $out = $process.StandardOutput.ReadToEndAsync()
        $err = $process.StandardError.ReadToEnd()
        $process.WaitForExit()
        return @{ Code = $process.ExitCode; Out = $out.Result; Err = $err }
    } finally { $process.Dispose() }
}

# Splits a git invocation at its verb: @{ Verb; Rest; RestDynamic (per Rest word, known only at run time); Cwd
# (after -C); Globals (to replay on a policy's own read); Config (-c pairs, keys lowercased); Redirected (it names
# another tree or overrides configuration); Unresolved (what before the verb, the verb included, is known only at
# run time, or $null) }.
function Split-GitInvocation {
    param($Invocation)
    $argv = $Invocation.Argv
    $flags = $Invocation.Dynamic
    $dynamic = { param([int] $k) $null -ne $flags -and $k -lt $flags.Count -and $flags[$k] }
    $git = @{ Verb = $null; Rest = @(); RestDynamic = @(); Cwd = $Invocation.Cwd; Globals = [Collections.Generic.List[string]]::new(); Config = @{}; Redirected = $false; Unresolved = $null; UnresolvedValue = $null }
    foreach ($key in $Invocation.Env.Keys) { if ($key -in $script:GitRedirectVariables) { $git.Redirected = $true } }
    for ($i = 1; $i -lt $argv.Count; $i++) {
        $t = $argv[$i]
        if (-not $t.StartsWith('-') -or $t -eq '-') {
            $git.Verb = $t
            if (& $dynamic $i) { $git.Unresolved = 'the git subcommand' }
            $git.Rest = [string[]]@($argv | Select-Object -Skip ($i + 1))
            $git.RestDynamic = [bool[]]@(for ($k = $i + 1; $k -lt $argv.Count; $k++) { [bool](& $dynamic $k) })
            break
        }
        if (& $dynamic $i) { $git.Unresolved = 'a git option' }
        $name = $t
        $value = $null
        if ($t -cmatch '^-C(.+)$') { $name = '-C'; $value = $Matches[1] }
        elseif ($t -cmatch '^-c(.+)$') { $name = '-c'; $value = $Matches[1] }
        elseif ($t -match '^(--[a-z-]+)=(.*)$') { $name = $Matches[1]; $value = $Matches[2] }
        elseif ($t -cin $script:GitSeparateValues -and $i + 1 -lt $argv.Count) {
            $value = $argv[++$i]
            if (& $dynamic $i) { $git.UnresolvedValue = "the value of git $t" }
        }
        if ($name -cin $script:GitRedirects) { $git.Redirected = $true }
        if ($name -ceq '-C') { $git.Cwd = Resolve-PolicyPath $value $git.Cwd }
        elseif ($name -ceq '-c' -and $null -ne $value) {
            $pair = $value -split '=', 2
            $git.Config[$pair[0].ToLowerInvariant()] = ($pair.Count -gt 1) ? $pair[1] : 'true'
            $git.Globals.Add('-c'); $git.Globals.Add($value)
        } elseif ($name -in @('--git-dir', '--work-tree', '--namespace') -and $null -ne $value) { $git.Globals.Add("$name=$value") }
    }
    return $git
}

# Replaces an alias verb with its expansion: a hashtable for a git alias, @{ Shell = text } for a `!` alias, or the
# input unchanged.
function Resolve-GitAlias {
    param([hashtable] $Git)
    for ($depth = 0; $depth -lt 4; $depth++) {
        if ($null -eq $Git.Verb -or $Git.Verb -cin $script:GitBuiltins) { return $Git }
        $key = "alias.$($Git.Verb.ToLowerInvariant())"
        $alias = $Git.Config[$key]
        if ($null -eq $alias) {
            $read = Invoke-PolicyGit $Git.Cwd $Git.Globals @('config', '--get', $key)
            if ($read.Code -ne 0) { return $Git }
            $alias = $read.Out.Trim()
        }
        if ($alias.StartsWith('!')) { return @{ Shell = $alias.Substring(1); Rest = $Git.Rest; RestDynamic = $Git.RestDynamic; Cwd = $Git.Cwd } }
        $words = @(Split-PolicyWords $alias)
        if (-not $words.Count) { return $Git }
        $expanded = $Git.Clone()
        $expanded.Verb = $words[0]
        $expanded.Rest = [string[]](@($words | Select-Object -Skip 1) + @($Git.Rest))
        $expanded.RestDynamic = [bool[]](@($words | Select-Object -Skip 1 | ForEach-Object { $false }) + @($Git.RestDynamic))
        $Git = $expanded
    }
    return $Git
}

# True when a word after the verb is known only at run time, so it could be any option or operand. -Options stops
# at a literal `--`, past which a verb takes no options.
function Test-GitDynamicRest {
    param([hashtable] $Git, [switch] $Options)
    for ($k = 0; $k -lt $Git.Rest.Count; $k++) {
        if ($Git.RestDynamic[$k]) { return $true }
        if ($Options -and $Git.Rest[$k] -eq '--') { return $false }
    }
    return $false
}

# True when an option list names one of $Flags as a short option (combined clusters too, up to the first option that
# takes the rest of its cluster or the next word as its value) or one of $Long. Stops at `--`.
function Test-GitOption {
    param([string[]] $Rest, [string] $Flags, [string] $Values = '', [string[]] $Long = @(), [string[]] $LongValues = @())
    for ($i = 0; $i -lt $Rest.Count; $i++) {
        $t = $Rest[$i]
        if ($t -eq '--') { return $false }
        if ($t.StartsWith('--')) {
            $name = ($t -split '=', 2)[0]
            if ($name -cin $Long) { return $true }
            if ($name -cin $LongValues -and -not $t.Contains('=')) { $i++ }
            continue
        }
        if ($t.Length -lt 2 -or -not $t.StartsWith('-')) { continue }
        for ($k = 1; $k -lt $t.Length; $k++) {
            if ($Flags.Contains($t[$k])) { return $true }
            if ($Values.Contains($t[$k])) {
                if ($k -eq $t.Length - 1) { $i++ }
                break
            }
        }
    }
    return $false
}

# Returns the operands of an option list, skipping the values of the options that take one.
function Get-GitOperands {
    param([string[]] $Rest, [string] $Values = '', [string[]] $LongValues = @())
    $operands = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt $Rest.Count; $i++) {
        $t = $Rest[$i]
        if ($t -eq '--') { $operands.AddRange([string[]]@($Rest | Select-Object -Skip ($i + 1))); break }
        if ($t.StartsWith('--')) {
            if (($t -split '=', 2)[0] -cin $LongValues -and -not $t.Contains('=')) { $i++ }
            continue
        }
        if ($t.Length -ge 2 -and $t.StartsWith('-')) {
            for ($k = 1; $k -lt $t.Length; $k++) {
                if ($Values.Contains($t[$k])) {
                    if ($k -eq $t.Length - 1) { $i++ }
                    break
                }
            }
            continue
        }
        $operands.Add($t)
    }
    return $operands.ToArray()
}
