---
type: llm
weight: 1
---

The response says to break the kernel with a one-line mutation inside an isolated worktree of its own (not a shared tree), run the law, and expect it to go red.

PASS if it says to mutate `FloorDivide` by one line in a worktree of your own (and not hand-revert files in a shared tree), run the law, and expect red. Example: "Mutate `FloorDivide` by one line in your own worktree, run only the law, and expect it to go red; never hand-revert in a shared tree."

FAIL if it mutates the shared tree directly with no isolation, or only adds operands and reruns. Example: "Edit `FloorDivide` in the main checkout, run the law, then `git checkout --` the file."
