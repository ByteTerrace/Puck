---
type: llm
weight: 1
---

The response gives a runnable way to make the law go red against a broken `FloorDivide` (a plausible one-line mutation run against that law, or `puck laws prove` with a real fix where it can select the row) and requires the failure to come from the assertion the law makes, not from a crash, a build error or another leg.

PASS example: "Truncate instead of flooring, run only that law with `--filter-display-name '<law-id>*'`, and check it fails at the intended assertion; a failure anywhere else doesn't count."

FAIL if it only reruns the law, adds operands, or counts any red result as proof. Example: "Add more random operands and run it again; if it still passes, it's solid."
