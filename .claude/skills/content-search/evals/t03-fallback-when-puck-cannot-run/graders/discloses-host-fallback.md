---
type: llm
weight: 1
---

The response says its commands use the host's search tool (grep, ripgrep, Select-String or similar) in place of the repository's `puck search` verb, and names at least one way their results can differ from what `puck search` would return (which files the walk reads, ignored or generated files, glob rules, or the pattern dialect).

PASS example: "`puck search` can't run without the SDK, so this falls back to ripgrep. Its walk skips gitignored files and its globs differ from Puck's, so treat the result as a degraded stand-in."

FAIL if it gives host commands without saying they replace `puck search`, or presents them as equivalent to a `puck search` run. Example: "Run `grep -rni grant src | grep -vi refusal`; that's the full answer."
