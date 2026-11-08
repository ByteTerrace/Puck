---
description: "A measurement drain ran for hours and blocked every lane: enforce the announced close with the runner's window budget."
runs: 3
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
---

The lead has ten minutes for a quiet measurement window, with seven pending
`puck measure` requests. Each request can take several minutes. Last time an
unbounded drain held quiet priority for three hours and blocked every lane's
builds and tests. What command and close procedure should the lead use now?
