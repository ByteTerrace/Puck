---
type: regex
weight: 1
match: contains
flags: i
---

TieredPGO\W{0,4}(=|is|set to|to)?\W{0,4}false|(dynamic|tiered)\s+PGO\W{0,4}(is\s+)?(off|disabled)|(disables?|turns? off)\W{0,4}(dynamic\s+|tiered\s+)?PGO
