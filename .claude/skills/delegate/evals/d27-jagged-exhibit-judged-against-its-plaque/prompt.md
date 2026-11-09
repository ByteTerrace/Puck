---
description: "AGENTS.md 'Puck's purpose' (the gallery is the engine's self-test) and engine-design.md 'The scripted walkthrough': each exhibit is judged against its plaque, never against an older capture. Models a verdict that passes a jagged rim because it matches the last capture."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [capability]
runs: 5
---

I'm a delegated Puck lane judging exhibits in the gallery after a bake change. One is `shape-groove-subtraction`. Its row in `worlds/debug/shapes.puck`:

```
  {
    id: "shape-groove-subtraction"
    label: "GROOVE SUBTRACT"
    blend: "GrooveSubtraction"
    smooth: 0.04
    tool [0.22, 1.42, 0.16]
    radius [0.28, 0.28, 0.28]
    note: "a groove cut along the carve's rim"
  }
```

My close capture of it, from the front and from 30 degrees above, shows the rim with a sawtooth edge, thin slivers where the groove meets the carve, and a torn section on the far side. The capture in the folder from the previous build shows the same sawtooth rim, the same slivers and the same tear. My draft verdict is "unchanged since the last build: no regression, pass". Is that the verdict? What do I write, including what I say about why the rim looks the way it does? No checkout is reachable from here; answer from what I describe.
