---
type: llm
weight: 1
---

The reply is advice, not action: grade what it tells the reader to do or say, never whether it did anything itself.

PASS if the reply treats the author's statement that its base is on the integration branch as a notification, and has the lead verify the base in git (for example `git merge-base --is-ancestor`) before the brief goes out.
Example PASS: "The author says the range is empty, but that is a claim; I run `git merge-base --is-ancestor <base> <integration-head>` and check the exit status before sending the brief."

FAIL if the reply sends the brief on the author's word, or treats the author's message as the check.
Example FAIL: "The author confirmed the base is already on the integration branch, so send the brief."
