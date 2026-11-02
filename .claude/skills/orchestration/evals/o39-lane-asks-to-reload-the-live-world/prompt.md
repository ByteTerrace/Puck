---
description: "orchestration 'The Claude roster': the live World runs the main checkout's build, so a lane sees its edit in a World of its own named by `world` on every call, and a brief names the live mutations a lane may make."
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

As the lead of Puck's lanes: a lane working in its own worktree edited a shader and asks to call `world.reload` on the live World through the `puck` MCP server, to see its edit running. The owner is playing in that World right now. What do you answer?
