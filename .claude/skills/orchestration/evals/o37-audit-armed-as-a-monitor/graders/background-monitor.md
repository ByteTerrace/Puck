---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the audit runs as a background monitor that reports while the lead is busy, not as a session cron or loop that fires only when the lead is idle.
Example PASS: "Arm it as a background monitor so it reports while I am busy."

FAIL if the reply arms it as a cron or loop that fires only when the lead is idle.
Example FAIL: "Schedule a recurring session cron that checks every hour."
