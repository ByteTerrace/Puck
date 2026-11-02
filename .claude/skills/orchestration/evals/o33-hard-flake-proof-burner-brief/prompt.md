---
description: "orchestration 'Schedule machines': a CPU burner for a flake proof may run beside another lane's GPU leg, and a leg that times out under it is re-run once alone and reported as a flake."
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes, you are briefing a partner to prove that a canary timeout is a flake: it will re-run the canary while a CPU burner loads the box, and it works on the shared GPU machine where other lanes run GPU legs. What must the brief say about the burner?
