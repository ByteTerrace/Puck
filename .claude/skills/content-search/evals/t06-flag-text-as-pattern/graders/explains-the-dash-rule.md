---
type: llm
weight: 1
---

The response explains that a pattern beginning with a dash is read as a command-line option unless it follows the `--` separator.

PASS if it says a pattern starting with `-` would otherwise be parsed as a flag and that `--` (before the pattern) ends option parsing, or that an escape or `-e` form protects it. Example: "`--exit-after-seconds` would be parsed as an option, so put `--` before it: `search -F -M 0 -- --exit-after-seconds`."

FAIL if it gives no explanation of how the leading dash is handled, or says the dash needs no special handling. Example: "Just run `puck search --exit-after-seconds`; the tool works it out."
