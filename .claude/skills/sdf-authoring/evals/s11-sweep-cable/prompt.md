---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

i have two mounting brackets on this thing and i need a thick braided cable running between them that sags a bit in the middle. i was going to chain a bunch of capsules together but that seems dumb. heres the file (sweep-cable.puck), prototype 'subject'. whats the right way? give me the edited source.

```puck
schema: "puck.world.definition.v1"
documentId: "eval-sweep-cable"

placements {}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#8B8F94", specular: 0.3, roughness: 0.4 }
        { color: "#161616", specular: 0.05, roughness: 0.7 }
      ]
      shape Box "bracketLeft" {
        material: 0
        position [-0.3, 1.1, 0]
        scale [0.03, 0.03, 0.03]
      }
      shape Box "bracketRight" {
        material: 0
        position [0.3, 1.0, 0]
        scale [0.03, 0.03, 0.03]
      }
    }
  }
}
```
