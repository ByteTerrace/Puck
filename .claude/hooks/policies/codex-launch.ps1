#Requires -Version 7.0
# codex-launch: a Codex launch that changes its own approvals or sandbox. Claude's settings allow `codex exec`
# and ask on these forms textually; this check applies the same set to every form the classifier sees (a moved or
# PowerShell launch, options before the subcommand), asking the lead and refusing a lane.

$script:CodexConfigKeys = '^(approval_policy|sandbox_mode|default_permissions|permissions|approvals_reviewer|sandbox_workspace_write|shell_environment_policy|hooks)(\.|$)'

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

function Test-CodexLaunchPolicy {
    param([hashtable] $Context, $Invocation)
    if ($Invocation.Name -ne 'codex') { return }
    $change = Get-CodexPermissionChange $Invocation.Argv
    if ($null -eq $change) { return }
    return New-PolicyRoleDecision $Context "A lane starts no Codex session, least of all one that changes its approvals or sandbox ($change)." "This Codex launch changes its approvals or sandbox ($change): $($Invocation.Text)"
}
