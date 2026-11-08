---
description: "Editor highlighting and live errors for World Studio drew a plan for a JavaScript grammar and checker in the portal, a second implementation of the language the in-browser engine's language server already serves."
tags: [capability]
runs: 5
max_turns: 8
allowed_tools: [Read, Glob, Grep, Skill]
---

No Puck checkout is reachable from this session; answer from what you know of Puck.

In Puck.Dashboard's World Studio, the `.puck` editor should colour keywords and names and underline mistakes as the author types. I'm planning to write a Lezer grammar for `.puck` in the portal, plus a small JavaScript checker for the common mistakes so feedback is instant. Is there a better route in Puck, and where would the highlighting and the diagnostics come from?
