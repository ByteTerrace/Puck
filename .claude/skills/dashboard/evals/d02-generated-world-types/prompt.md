---
description: "After a world-schema change the portal's TypeScript lacked the new field, and the plan was to hand-edit the generated world types instead of regenerating them with puck schema."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

I added a field to `WorldDefinition` in `Puck.World.Schema`, and now Puck.Dashboard's portal won't type-check: the world document type in `portal/src/document/worldDefinition.generated.ts` has no such field. I'm going to add the property to that file by hand so the build goes green. Is that right, and how will CI notice if the two drift?
