---
type: llm
weight: 1
---

The response has the store record what it overwrites into the firing's journal scope before each write, so the arena's rewind restores it.

PASS if it says the store records the previous value through the arena's journal (`ArenaJournal.RecordParticipant` or equivalent wording) before it writes, inside the firing's open scope, and that a rewind of that scope hands the records back to the store to undo. Example: "Implement `IJournalParticipant`; before each tally write call `RecordParticipant` with the old value, and when the firing rewinds, the arena hands the records back newest first."

FAIL if it keeps the per-firing copy-and-restore plan, adds a separate undo stack or rewinds the dictionary by its own logic outside the journal. Example: "Snapshot the dictionary when the firing opens and restore it in the refusal branch."
