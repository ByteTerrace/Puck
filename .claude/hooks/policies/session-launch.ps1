#Requires -Version 7.0
# session-launch: starting another agent session. A lane does its brief itself and starts no session of its own
# (AGENTS.md: a lane that fans out hands back before its children finish), so a lane is refused every launch of
# `codex`, `agy`, `claude` and the Codex companion script (`codex-companion.mjs`), except the forms that only print
# help, a version or a listing. The lead may launch sessions; it is asked only before a Codex launch that changes its
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

# Returns the agent CLI an invocation starts (codex, agy, claude or the companion), with the words after it, or $null.
function Get-SessionLaunch {
    param($Invocation)
    $name = $Invocation.Name.ToLowerInvariant()
    if ($name -in @('codex', 'agy', 'claude')) {
        return @{ Cli = $name; Rest = @($Invocation.Argv | Select-Object -Skip 1); Dynamic = @($Invocation.Dynamic | Select-Object -Skip 1) }
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
    if (@($Launch.Dynamic | Where-Object { $_ }).Count) { return $false }
    $words = @($Launch.Rest)
    if (-not $words.Count) { return $false }
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
