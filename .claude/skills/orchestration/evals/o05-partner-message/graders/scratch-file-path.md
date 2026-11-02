---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the long analysis goes in a lane-named file (under the worktree's `.claude/scratch/`, or a shared lane-named file) and the message carries that file's path rather than the analysis itself.
Example PASS: "Write it to `<worktree>/.claude/scratch/render-readback-analysis.md` and send `[render-readback] progress: analysis at <path>`."

FAIL if the reply pastes the long analysis into the message, or gives no place for it but the message.
Example FAIL: "Send the whole analysis in the message body so the partner has it all in one place."
