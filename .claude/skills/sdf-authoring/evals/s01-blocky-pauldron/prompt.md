---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

this shoulder pauldron i blocked out is three stacked boxes and it reads exactly like three stacked boxes. i want it to look like a real curved armor plate - a shell that swells over the shoulder and tapers down toward the arm, with proper broken edges. heres the file (blocky-pauldron.puck), prototype id is 'subject'. can you sculpt it properly? give me the edited source.

```puck
schema: "puck.world.definition.v1"
documentId: "eval-blocky-pauldron"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#8A8F94", specular: 0.3, roughness: 0.5 }
      ]
      shape Box "plateLower" {
        material: 0
        position [0, 1.0, 0]
        scale [0.22, 0.09, 0.14]
      }
      shape Box "plateMid" {
        material: 0
        position [0, 1.12, 0]
        scale [0.24, 0.10, 0.15]
      }
      shape Box "plateUpper" {
        material: 0
        position [0, 1.25, 0]
        scale [0.20, 0.08, 0.13]
      }
    }
  }
}
```
