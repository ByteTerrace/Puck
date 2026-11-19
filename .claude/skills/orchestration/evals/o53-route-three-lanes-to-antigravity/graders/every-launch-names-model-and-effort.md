---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says each of the three launches names its model and its effort level explicitly (`--model` and `--effort`) so that Antigravity's home default never decides a lane, and gives an effort for each lane.
Example PASS: "Every launch carries `--model gemini-4-<name> --effort <level>`: carbon at high, neon at medium, hydrogen at low."

FAIL if the reply names only a model and no effort, relies on the signed-in home default for any lane, or gives no launch flags.
Example FAIL: "`agy -p --model gemini-4-neon` for each; the default effort is fine."
