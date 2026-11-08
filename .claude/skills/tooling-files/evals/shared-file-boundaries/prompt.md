---
max_turns: 14
allowed_tools: [Read, Glob, Grep, Skill]
runs: 5
tags: [capability, regression]
---

This is a read-only design task in a Puck checkout. A CLI command stages several rewritten files before publishing
any, another initializes a lane stamp only when no writer has published one, and world staging removes its previous
output tree. Find the existing mechanisms each should use. State what enforces atomic publication, how a failed batch
cleans up its pending files, how initialization preserves a concurrent winner, and the deletion confinement invariant
and its enforcement point. Distinguish lexical containment from physical confinement. Do not implement a helper.
