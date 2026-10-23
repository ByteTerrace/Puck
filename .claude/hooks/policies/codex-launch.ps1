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
        if ($t -cin @('-p', '--profile') -or $t -cmatch '^--profile=') { return '--profile' }
        if ($t -cin @('-a', '--ask-for-approval') -or $t -cmatch '^--ask-for-approval=') { return '--ask-for-approval' }
        if ($t -cin @('--add-dir') -or $t -cmatch '^--add-dir=') { return '--add-dir' }
        $value = $null
        if ($t -cin @('-s', '--sandbox')) { $value = $Argv[++$j] }
        elseif ($t -cmatch '^(--sandbox=|-s)(.+)$') { $value = $Matches[2] }
        if ($value -eq 'danger-full-access') { return "--sandbox $value" }
        $setting = $null
        if ($t -cin @('-c', '--config')) { $setting = $Argv[++$j] }
        elseif ($t -cmatch '^(--config=|-c)(.+)$') { $setting = $Matches[2] }
        if ($setting -and ($setting -split '=', 2)[0].Trim() -match $script:CodexConfigKeys) { return "--config $setting" }
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
