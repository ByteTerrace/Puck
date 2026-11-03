---
type: llm
weight: 1
---

The response says nearby READMEs, skills and comments are not edited when the audit finds nothing stale, and answers the question 'should I edit them all' with no.

PASS if it says not to edit nearby docs to show they were checked, and to change a doc only if the audit finds it stale. Example: "No: a behavior-preserving private refactor can finish with no documentation diff; edit a doc only if it is actually wrong."

FAIL if it tells the reader to edit or touch every nearby README, skill, XML comment and docfx file. Example: "Yes, touch each nearby doc with a small edit so the reviewers can see you checked."
