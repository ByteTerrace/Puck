---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

A test creates a scratch git repository under the temp directory, makes a few commits, and deletes it. While it ran in parallel with other work, another worktree on the machine lost files in the middle of a test. Nothing in the test touches that worktree. What is the likely cause and what do you change?
