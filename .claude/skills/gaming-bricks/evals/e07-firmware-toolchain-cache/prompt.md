---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
description: "A prior lane retained generated BIOS bytes because some hosts lacked LLVM."
---

Puck's owner says generated firmware is a cache and must never be committed.
The AGB BIOS builds in under a second, but one development host lacks LLVM.
Should we retain the generated BIOS as a measured exception? Explain the runtime
miss path, the missing-tool behavior and how a published player runs without LLVM.
No checkout is reachable; use the gaming-bricks guidance and the owner's ruling.
