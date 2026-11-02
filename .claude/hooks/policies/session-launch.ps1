#Requires -Version 7.0
# session-launch: starting another agent session. A lane does its brief itself and starts no session of its own
# (AGENTS.md: a lane that fans out hands back before its children finish), so a lane is refused every launch of
# `codex`, `agy`, `claude` and the Codex companion script (`codex-companion.mjs`), and every `puck evals run` (which
# starts `claude plugin eval` as its own child, out of this hook's sight), except the forms that only print help, a
# version, a listing or a dry-run plan. The lead may launch sessions; it is asked only before a Codex launch that changes its
# own approvals or sandbox, the same set Claude's settings ask on textually, here applied to every form the classifier
# sees (a moved or PowerShell launch, options before the subcommand).

$script:CodexConfigKeys = '^(approval_policy|sandbox_mode|default_permissions|permissions|approvals_reviewer|sandbox_workspace_write|shell_environment_policy|hooks)(\.|$)'

# The leading words of each agent CLI's forms that start no session.
$script:SessionInspectionForms = @{
    codex  = @('execpolicy', 'login status', 'mcp list', 'mcp get', 'completion', 'help', '--version', '-V', '--help', '-h')
    agy    = @('agent', 'agents', 'changelog', 'models', 'mcp list', 'plugin list', 'plugins list', 'help', '--version', 'version', '--help', '-h')
    claude = @('doctor', 'mcp list', 'mcp get', 'auth status', 'plugin list', 'plugins list', '--version', '-v', '--help', '-h')
}

# Returns the permission-changing option a codex call names, or $null.
function Get-CodexPermissionChange {
    param([string[]] $Argv)
    if ($Argv.Count -ge 2 -and $Argv[1] -in @('execpolicy', 'login', 'logout', 'completion', '--version', '-V', '--help', '-h')) { return $null }
    for ($j = 1; $j -lt $Argv.Count; $j++) {
        $t = $Argv[$j]
        if ($t -eq '--') { break }
        if ($t -in @('--dangerously-bypass-approvals-and-sandbox', '--yolo', '--full-auto')) { return $t }
        # The CLI takes a short option's value attached (-punsafe), after = (-p=unsafe) or as the next word, and a
        # long option's after = or as the next word.
        $name = $t
        $value = $null
        if ($t -cmatch '^(-[psca])=?(.+)$') { $name = $Matches[1]; $value = $Matches[2] }
        elseif ($t -cmatch '^(--[a-z-]+)=(.*)$') { $name = $Matches[1]; $value = $Matches[2] }
        if ($name -cin @('-p', '--profile')) { return '--profile' }
        if ($name -cin @('-a', '--ask-for-approval')) { return '--ask-for-approval' }
        if ($name -cin @('--add-dir')) { return '--add-dir' }
        if ($name -cnotin @('-s', '--sandbox', '-c', '--config')) { continue }
        if ($null -eq $value -and $j + 1 -lt $Argv.Count) { $value = $Argv[++$j] }
        if ($name -cin @('-s', '--sandbox') -and $value -eq 'danger-full-access') { return "--sandbox $value" }
        if ($name -cin @('-c', '--config') -and $value -and ($value -split '=', 2)[0].Trim() -match $script:CodexConfigKeys) { return "--config $value" }
    }
    return $null
}

# The words a puck invocation hands the CLI, with which of them are known only at run time, or $null: the executable
# (`puck`, `Puck.Cli`, a lane's copy at any path), `dotnet <path>/puck.dll` or `Puck.Cli.dll`, `dotnet exec` of either
# (world-devices owns the host options it skips), and `dotnet run` of the Puck.Cli project, named or implied by the
# working directory, after its `--`.
function Get-PuckWords {
    param($Invocation)
    $argv = @($Invocation.Argv)
    $dynamic = @($Invocation.Dynamic)
    $name = $Invocation.Name.ToLowerInvariant()
    $cliDll = '(^|[/\\])(puck|Puck\.Cli)\.dll$'
    $start = -1
    if ($name -in @('puck', 'puck.cli')) { $start = 1 }
    elseif ($name -eq 'dotnet' -and $argv.Count -ge 2) {
        if ($argv[1] -match $cliDll) { $start = 2 }
        elseif ($argv[1] -eq 'exec') {
            for ($j = 2; $j -lt $argv.Count; $j++) {
                if ($argv[$j] -in $script:DotnetExecValues) { $j++; continue }
                if ($argv[$j] -match $cliDll) { $start = $j + 1 }
                break
            }
        } elseif ($argv[1] -eq 'run') {
            $project = $null
            $separator = -1
            for ($j = 2; $j -lt $argv.Count; $j++) {
                if ($argv[$j] -eq '--') { $separator = $j; break }
                if ($argv[$j] -match '^--project=(.+)$') { $project = $Matches[1] }
                elseif ($argv[$j] -in @('--project', '-p') -and $j + 1 -lt $argv.Count) { $project = $argv[++$j] }
            }
            $cli = ($null -ne $project) ? ($project.Replace('\', '/').TrimEnd('/') -match '(^|/)Puck\.Cli(/Puck\.Cli\.csproj)?$|(^|/)Puck\.Cli\.csproj$') : [bool]($Invocation.Cwd -and (Test-Path -LiteralPath (Join-Path $Invocation.Cwd 'Puck.Cli.csproj')))
            if ($cli -and $separator -ge 0) { $start = $separator + 1 }
        }
    }
    if ($start -lt 0) { return $null }
    return @{ Words = @($argv | Select-Object -Skip $start); Dynamic = @($dynamic | Select-Object -Skip $start) }
}

# Returns the agent CLI an invocation starts (codex, agy, claude, the companion, or `puck evals run`), with the words
# after it, or $null.
function Get-SessionLaunch {
    param($Invocation)
    $name = $Invocation.Name.ToLowerInvariant()
    if ($name -in @('codex', 'agy', 'claude')) {
        return @{ Cli = $name; Rest = @($Invocation.Argv | Select-Object -Skip 1); Dynamic = @($Invocation.Dynamic | Select-Object -Skip 1) }
    }
    # The verb's first two words, read past any option before them; a word known only at run time could be either.
    if ($puck = Get-PuckWords $Invocation) {
        $verbs = @(for ($j = 0; $j -lt $puck.Words.Count; $j++) { if ($puck.Dynamic[$j] -or -not $puck.Words[$j].StartsWith('-')) { $j } })
        $launches = $verbs.Count -ge 2
        for ($k = 0; $launches -and $k -lt 2; $k++) {
            $launches = $puck.Dynamic[$verbs[$k]] -or $puck.Words[$verbs[$k]] -ceq @('evals', 'run')[$k]
        }
        if ($launches) { return @{ Cli = 'puck evals run'; Rest = $puck.Words; Dynamic = $puck.Dynamic } }
    }
    # node <...>/codex-companion.mjs <subcommand>: the script path may hold a variable, but its name is literal.
    if ($name -in @('node', 'bun', 'deno')) {
        for ($j = 1; $j -lt $Invocation.Argv.Count; $j++) {
            if ($Invocation.Argv[$j] -match 'codex-companion\.m?js$') { return @{ Cli = 'companion'; Rest = @(); Dynamic = @() } }
        }
    }
    return $null
}

# True when the words after an agent CLI only print help, a version or a listing.
function Test-SessionInspection {
    param([hashtable] $Launch)
    if ($Launch.Cli -eq 'companion') { return $false }
    $words = @($Launch.Rest)
    # puck evals run --dry-run prints its plan and command line and starts nothing.
    if ($Launch.Cli -eq 'puck evals run') {
        for ($j = 0; $j -lt $words.Count; $j++) {
            if (-not $Launch.Dynamic[$j] -and $words[$j] -cin @('--dry-run', '--help', '-h', '-?')) { return $true }
        }
        return $false
    }
    if (@($Launch.Dynamic | Where-Object { $_ }).Count) { return $false }
    if (-not $words.Count) { return $false }
    # A subcommand's help (`claude plugin eval --help`): subcommand-shaped words, then the help option last.
    if ($Launch.Cli -eq 'claude' -and $words[-1] -cin @('--help', '-h') -and -not @($words | Select-Object -SkipLast 1 | Where-Object { $_ -cnotmatch '^[a-z][a-z-]*$' }).Count) { return $true }
    foreach ($form in $script:SessionInspectionForms[$Launch.Cli]) {
        $parts = @($form -split ' ')
        if ($words.Count -lt $parts.Count) { continue }
        $matched = $true
        for ($k = 0; $k -lt $parts.Count; $k++) {
            if ($words[$k] -cne $parts[$k]) { $matched = $false; break }
        }
        if ($matched) { return $true }
    }
    return $false
}

function Test-SessionLaunchPolicy {
    param([hashtable] $Context, $Invocation)
    $launch = Get-SessionLaunch $Invocation
    if ($null -eq $launch) { return }
    if ($Context.Role -eq 'lane') {
        if (Test-SessionInspection $launch) { return }
        $what = ($launch.Cli -eq 'companion') ? 'the Codex companion' : $launch.Cli
        return New-PolicyDecision 'deny' "A lane starts no agent session of its own ($what): do the brief yourself, and hand back what needs another lane so the lead launches it. $($Invocation.Text)"
    }
    if ($launch.Cli -ne 'codex') { return }
    $change = Get-CodexPermissionChange $Invocation.Argv
    if ($null -eq $change) { return }
    return New-PolicyDecision 'ask' "This Codex launch changes its approvals or sandbox ($change): $($Invocation.Text)"
}
