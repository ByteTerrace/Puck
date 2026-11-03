---
type: llm
weight: 1
---

The response writes no brace set (`{cs,md}`) or character class inside a glob it asks the reader to run.

PASS if every glob in its commands is a plain `*`, `?` or `**` pattern, with separate flags for separate extensions, or it passes no glob at all. Example: "`-g "*.cs" -g "*.md"`."

FAIL if any glob it tells the reader to run contains a brace set or a character class. Example: "`-g "*.{cs,md}"`."
