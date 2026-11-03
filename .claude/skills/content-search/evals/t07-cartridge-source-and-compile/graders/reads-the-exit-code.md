---
type: llm
weight: 1
---

The response tells the reader to check the search's exit code before trusting an empty result: exit 1 means no match and exit 2 means an error.

PASS if it names exit 1 as no match and exit 2 as an error (such as a bad path or flag), and says an empty result is not trusted until the code is known. Any search tool's exit codes count. Example: "Exit 1 with no output means no source matched; exit 2 is an error such as a bad path or flag, not an empty result."

FAIL if it reads empty output as no match without mentioning the exit code, or gets the codes wrong. Example: "If nothing prints, `litColumns` isn't in the sources."
