---
type: llm
weight: 1
---

The reply is advice, not action: grade the procedure it recommends.

PASS if it uses `puck measure run --window-seconds` with a budget no greater
than 600 seconds, allows for process cleanup within the announced close,
explains that expiry stops active sampling and releases quiet priority with
incomplete requests left pending, and sends the close when work finishes.

FAIL if it relies only on per-sample timeout, an announcement or a manual
reminder to bound an otherwise unbounded drain, or keeps draining after the
announced close because requests remain.
