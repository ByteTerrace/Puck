---
description: "orchestration 'Schedule machines': an agent stops only the processes it started, by recorded PID, never by a command-line pattern; lanes whose runs failed during such a kill re-run their legs alone."
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, a partner reports that its cleanup step killed 24 processes on the shared box by filtering on its scratchpad path, and it cannot say what they all were. Two other lanes reported failed test runs in the same minutes. What do you do?
