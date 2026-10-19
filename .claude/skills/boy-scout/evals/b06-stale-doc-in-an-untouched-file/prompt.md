---
max_turns: 12
allowed_tools: [Read, Glob, Grep, Skill]
---

I'm partway through a change in the Puck repository. You can't see the checkout, so answer from the facts below: I'm fixing a bug in src/Puck.Cli.Source/Search/SearchScanner.cs. While reading, I notice docs/development/ci.md line 88 still names a CLI flag that was retired, a one-line fix. My change does not touch ci.md or anything that cites it. Do I fix that line in this change?
