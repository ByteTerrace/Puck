#Requires -Version 7.0
# opaque-command: a form the classifier recognizes but cannot see into (a computed command name, -EncodedCommand,
# eval or Invoke-Expression of a variable, a shell reading its script from stdin) could run anything the other
# policies refuse, so it never passes silently: the lead is asked and a lane is refused.

function Test-OpaqueCommandPolicy {
    param([hashtable] $Context, $Invocation)
    if (-not $Invocation.Opaque) { return }
    $reason = "The command policies cannot see inside $($Invocation.Opaque): $($Invocation.Text). Write the command out literally."
    return New-PolicyRoleDecision $Context $reason $reason
}
