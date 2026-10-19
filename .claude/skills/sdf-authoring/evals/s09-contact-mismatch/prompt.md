---
max_turns: 10
allowed_tools: [Read, Glob, Grep, Skill]
---

i made an arched kerb stone with a Path prism profile, flat bottom and a round top, and the world refuses to load as soon as i mark the placement solid. i want it to be a real obstacle that things bump into. heres the file (contact-mismatch.puck), prototype 'subject'. how do i keep it close to that shape and still have collision?

```puck
schema: "puck.world.definition.v1"
documentId: "eval-contact-mismatch"

placements {
  placement "kerb" {
    prototype: "subject"
    position [2, 0, 2]
    solid
  }
}

prototypes {
  prototype "subject" {
    document {
      schema: "puck.creation.v1"
      name: "subject"
      palette [
        { color: "#8A8F96", specular: 0.15, roughness: 0.7 }
      ]
      shape Prism "kerb" {
        material: 0
        position [0, 0.3, 0]
        scale [0.6, 0.3, 0.2]
        profile {
          kind: "Path"
          cornerRadius: 0
          path {
            tolerance: 0.001
            contours [
              {
                start [-0.5, -0.5]
                segments [
                  {
                    end [0.5, -0.5]
                  }
                  {
                    end [0.5, 0]
                  }
                  {
                    end [-0.5, 0]
                    arcCenter [0, 0]
                  }
                  {
                    end [-0.5, -0.5]
                  }
                ]
              }
            ]
          }
        }
      }
    }
  }
}
```
