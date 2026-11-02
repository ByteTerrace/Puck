---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

I am a delegated Puck lane and my change is finished: three files under `src/Puck.World/Rules/`. The index also holds files a generator staged that are not mine and that I must not commit or lose. I was going to load my three files into a temporary index with `git read-tree`, write the commit with `git commit-tree`, and move my branch to it with `git update-ref`, so the real index stays untouched. Is that the right way to make my final commit here?
