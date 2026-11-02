---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply keeps the lane to the projects and scenario its brief names and recommends no `Puck.slnx` build, whole-suite run or full `worlds/debug` sweep. Saying whose job those are (the integration lane's or the lead's) is welcome but not required.
Example PASS: "Build the two projects and run `scopes-tests.puck`. The solution build and the debug-world sweep are the integration lane's, not a coding lane's."

FAIL if the reply gives commands for a `Puck.slnx` build, the whole suite or a full `worlds/debug` sweep, even as optional extra assurance.
Example FAIL: "If you have time, `dotnet build Puck.slnx` and `puck test worlds/debug` will catch anything the focused run misses."
