---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says to read the change at the named revisions (`git diff 4f1c2e0 9a7d3b1`, `git show 9a7d3b1:<path>`) and not from the working tree or the current HEAD, because later batches have changed those files since.
Example PASS: "Don't open the working-tree file; two batches landed after `rt-cull`. Read `git show 9a7d3b1:src/Puck.Rendering/Culling/HzbPass.cs`."

FAIL if the reply says to read the working-tree files or the current HEAD, or checks out the head commit in the lead's checkout.
Example FAIL: "Open `HzbPass.cs` in the working tree and compare it with the diff."
