#Requires -Version 7.0

<#
.SYNOPSIS
Moves an at-risk Bash tool command into a script file before it runs.

.DESCRIPTION
On Windows the Bash tool hands the command to bash.exe as one command-line argument, which truncates it past
~8 KB and halves every doubled backslash. The hook payload arrives on stdin intact, so an at-risk command is
written verbatim to a script and replaced with a `.` of that script, which runs in the same shell. Any other
command passes through untouched.

Claude matches its permission rules against the rewritten input and shows that input in the prompt, so a moved
command's ask rules see only the `.` line. Before rewriting, the shared classifier reads the original command;
when it finds a git push, a codex exec, a Codex launch that changes its permissions, a form it cannot see into, or
anything git-safety would refuse, the hook asks and its reason quotes those invocations whole.
#>

[CmdletBinding()]
param()

. "$PSScriptRoot/command-policy.ps1"

$maxDirectLength = 2000
$maxReasonLength = 2000
$transportDirectory = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath 'claude-bash-transport'
$staleBefore = (Get-Date).AddDays(-1)

$raw = Read-PolicyStdin
if ([string]::IsNullOrWhiteSpace($raw)) {
    exit 0
}

$payload = ConvertFrom-Json -InputObject $raw -AsHashtable
$toolInput = $payload['tool_input']
$command = ${toolInput}?['command']

if ($command -isnot [string]) {
    exit 0
}

if (($command.Length -le $maxDirectLength) -and (-not $command.Contains('\\'))) {
    exit 0
}

# Returns the invocations of a moved command that its ask rules would have seen.
function Get-TransportAskingInvocations {
    param([hashtable] $Context)
    foreach ($invocation in Get-PolicyInvocations $Context.Command 'posix' $Context.Cwd) {
        $asks = [bool]$invocation.Opaque
        if ($invocation.Name -eq 'git') {
            $git = Resolve-GitAlias (Split-GitInvocation $invocation)
            $asks = $asks -or $git.ContainsKey('Shell') -or $git.Verb -eq 'push'
        }
        if ($invocation.Name -eq 'codex') {
            $asks = $asks -or $invocation.Argv -contains 'exec' -or ($invocation.Argv.Count -gt 1 -and $invocation.Argv[1] -eq 'e')
            $asks = $asks -or $null -ne (Test-CodexLaunchPolicy $Context $invocation)
        }
        $asks = $asks -or $null -ne (Test-GitSafetyPolicy $Context $invocation)
        if ($asks) { $invocation }
    }
}

$null = [IO.Directory]::CreateDirectory($transportDirectory)

Get-ChildItem -LiteralPath $transportDirectory -File |
    Where-Object -FilterScript { $_.LastWriteTime -lt $staleBefore } |
    Remove-Item -ErrorAction SilentlyContinue

$digest = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($command))
$name = [Convert]::ToHexString($digest, 0, 8).ToLowerInvariant()
$scriptPath = (Join-Path -Path $transportDirectory -ChildPath "$name.sh").Replace('\', '/')
$body = $command.EndsWith("`n") ? $command : ($command + "`n")

[IO.File]::WriteAllText($scriptPath, $body)

$context = New-PolicyContext $payload 'claude'
$asking = @(Get-TransportAskingInvocations $context | ForEach-Object Text | Select-Object -Unique)

$toolInput['command'] = ". '$scriptPath'"

$output = @{
    hookEventName = 'PreToolUse'
    updatedInput  = $toolInput
}

if ($asking.Count) {
    $output['permissionDecision'] = 'ask'
    # Whole invocations while they fit; one that alone passes the limit is cut.
    $quoted = [Text.StringBuilder]::new()
    foreach ($text in $asking) {
        if ($quoted.Length + $text.Length + 1 -gt $maxReasonLength) {
            if ($quoted.Length -eq 0) { [void]$quoted.Append($text.Substring(0, $maxReasonLength)).Append('...') }
            else { [void]$quoted.Append("(and $($asking.Count - [Array]::IndexOf($asking, $text)) more)") }
            break
        }
        [void]$quoted.Append($text).Append("`n")
    }
    $output['permissionDecisionReason'] = "$scriptPath runs:`n" + $quoted.ToString().TrimEnd()
}

# Escape non-ASCII so the reply survives whatever encoding the console output uses.
ConvertTo-Json -Compress -Depth 4 -EscapeHandling EscapeNonAscii -InputObject @{ hookSpecificOutput = $output }
