---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply's change leaves `MicrophoneCapture` and `VideoTapeEncoder` in place: it consists of the reader consolidation (callers moved to `ReplayTapeReader`, `TapeReader` deleted) and deletes neither capture class.
Example PASS: "The change is the reader merge only. I'm not deleting `MicrophoneCapture` or `VideoTapeEncoder`."

FAIL if the reply still deletes either class in this change, or plans to delete them in a follow-up commit of its own.
Example FAIL: "The change merges the readers and removes the two dead capture classes the owner approved for deletion."
