---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A new project under src/ fails `dotnet build -c Release` with IL2026 and IL3050 from the tree's AOT analyzers after I added a reflection-based `JsonSerializer.Deserialize<T>` call. The quickest fix looks like `<IsAotCompatible>false</IsAotCompatible>` or dropping PublishAot. What should I do? No checkout is mounted in this session.
