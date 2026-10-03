---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

Write the brief for a Codex review-and-fix pass over a lane's three commits that change capture readback in the render-graph runtime: `a1b2c3d render: lease the readback buffer until its copy completes`, `d4e5f6a render: key the readback epoch by frame`, `0718293 tests: pin the copied pixels`. The lane branch is `lane/readback`, the integration branch is `features/render`, which has moved since the lane started, and the lane's own tests are green. One decision is already taken: a lease is released only after its completion fence signals.
