---
type: llm
weight: 1
---

The response says the change that implements the ready bits also updates the places the limitation is written down: the hardware reference's note about the gap and the open item that tracks it.

PASS if it says that in the same change the recorded limitation is updated or closed (the note is rewritten, the open item deleted or closed). Example: "In the same change, update the hardware reference that describes the gap and close its open-items entry."

FAIL if it leaves the recorded limitation for later, does not mention it, or only says to add a new entry recording a limitation that the fix removes. Example: "Once the stage passes, you're done."
