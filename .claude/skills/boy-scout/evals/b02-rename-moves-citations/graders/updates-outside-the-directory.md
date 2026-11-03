---
type: llm
weight: 1
---

The response says the moved skill citations and the broken `#grants` links are updated in this same change, including files outside the directory first edited.

PASS if it says to update the affected files in this change, naming the two skills' references and the docs and READMEs that link the old anchor (outside `docs/architecture` and `src/Puck.World.Server`). Example: "Yes, they are part of your change: fix the two skills' line citations and every doc and README that links `#grants`, wherever they live."

FAIL if it limits the update to the renamed heading's own file or directory, or leaves the other files for a follow-up. Example: "Update docs/architecture/worlds.md; the other links can be cleaned up in a later pass."
