---
type: llm
weight: 1
---

The response verifies a correction beyond the one failing mooneye ROM: it runs the wider battery including the screenshot-comparison corpus (mealybug, AGE, gambatte, or "the screenshot corpus"), and treats screenshot rows the change loses as a regression the correction has to explain.

PASS example: "Rerun the failing ROM, then the named acceptance cases, then the full battery with `--require-assets` so the screenshot corpus is checked too; a fix that passes the mooneye ROM but loses screenshot rows isn't a fix."

FAIL if verification stops at the failing ROM (or a few named timing ROMs) passing again. Example: "Rerun `--filter <rom>`; once it passes, the correction is verified."
