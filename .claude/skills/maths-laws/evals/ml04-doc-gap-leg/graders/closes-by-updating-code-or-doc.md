---
type: llm
weight: 1
---

The response records the mismatch in the authored structural leg, and says the gap closes by correcting the code or the XML documentation and updating that leg.

PASS if it says the leg stays until either the code or the XML documentation is corrected, and then the leg is updated or removed in that change. Example: "Record it in a structural leg with a `DOC GAP` citation; when the doc or the code is corrected, update the leg in the same change."

FAIL if it records the gap with no way to close it, or says to fix neither and leave it recorded forever. Example: "Add a waiver and move on; nobody needs to revisit it."
