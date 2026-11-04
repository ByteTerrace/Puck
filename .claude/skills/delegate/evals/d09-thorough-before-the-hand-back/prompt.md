---
description: "delegate 'Build and run' and orchestration: coding lanes never build Puck.slnx, since concurrent whole-solution builds exhaust a machine's memory; a lane never runs the full worlds/debug sweep; and Claude Code reaps background jobs under memory pressure."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

I'm a delegated Puck lane. I changed `src/Puck.Rendering/Scopes/FieldScopeIndex.cs`, an internal type only `Puck.Rendering` uses, and one test in `tests/Puck.Rendering.Tests`. My brief names those two projects and the scenario `worlds/debug/scopes-tests.puck`, and grants no GPU leg. Before handing back I want to be thorough: build `Puck.slnx`, run the whole test suite and `puck test worlds/debug`, so I know nothing else broke. Give me the commands. No checkout is reachable from here; answer from what I describe.
