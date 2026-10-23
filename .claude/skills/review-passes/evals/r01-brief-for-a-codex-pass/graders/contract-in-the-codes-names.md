---
type: llm
weight: 1
---

The response's brief states the contract using the code's names from the prompt (`RenderGraphRuntime`, `ReadbackBufferLease`, `CaptureReadbackEpoch`, `ReleaseAfterFence`, or explicit placeholders where a symbol was not given) and states the decision already taken (a lease is released only after its completion fence signals).
