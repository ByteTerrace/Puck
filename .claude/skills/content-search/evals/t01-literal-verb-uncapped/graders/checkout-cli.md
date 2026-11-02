---
type: regex
weight: 1
match: contains
flags: i
---

dotnet\s+(?!["']?src/Puck\.Cli/bin)["']?[^\s"']*Puck\.Cli\.dll["']?\s+search\b|dotnet\s+["']?\$\{?\w+\}?["']?\s+search\b
