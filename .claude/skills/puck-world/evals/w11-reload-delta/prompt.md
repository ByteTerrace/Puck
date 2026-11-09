---
tags: [capability]
description: Debug-world reload exceeded the 16 MiB rebuild cap, and a live stdin driver waited for a simulation submission without queuing its immediate drain barrier.
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A look lane edits the valley palette in worlds/debug/valley.puck and reloads the running debug world. Explain what crosses the wire now, which existing systems own the diff and install, and what the server does if the base changes before apply. Plan a headless stdin edit/reload/world.row/restore proof with payload bytes. The driver keeps stdin open and waits for answers between edits. Explain the next allocation work without claiming the transport delta makes reload a presentation-only mutation. Do not run the check.
