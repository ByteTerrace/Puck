---
type: llm
weight: 1
---

The response says the oracle's reference arithmetic must not call the `Puck.Maths` member or kernel under test.

PASS if it says the oracle is independent arithmetic (for example `BigInteger`) that never calls the helper, its siblings or the shared kernel. Example: "The oracle in `Oracles.cs` computes the exact answer with `BigInteger` and must not call `FixedQ4816` or its kernels, or the law proves nothing."

FAIL if it has the oracle call the helper under test or a kernel it shares, or does not say the oracle must be independent. Example: "In the oracle, call the new helper and compare it with the plain operator."
