---
type: regex
weight: 1
match: contains
flags: i
---

commit[^\n]{0,60}\bWIP\b|\bWIP\b[^\n]{0,60}commit
