# Emits Puck's comment and prose doctrine before an edit to a C# or Markdown file.
# Reads the hook payload on stdin; it does not search the repository.

$ErrorActionPreference = 'Stop'

$raw = [Console]::In.ReadToEnd()

if ([string]::IsNullOrWhiteSpace($raw)) { exit 0 }

$file = (ConvertFrom-Json $raw).tool_input.file_path

if ([string]::IsNullOrWhiteSpace($file)) { exit 0 }
if ($file -notmatch '\.(cs|md)$') { exit 0 }

$doctrine = @'
Puck comment and prose doctrine - applies to the file you are about to edit.

A comment, XML doc, README line, or skill line earns its place only by stating
something the code cannot state, that a reader could act on being wrong about.
If deleting it loses nothing recoverable from the code plus git log, delete it.

Never write: dates; provenance citations (plan, review, wave, session, or
owner-ruling references); CAPS for emphasis; past-tense defect narratives;
changelogs; restated design argument; test counts, line numbers, or inventories
that drift.

Do write: sign conventions and units; invariants; KEEP IN SYNC couplings;
layout and packing tables; external spec or hardware citations.

XML docs use the Microsoft .NET register: methods verb-first (Creates, Returns,
Gets), properties "Gets the ...", bool "Gets a value indicating whether ...".
Use <remarks> only for caller contracts.

Skills and docs carry procedural facts, not narrative. On a stale claim, delete
it rather than replacing it with a longer one. History belongs in git.

The control is: puck scan -Only comment-smells
'@

@{
    suppressOutput      = $true
    hookSpecificOutput  = @{
        hookEventName     = 'PreToolUse'
        additionalContext = $doctrine
    }
} | ConvertTo-Json -Depth 4 -Compress
