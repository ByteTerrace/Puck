# Agent command hooks

The Claude settings register `world-devices.ps1`, `no-node-reuse.ps1` and
`no-coauthor.ps1` for Bash and PowerShell. Each policy dot-sources
`command-policy.ps1` for input decoding, shell token boundaries and responses.
The owner launch configuration is not a shell tool call and is unaffected.

| Harness | Command input | Refusal |
|---|---|---|
| Claude Code | `tool_input.command` | `hookSpecificOutput.permissionDecision: deny` |
| Codex | `tool_input.command`, or native function `tool_input.cmd` | Same PreToolUse denial shape |
| Antigravity | `toolCall.args.CommandLine` | `decision: deny` with `reason` |

Claude and Codex receive no decision on a passing command. Antigravity requires
an explicit `allow` or `deny`. Its registration belongs in the named-hook
object in `.agents/hooks.json`; its command runs from that file's directory.
Codex's registration belongs in `.codex/hooks.json` beside its active project
configuration, and must be trusted through `/hooks` before it runs. Its command
resolves the script from the Git root because the session can start below it.
See the [official Codex hook contract](https://learn.chatgpt.com/docs/hooks).
Antigravity's bundled `agy-customizations/docs/hooks.md` owns its local contract.

The policies inspect literal commands, quoted arguments, chained commands and
explicit shell `-c` or `-Command` wrappers. They are reminders enforced at the
tool boundary, not a general shell interpreter: aliases, computed executable
names, encoded commands and arbitrary script bodies require normal agent
discipline. The commit policy reads message files and reused commit messages;
it refuses stdin or absent message files because it cannot inspect them before
the command runs. It cannot inspect a message later composed in an editor.

From the checkout root, run the CPU-only harness check with
`pwsh -NoProfile -File .claude/hooks/tests/command-policies.ps1`. It feeds JSON
to each real script through stdin for the three harnesses, including Codex's
native function input, and checks both permitted and refused commands. It
executes no candidate World, build or commit command.
