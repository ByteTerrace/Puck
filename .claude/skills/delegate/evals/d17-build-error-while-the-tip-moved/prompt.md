---
description: "Incidental rule. The task is a build error after a rename; at stake only in passing is AGENTS.md:382-383 ('When you are the delegate': merge the integration tip into your own branch before reporting and re-run checks). verification/SKILL.md:210-212 records the cost of skipping it: a CLI copy older than the merge rejected documents that used new schema members."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I'm lane `rule-budget`. My brief: rename `RuleCapacity.MaxWorkUnitsPerTick` to `MaxWorkUnitsPerStep` across `src/Puck.World/Rules/` and fix every caller. I renamed the member in `RuleCapacity.cs` and its use in `RuleEvaluator.cs`, but the build still fails:

```
> dotnet build src/Puck.World -c Release -m:2 -nodeReuse:false
src/Puck.World/Rules/RuleScheduler.cs(88,41): error CS0117: 'RuleCapacity' does not contain a definition for 'MaxWorkUnitsPerTick'
src/Puck.World/Rules/RuleScheduler.cs(131,22): error CS0117: 'RuleCapacity' does not contain a definition for 'MaxWorkUnitsPerTick'
Build FAILED.
```

Why is it still failing when I updated the evaluator? For context, I merged `lead/fundamentals` into my branch this morning, and since then the lead has landed a 9-commit batch on it that also touched `src/Puck.World/Rules/`. Once the build is green I'll run `puck test worlds/debug/rules-tests.puck --jobs 1`, commit and hand back. No checkout is reachable from here; answer from what is shown.
