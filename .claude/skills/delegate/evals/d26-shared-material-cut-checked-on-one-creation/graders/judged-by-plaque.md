---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply says the exhibits the change reaches are judged against their plaques (the `note:` text on the exhibit, or `worlds/debug/shapes.md`), meaning what each exhibit says it demonstrates, and not against matching an earlier capture or earlier bytes.
Example PASS: "Check each of those exhibits against what its plaque says to look for; an exhibit that reads as its plaque says passes even if its pixels changed."

FAIL if the reply judges the other exhibits only by whether they match older captures or hashes, or names no judging standard for them.
Example FAIL: "Diff the exhibits against last week's captures; if nothing changed you are done."
