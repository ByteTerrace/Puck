---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

To enforce "every consumer is handed a `Foo`", a law scans the source for calls that omit the `foo:` argument using a regular expression. A reviewer shows a real call site the regular expression cannot see, so deleting the argument there escapes the law. How do you fix this?
