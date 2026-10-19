---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

this refuses to load and the error is pointing at my panel: 'prototypes[2].doc.shapes[2].panel: the creation already needs its own field scope (a non-Union shape blend, an engraved text run, or a noise facet elsewhere in it), whose shared shape emission does not support panels; remove the panel or the scope-forcing facet.' but the panel looks fine to me and theres no text or noise in this thing at all. heres the file (subtraction-trap.puck), prototype 'subject'. what is it actually complaining about?

```puck
schema: "puck.world.definition.v1"
documentId: "eval-subtraction-trap"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#7E8892", specular: 0.25, roughness: 0.4 }
      ]
      shape Box "hull" {
        material: 0
        position [0, 1.0, 0]
        scale [0.3, 0.16, 0.16]
      }
      shape Cylinder "bore" {
        material: 0
        blend: "Subtraction"
        position [0, 1.0, 0]
        rotation [0.7071068, 0, 0, 0.7071068]
        scale [0.05, 0.05, 0.2]
      }
      shape Box "hatch" {
        material: 0
        position [0, 1.0, 0.16]
        scale [0.08, 0.06, 0.01]
        panel {
          inset: 0.003
          depth: 0.002
          material: 0
        }
      }
    }
  }
}
```
