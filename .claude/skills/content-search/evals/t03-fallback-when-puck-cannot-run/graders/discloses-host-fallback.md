---
type: llm
weight: 1
---

The response gives host-tool commands (grep, ripgrep, git grep, Select-String or similar) for the search and names at least one way their results can differ from a `puck search` run.

PASS if it names at least one such difference or limitation of how the host commands search: which files they read (tracked only, ignored or build output, hidden files), binary or encoding handling, glob rules, the pattern dialect, or that they match per line. Example: "These use `git grep` and `Select-String` instead of `puck search`; `git grep` reads tracked files only, and `Select-String` also reads `bin/` and `obj/`."

FAIL if it gives host commands and presents them as the complete or equivalent answer, with no limitation named. Example: "Run `grep -rni grant src | grep -vi refusal`; that's the full answer."
