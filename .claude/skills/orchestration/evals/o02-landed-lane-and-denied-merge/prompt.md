---
description: "orchestration 'Launch capabilities': the lead runs any merge an agent is denied, and generated-file conflicts are resolved by running their generators (`puck schema`, `puck locks`), never by hand."
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
tags: [regression]
---

As the lead of Puck's lanes, one lane lands while two other local lanes remain open. An agent is denied a merge, and another reports conflicts in the generated world schema files and `packages.lock.json`. How do you proceed?
