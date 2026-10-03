---
type: regex
weight: 1
match: contains
flags: i
---

original[^.\n]{0,30}(failure |error )?message|first[^.\n]{0,30}(failure|error) message
