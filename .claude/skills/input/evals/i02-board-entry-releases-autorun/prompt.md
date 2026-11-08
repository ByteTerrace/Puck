---
description: "Input continuity across a mode switch: entering a seated board must release held movement and autorun, and keeping an action is authored per transition, not engine policy (input-channel decision C3/C4)."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

In the Puck debug world a player has autorun latched on (the `forward` toggle on the pad's View page) and is holding run, then sits down at the chess board and later stands up again. Should autorun still be pushing her forward when she stands up? Where does Puck decide which held or toggled actions survive a change like that? And in my own world, how would I let a weapon swap keep a held `fire` going without the engine knowing what a weapon is?
