---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

the armor on this bust looks like cheap flat plastic and the eyes are just two black spheres, its lifeless. i want the shell to read like automotive painted metal with a clearcoat on it, and the eyes to actually look like eyes. heres the file (flat-plastic.puck), prototype 'subject'. what do i change?

```puck
schema: "puck.world.definition.v1"
documentId: "eval-flat-plastic"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#6E7580", specular: 0, roughness: 0.6 }
        { color: "#0A0A0A", specular: 0, roughness: 0.6 }
      ]
      shape Superellipsoid "head" {
        material: 0
        position [0, 1.6, 0]
        scale [0.14, 0.16, 0.14]
        exponent: 3
      }
      shape Sphere "eyeLeft" {
        material: 1
        position [0.05, 1.62, 0.125]
        scale [0.018, 0.018, 0.018]
      }
      shape Sphere "eyeRight" {
        material: 1
        position [-0.05, 1.62, 0.125]
        scale [0.018, 0.018, 0.018]
      }
    }
  }
}
```
