---
type: llm
weight: 1
---

The response decides the tap and the hold with the authored input-pattern matcher, not with a second hold detector.

PASS if the mechanism it proposes for telling the tap from the hold is the input patterns (or the rule operands that read them), with no new timer, tick counter, wall clock or binding-layer state added for the decision. Example: "Author two `inputPatterns` rows, a press-then-release tap and a hold, matched by the existing pattern matcher; no new detector."

FAIL if it proposes deciding the hold from the binding layer's chord or modifier state (`BindingChordTracker`, `HeldOrderTracker`), a binding activator, a new per-seat tick count or a wall-clock timer. Example: "Use BindingChordTracker's held set and count the seat's ticks since the press; at 30 ticks open the menu."
