---
type: llm
weight: 1
---

The response describes a search for leftover uses of the renamed concept and of its old heading anchor.

PASS if it searches for the old name (such as `grant`, `Grants` or `GrantsView`) and for the old anchor (`#grants` or `## Grants`), by any search tool or link checker. Example: "Run `puck search -M 0 -l` for `grant`, `Grants`, `#grants` and `GrantsView` across the repository."

FAIL if it gives no search, or searches only for the new name, or searches the concept but never the anchor or heading. Example: "Update the links you know about and check the build."
