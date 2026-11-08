---
type: llm
weight: 1
---

The response waits on a high-resolution waitable timer and rejects both proposed options.

PASS if it recommends a high-resolution waitable timer (created with the high-resolution flag) that wakes near its due time without raising process-wide timer resolution and without spinning, and rejects both `timeBeginPeriod` and the stopwatch spin. Example: "Use Puck's `IPrecisionWaiter`; on Windows it is a waitable timer created with `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`, which wakes within about half a millisecond with no `timeBeginPeriod` and no spin. A headless host registers it with `AddWindowsPrecisionWaiter`."

FAIL if it picks either proposed option, or recommends a spin or a global timer-resolution change in any form. Example: "Spin for the last 2 ms; it costs a little CPU but is the most precise."
