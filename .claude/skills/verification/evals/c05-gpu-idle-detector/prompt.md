---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Before a granted GPU canary run you wrote a small detector that polls the process list for the game executable and waits until none is running. It never reports idle, although `ps` in another terminal shows no game process. What is going on and how do you fix the detector?
