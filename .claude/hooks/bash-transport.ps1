#Requires -Version 7.0

<#
.SYNOPSIS
Moves an at-risk Bash tool command into a script file before it runs.

.DESCRIPTION
On Windows the Bash tool hands the command to bash.exe as one command-line argument, which truncates it past
~8 KB and halves every doubled backslash (anthropics/claude-code#85111, #92543, #93915). The hook payload
arrives on stdin intact, so an at-risk command is written verbatim to a script and replaced with a `.` of that
script, which runs in the same shell. Any other command passes through untouched.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$maxDirectLength = 2000
$transportDirectory = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath 'claude-bash-transport'
$staleBefore = (Get-Date).AddDays(-1)

# Decode stdin as UTF-8 explicitly; the console input encoding would mangle non-ASCII text.
$stdin = [Console]::OpenStandardInput()
$reader = [IO.StreamReader]::new($stdin, [Text.UTF8Encoding]::new($false))

try {
    $raw = $reader.ReadToEnd()
} finally {
    $reader.Dispose()
}

if ([string]::IsNullOrWhiteSpace($raw)) {
    exit 0
}

$toolInput = (ConvertFrom-Json -InputObject $raw -AsHashtable)['tool_input']
$command = ${toolInput}?['command']

if ($command -isnot [string]) {
    exit 0
}

if (($command.Length -le $maxDirectLength) -and (-not $command.Contains('\\'))) {
    exit 0
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

$toolInput['command'] = ". '$scriptPath'"

# Escape non-ASCII so the reply survives whatever encoding the console output uses.
ConvertTo-Json -Compress -Depth 4 -EscapeHandling EscapeNonAscii -InputObject @{
    hookSpecificOutput = @{
        hookEventName = 'PreToolUse'
        updatedInput  = $toolInput
    }
}
