---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

i've got two hull masses that meet in the middle and i wanted a recessed panel line where they join, so i built a thin box and subtracted it. it works but the seam doesn't follow the curve of the join properly and now i can't put panels on anything in this creation. heres the file (hull-seam.puck), prototype 'subject'. is there a better way to do this?

```puck
schema: "puck.world.definition.v1"
documentId: "eval-hull-seam"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#7C828A", specular: 0.25, roughness: 0.4 }
      ]
      shape Superellipsoid "hullLeft" {
        material: 0
        position [-0.2, 1.2, 0]
        scale [0.22, 0.14, 0.14]
        exponent: 3
      }
      shape Superellipsoid "hullRight" {
        material: 0
        position [0.2, 1.2, 0]
        scale [0.22, 0.14, 0.14]
        exponent: 3
      }
      shape Box "seamCutter" {
        material: 0
        blend: "Subtraction"
        position [0, 1.2, 0]
        scale [0.02, 0.16, 0.16]
      }
    }
  }
}
```
