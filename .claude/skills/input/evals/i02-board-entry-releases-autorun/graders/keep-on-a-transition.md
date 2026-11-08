---
type: regex
weight: 1
match: contains
flags: i
---

transitions?\b[^\n]{0,80}\bkeep\b|\bkeep\b[^\n]{0,80}\btransitions?\b
