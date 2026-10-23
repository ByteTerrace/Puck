---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

When verifying a C# change in the Puck repository (`tests/Puck.World.Tests`), a test law enforces "every consumer is handed a `Foo`" by scanning C# source files for calls that omit the `foo:` argument using a regular expression. During verification, a reviewer shows a real C# call site the regular expression cannot see, so deleting the argument there escapes the law. Under Puck's verification rules, how do you fix this?
