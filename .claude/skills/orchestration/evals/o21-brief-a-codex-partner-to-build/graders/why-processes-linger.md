---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says why node reuse is off: a reused MSBuild node outlives the build and keeps holding memory on the shared machine.
Example PASS: "`-nodeReuse:false`, because a reused node lingers after the build and holds memory other lanes need."

FAIL if the reply gives no reason, or a reason that leaves out the lingering node's memory.
Example FAIL: "Pass `-nodeReuse:false` so other builds cannot attach to your nodes."
