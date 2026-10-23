---
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

In the Puck repository, write the brief for a Codex review-and-fix pass over a lane's three commits that change capture readback in `RenderGraphRuntime` (`ReadbackBufferLease`, `CaptureReadbackEpoch`, `ReleaseAfterFence`) for plan `docs/plans/render-readback.md` step `R2`: `a1b2c3d render: lease the readback buffer until its copy completes`, `d4e5f6a render: key the readback epoch by frame`, `0718293 tests: pin the copied pixels`. The lane branch `lane/readback` branched from `c0ffee0` on `features/render` (verified ancestor; `features/render` has since advanced to `feedface`), and the lane's test run (`tests/Puck.Shaders.Tests`, 14 passed, 0 warnings) is green. The lead has decided the lane gets a pass because `CaptureReadbackEpoch` is persisted in the replay tape format. One decision is already taken: `ReadbackBufferLease` is released via `ReleaseAfterFence` only after its completion fence signals.
