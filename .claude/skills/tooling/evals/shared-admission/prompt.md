---
tags: [capability]
description: "GPU lease wrapper drifted from World launch, and standalone world-authoring startup omitted admission."
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Map how a new world-authoring executable should share host admission, GPU lease
ordering and compiler assembly traversal with the current CLI. Identify the
implementations to reuse, what they enforce, and where their guarantees stop.
Do not write code or run builds.
