---
type: llm
weight: 1
---

The response's `puck search` command selects lines that contain grant and do not contain refusal, in a form the Puck regex engine accepts.

PASS if the pattern is the anchored complement with grant in the kept part and refusal in the complemented part, such as `'^(.*grant.*&~(.*refusal.*))$'`, or if a second filter drops the refusal lines from the grant matches. Case may be handled by `-i`.

FAIL if inclusion and exclusion are swapped (`^(.*refusal.*&~(.*grant.*))$`), if the complement is unanchored (`.*grant.*&~(.*refusal.*)`, which a short substring satisfies), or if the exclusion is a leading negative lookahead such as `^(?!.*refusal).*grant`, which Puck supports only at a pattern's start as lookbehind and at its end as lookahead.
