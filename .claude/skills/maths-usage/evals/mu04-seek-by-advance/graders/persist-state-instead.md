---
type: regex
weight: 1
match: contains
flags: i
---

(restor|resum|reload|load)\w*\b[^\n]{0,80}FromRawBits|FromRawBits[^\n]{0,40}\b(to\s+)?(restor|resum)
