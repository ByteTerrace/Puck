---
description: "A reviewer wanted a byte-reproducible video of a scripted run from capture.start's default document plus a microphone row; the default wall clock is not reproducible and the sim clock refuses audio rows."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I need a video of a scripted Puck debug-world run whose file comes out byte-for-byte the same every time, so reviewers can diff two runs. My plan is `capture.start` with the shipped default recording document, plus a microphone audio row so I can narrate. Will that give a reproducible file? If not, what should the recording document say, and what are the trade-offs?
