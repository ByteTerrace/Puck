#Requires -Version 7.0

<#
.SYNOPSIS
Reminds an agent of Puck's writing rules before it edits a file whose prose those rules govern.

.DESCRIPTION
As a PreToolUse hook, classifies the edited file into a writing surface and injects that surface's rules,
quoted at run time from the documents that own them, so the reminder can never drift from its owner. Each
surface is injected once per session and agent; files outside the project and legal text get nothing.

As a SessionStart hook for a compaction, forgets which surfaces the session has seen, so the next edit
injects them again into the compacted context.

A quoted heading or bullet that no longer exists fails the hook with one line naming it.
#>

[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = (Resolve-Path -LiteralPath (Join-Path -Path $PSScriptRoot -ChildPath '../..')).Path
$markerRoot = Join-Path -Path ([IO.Path]::GetTempPath()) -ChildPath 'claude-prose-doctrine'

$contributing = 'docs/development/contributing.md'
$documentation = '.claude/skills/documentation/references/complete-reference.md'

$codeExtensions = @('.hlsl', '.hlsli', '.puck', '.ps1', '.ts')
$legalNames = @('LICENSE.md', 'THIRD-PARTY-NOTICES.md', 'CLA.md', 'PRIVACY.md')
$generatedPatterns = @(
    '^tests/Puck\.Maths\.Tests/(coverage-manifest\.json|leg-ledger\.md|RESULTS\.md|frontier\.json)$'
    '^docs/api/(api|_site)/'
)

# Returns the lines under a heading, up to the next heading of the same or a higher level.
function Get-Section {
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Heading
    )

    $lines = Get-Content -LiteralPath (Join-Path -Path $projectRoot -ChildPath $RelativePath) -Encoding utf8
    $start = [Array]::IndexOf($lines, $Heading)

    if ($start -lt 0) {
        throw "prose-doctrine: '$Heading' is no longer a heading in $RelativePath; update the hook to its new home."
    }

    $level = $Heading.IndexOf(' ')
    $end = $lines.Count

    for ($index = $start + 1; $index -lt $lines.Count; $index++) {
        if ($lines[$index] -match "^#{1,$level} ") {
            $end = $index
            break
        }
    }

    return ($lines[($start + 1)..($end - 1)] -join "`n").Trim().TrimEnd('-').Trim()
}

# Returns the bullet of a section whose text begins with the given opening.
function Get-Bullet {
    param(
        [Parameter(Mandatory)] [string] $RelativePath,
        [Parameter(Mandatory)] [string] $Heading,
        [Parameter(Mandatory)] [string] $Opening
    )

    $bullets = (Get-Section -RelativePath $RelativePath -Heading $Heading) -split "`n(?=- )"
    $match = $bullets | Where-Object -FilterScript { $_.StartsWith("- $Opening") }

    if ($null -eq $match) {
        throw "prose-doctrine: no bullet under '$Heading' in $RelativePath opens with '$Opening'; update the hook."
    }

    return $match
}

$passages = @{
    ApiDocs         = {
        Get-Bullet -RelativePath $contributing -Heading '## Code and documentation conventions' `
            -Opening 'Public APIs use XML documentation'
    }
    Comments        = {
        Get-Bullet -RelativePath $contributing -Heading '## Code and documentation conventions' `
            -Opening 'A comment earns its place'
    }
    ContentShape    = { Get-Section -RelativePath $documentation -Heading '### Content shape' }
    CurrentBehavior = {
        Get-Bullet -RelativePath $documentation -Heading '### Placement and scope rules' `
            -Opening '**Current references describe current behavior.**'
    }
    Evidence        = {
        Get-Bullet -RelativePath $documentation -Heading '### Placement and scope rules' `
            -Opening '**Separate current behavior from proposals and evidence.**'
    }
    Generated       = {
        Get-Bullet -RelativePath $documentation -Heading '### Placement and scope rules' `
            -Opening '**Machine-written artifacts are never hand-edited.**'
    }
    NoDates         = {
        Get-Bullet -RelativePath $documentation -Heading '### Placement and scope rules' `
            -Opening '**No dates and no commit SHAs.**'
    }
    Purge           = { Get-Section -RelativePath $documentation -Heading '### Prose that fails on every surface' }
    Voice           = { 'Voice, titles, and page shapes: docs/development/documentation.md.' }
    XmlRegister     = {
        "XML documentation register, from the documentation skill:`n`n" +
        (Get-Section -RelativePath $documentation -Heading '### The register, characterized')
    }
}

$surfaces = @{
    agent     = @{ Label = 'agent guidance'; Passages = @('Purge', 'ContentShape', 'CurrentBehavior', 'NoDates') }
    code      = @{ Label = 'source code'; Passages = @('ApiDocs', 'Comments') }
    csharp    = @{ Label = 'C# source'; Passages = @('ApiDocs', 'Comments', 'CurrentBehavior', 'XmlRegister') }
    generated = @{ Label = 'a machine-written artifact'; Passages = @('Generated') }
    human     = @{ Label = 'human documentation'; Passages = @('Purge', 'Evidence', 'NoDates', 'CurrentBehavior', 'Voice') }
}

function Get-Surface {
    param([Parameter(Mandatory)] [string] $RelativePath)

    $extension = [IO.Path]::GetExtension($RelativePath).ToLowerInvariant()

    if ($generatedPatterns | Where-Object -FilterScript { $RelativePath -match $_ }) {
        return 'generated'
    }

    if ($extension -in @('.cs', '.csx')) {
        return 'csharp'
    }

    if ($extension -in $codeExtensions) {
        return 'code'
    }

    if ($extension -ne '.md') {
        return $null
    }

    if (($RelativePath -in $legalNames) -or ($RelativePath -match '(^|/)NOTICE\.md$')) {
        return $null
    }

    if (($RelativePath -match '^\.claude/') -or ($RelativePath -in @('CLAUDE.md', 'AGENTS.md'))) {
        return 'agent'
    }

    return 'human'
}

# Decode stdin as UTF-8 explicitly; the console input encoding would mangle non-ASCII paths.
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

$payload = ConvertFrom-Json -InputObject $raw -AsHashtable
$sessionId = $payload['session_id']

if ([string]::IsNullOrWhiteSpace($sessionId)) {
    exit 0
}

$sessionMarkers = Join-Path -Path $markerRoot -ChildPath $sessionId

if ($payload['hook_event_name'] -eq 'SessionStart') {
    if ($payload['source'] -eq 'compact') {
        Remove-Item -LiteralPath $sessionMarkers -Recurse -Force -ErrorAction SilentlyContinue
    }

    exit 0
}

$file = $payload['tool_input']?['file_path']

if ($file -isnot [string]) {
    exit 0
}

$relativePath = [IO.Path]::GetRelativePath($projectRoot, [IO.Path]::GetFullPath($file)).Replace('\', '/')

if ($relativePath.StartsWith('../') -or [IO.Path]::IsPathRooted($relativePath)) {
    exit 0
}

$surface = Get-Surface -RelativePath $relativePath

if ($null -eq $surface) {
    exit 0
}

$agent = $payload['agent_id'] ?? 'main'
$marker = Join-Path -Path $sessionMarkers -ChildPath "$agent.$surface"

if (Test-Path -LiteralPath $marker) {
    exit 0
}

try {
    $rules = foreach ($name in $surfaces[$surface].Passages) {
        & $passages[$name]
    }
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}

$doctrine = @(
    "Puck writing rules for $($surfaces[$surface].Label), quoted from the documents that own them."
    'Shown once per session; the owners stay authoritative.'
    $rules
) -join "`n`n"

$null = New-Item -ItemType File -Path $marker -Force

Get-ChildItem -LiteralPath $markerRoot -Directory |
    Where-Object -FilterScript { $_.LastWriteTime -lt (Get-Date).AddDays(-7) } |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue

ConvertTo-Json -Compress -Depth 4 -EscapeHandling EscapeNonAscii -InputObject @{
    suppressOutput     = $true
    hookSpecificOutput = @{
        hookEventName     = 'PreToolUse'
        additionalContext = $doctrine
    }
}
