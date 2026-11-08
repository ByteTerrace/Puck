---
tags: [capability]
description: A collider report's OpensOnto field walks blocked the debug world's frame thread beyond its text deadline.
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

The full debug world's `world.debug-view report colliders` used to hang the host loop in `OpensOnto` and field reads. Explain the current bounded reporting mechanism, where live state may be read, and how you would verify that a short-deadline report leaves the next console line usable. No GPU grant; plan the check without running it.
