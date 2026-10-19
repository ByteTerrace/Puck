---
max_turns: 12
allowed_tools: [Read, Glob, Grep, Skill]
---

i want a 2cm cream stripe running around this helmet front to back, and a thin dark mouth line on the faceplate. my plan is to add a thin box for the stripe sitting 1mm proud of the helmet surface and a flattened capsule for the mouth. good plan? if not whats the right way, and show me the source.

```puck
schema: "puck.world.definition.v1"
documentId: "eval-helmet"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#5B6470", specular: 0.3, roughness: 0.4 }
      ]
      shape Superellipsoid "helmet" {
        material: 0
        position [0, 1.6, 0]
        scale [0.14, 0.16, 0.14]
        exponent: 3
      }
    }
  }
}
```
