---
description: "A gameplay gesture placed on the host's physical binding layer (a tapped binding activator) instead of the authority's input history and patterns, which rules read and replay reproduces."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I'm adding a fighting-game special move to a Puck world: a quarter-circle forward on the stick (down, down-forward, forward) followed by the attack button within a quarter of a second, and it has to read correctly when the character faces left. `Puck.Commands` already has `BindingActivatorDefinition` with a `Tapped` mode and `TimeoutTicks`, which detects ordered tap sequences in a binding row. Should I build the move on that? If not, what should I use, and how does a world rule fire the move exactly once per input?
