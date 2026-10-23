#Requires -Version 7.0

<#
.SYNOPSIS
The one PreToolUse command-policy hook, registered once per tool.

.DESCRIPTION
Reads one hook payload from stdin (Claude Code's or Codex's tool_input, Antigravity's toolCall.args), classifies
the command into the invocations it would start (invocations.ps1), runs every policy under policies/ over them,
and emits one reply: deny over ask over abstain. A command no policy speaks to gets no output, so the tool's own
permission rules decide it.

-Tool names the registering tool (claude, codex or agy); without it the payload's shape decides. -Only limits the
run to the named policies.
#>

[CmdletBinding()]
param(
    [ValidateSet('claude', 'codex', 'agy')] [string] $Tool,
    [string[]] $Only
)

. "$PSScriptRoot/command-policy.ps1"

$raw = Read-PolicyStdin
if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }
$reply = Invoke-AgentPolicy (ConvertFrom-Json -InputObject $raw -AsHashtable) $Tool $Only
if ($reply) { [Console]::Out.Write($reply) }
exit 0
