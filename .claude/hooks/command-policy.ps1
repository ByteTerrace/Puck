#Requires -Version 7.0
# Shared transport for the three command policies. Dot-sourced, never registered as a hook.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-PolicyInput {
    $reader = [IO.StreamReader]::new([Console]::OpenStandardInput(), [Text.UTF8Encoding]::new($false))
    try { $raw = $reader.ReadToEnd() } finally { $reader.Dispose() }
    if ([string]::IsNullOrWhiteSpace($raw)) { return $null }
    $payload = ConvertFrom-Json -InputObject $raw -AsHashtable
    $inputData = $payload['tool_input'] ?? $payload['toolCall']?['args']
    if ($inputData -is [string]) { $inputData = ConvertFrom-Json -InputObject $inputData -AsHashtable }
    $command = ${inputData}?['command'] ?? ${inputData}?['cmd'] ?? ${inputData}?['CommandLine']
    if ($command -is [array]) {
        $command = ($command | ForEach-Object { "'" + $_.Replace("'", "''") + "'" }) -join ' '
    }
    if ($command -isnot [string]) { return $null }
    return @{ Payload = $payload; Command = $command }
}

function Deny-PolicyCommand {
    param($InputData, [string] $Reason)
    if ($InputData.Payload.ContainsKey('toolCall')) {
        @{ decision = 'deny'; reason = $Reason } | ConvertTo-Json -Compress
    } else {
        @{ hookSpecificOutput = @{ hookEventName = 'PreToolUse'; permissionDecision = 'deny'; permissionDecisionReason = $Reason } } |
            ConvertTo-Json -Depth 4 -Compress
    }
    exit 0
}

function Complete-PolicyCommand {
    param($InputData)
    # Claude and Codex abstain; Antigravity requires an explicit decision.
    if ($null -ne $InputData -and $InputData.Payload.ContainsKey('toolCall')) { '{"decision":"allow"}' }
    exit 0
}

function Get-PolicyCommands {
    param([string] $Command)
    # Preserve quoted separators and newlines in messages, but separate actual shell commands.
    $tokens = [Collections.Generic.List[string]]::new()
    $token = [Text.StringBuilder]::new()
    $quote = [char]0
    for ($i = 0; $i -lt $Command.Length; $i++) {
        $c = $Command[$i]
        if ($quote -ne [char]0) {
            if ($c -eq $quote) {
                if ($i + 1 -lt $Command.Length -and $Command[$i + 1] -eq $quote) { [void]$token.Append($c); $i++ }
                else { $quote = [char]0 }
            } elseif (($c -eq '`' -or $c -eq '\') -and $quote -eq '"' -and $i + 1 -lt $Command.Length -and $Command[$i + 1] -eq '"') {
                [void]$token.Append('"'); $i++
            } else { [void]$token.Append($c) }
            continue
        }
        if ($c -eq "'" -or $c -eq '"') { $quote = $c; continue }
        if ($c -eq '#' -and $token.Length -eq 0) {
            while ($i + 1 -lt $Command.Length -and $Command[$i + 1] -ne "`n") { $i++ }
            continue
        }
        if (($c -eq '`' -or $c -eq '\') -and $i + 1 -lt $Command.Length -and $Command[$i + 1] -in @("`r", "`n")) { $i++; continue }
        if ([char]::IsWhiteSpace($c) -or $c -in @(';', '&', '|')) {
            if ($token.Length) { $tokens.Add($token.ToString()); [void]$token.Clear() }
            if ($c -in @(';', '&', '|', "`n", "`r") -and $tokens.Count) {
                ,$tokens.ToArray(); $tokens.Clear()
            }
        } else { [void]$token.Append($c) }
    }
    if ($token.Length) { $tokens.Add($token.ToString()) }
    if ($tokens.Count) { ,$tokens.ToArray() }
}

function Get-PolicyExecutable {
    param([string] $Token)
    return ($Token.Replace('\', '/') -split '/')[-1] -replace '\.exe$', ''
}

function Test-PolicyExecutablePosition {
    param([string[]] $Tokens, [int] $Index)
    if ($Index -eq 0) { return $true }
    if ((Get-PolicyExecutable $Tokens[0]) -in @('env', 'sudo', 'nohup', 'exec', 'command')) { return $true }
    # The lease forwards an executable after its own -- separator.
    return $Tokens[$Index - 1] -eq '--' -and $Tokens -contains 'lease'
}

function Get-PolicyInvocations {
    param([string] $Command, [int] $Depth = 0)
    foreach ($tokens in Get-PolicyCommands -Command $Command) {
        ,$tokens
        if ($Depth -ge 4) { continue }
        for ($i = 0; $i -lt $tokens.Count - 1; $i++) {
            if ((Get-PolicyExecutable $tokens[$i]) -in @('bash', 'sh', 'pwsh', 'powershell', 'cmd')) {
                for ($j = $i + 1; $j -lt $tokens.Count - 1; $j++) {
                    if ($tokens[$j] -in @('-c', '-lc', '-Command', '/c')) {
                        Get-PolicyInvocations -Command $tokens[$j + 1] -Depth ($Depth + 1)
                        break
                    }
                }
            }
        }
    }
}
