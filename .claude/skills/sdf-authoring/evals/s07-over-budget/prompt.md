---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

this thing wont load, i get 'prototypes[2] stamps 369 shapes, exceeding the 367-shape per-stamp budget'. i dont really want to lose any of the detail. heres the file (over-budget.puck), prototype 'subject'. what are my options and whats the cheapest fix?

```puck
schema: "puck.world.definition.v1"
documentId: "eval-over-budget"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#9AA0A6", specular: 0.2, roughness: 0.55 }
      ]
      shape Box "wall" {
        material: 0
        position [0, 1.0, 0]
        scale [2, 1, 0.05]
      }
      for i in range(0, 184) {
        shape Box $"stud{i}" {
          material: 0
          position [-1.98 + i * 0.0215, 1.0, 0.05]
          scale [0.008, 0.09, 0.008]
          panel {
            inset: 0.002
            depth: 0.002
            material: 0
          }
        }
      }
    }
  }
}
```
