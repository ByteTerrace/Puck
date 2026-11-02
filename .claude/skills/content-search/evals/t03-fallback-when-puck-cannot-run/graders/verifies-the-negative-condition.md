---
type: llm
weight: 1
---

The response's procedure excludes lines that contain Refusal, using something the host tool supports: a second filter over the first search's lines, a lookahead in a tool that supports it (such as `rg --pcre2` or `grep -P`), or inspecting each line.

PASS example: "`grep -rni grant src | grep -vi refusal`" or "list every `grant` line, then drop the ones that also match `refusal`."

FAIL if the procedure matches Grant alone and never removes the Refusal lines, or relies on a lookahead or complement in a tool that does not support it. Example: "`rg -i '^(?!.*refusal).*grant' src`" (plain ripgrep has no lookahead), or "`rg -i grant src`".
