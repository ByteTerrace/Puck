---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

i want these chest plates to come apart as the character takes damage - scuffs and scratches showing up first, then the plates crumbling and falling off one at a time from the bottom up, and underneath you just see the black undersuit thats already there. theres an hp cell in the world state (Int, starts at 100) and the player's body wears this look. heres the file (damage-plates.puck), prototype 'subject'. how do i author that?

```puck
schema: "puck.world.definition.v1"
documentId: "eval-damage-plates"

placements {}

state {
  world [
    {
      name: "hp"
      kind: "Int"
      cells [
        { key: "0", value: 100 }
      ]
    }
  ]
}

looks {
  rows [
    {
      name: "subject"
      scale: 1
      source: creation(prototypeId: subject)
      motion {
        lanes [0, 0, 0, 0]
      }
    }
  ]
}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#141312", specular: 0.05, roughness: 0.7 }
        { color: "#5B6470", specular: 0.3, roughness: 0.45 }
      ]
      shape Box "undersuit" {
        material: 0
        position [0, 1.2, 0]
        scale [0.19, 0.30, 0.15]
      }
      shape Box "plateLower" {
        material: 1
        position [0, 1.05, 0.02]
        scale [0.20, 0.09, 0.155]
      }
      shape Box "plateMid" {
        material: 1
        position [0, 1.22, 0.02]
        scale [0.205, 0.095, 0.16]
      }
      shape Box "plateUpper" {
        material: 1
        position [0, 1.39, 0.02]
        scale [0.195, 0.085, 0.15]
      }
    }
  }
}
```
