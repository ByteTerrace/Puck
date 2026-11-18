---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the brief the reply drafts states a stopping condition for the lane: when it stops and hands back instead of continuing, such as a product call (what the engine should do) or an owner-only action; a look value is the lane's to decide, not a stopping condition.
Example PASS: "Stop and hand back `blocked` if the change needs a product call, or an owner-only action such as pushing `main`."

FAIL if the drafted brief has no stopping condition for the lane. A check the lead runs before sending the brief, or the `Brief: <id> blocked` trailer named only as a format, is not one.
Example FAIL: "If the ancestry check fails, the brief does not go out. Last commit: `Brief: <id> done` or `Brief: <id> blocked: <question>`."
