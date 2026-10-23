# Agent hooks

One command-policy entry, `agent-policy.ps1`, is registered once per tool for
its shell tools. It reads one payload, classifies the command into the program
invocations it would start (`invocations.ps1`), runs every policy under
`policies/` over them, and replies once: deny over ask over abstain. A command
no policy speaks to gets no output, so the tool's own permission rules decide.

| Policy | Refuses or asks |
|---|---|
| `git-safety` | Pushes and `gh` writes, and the git that touches what every lane shares (the table is in the script's header) |
| `codex-launch` | A Codex launch that changes its own approvals or sandbox |
| `no-coauthor` | A commit, merge, commit-tree or cherry-pick whose message carries `Co-Authored-By` |
| `no-node-reuse` | `dotnet build`, `msbuild`, `restore`, `pack` or `publish` without `-nodeReuse:false` |
| `world-devices` | A `Puck.World` boot that names no `--devices` |
| `opaque-command` | A form the classifier recognizes but cannot see into |

## Roles

A lane is a Claude subagent (its payload carries `agent_id`), a Codex session or
an Antigravity session. A lane is refused every push, every shared-state git
command and anything the classifier cannot see into. The Claude session with no
`agent_id` is the lead: it is asked before a push or `gh` write that reaches
`main`, force-pushes, deletes, mirrors or cannot be resolved, before a
permission-changing Codex launch and before an opaque command; its other pushes
and shared-state commands are left to its settings. A remote partner pushing its
own branch from its own session is the exception AGENTS.md states in prose;
nothing here carves it out.

## Registration

| Tool | Registration | Command input | Working directory | Reply |
|---|---|---|---|---|
| Claude Code | `.claude/settings.json`, Bash and PowerShell: `agent-policy.ps1 -Tool claude` | `tool_input.command` | `cwd` | `hookSpecificOutput.permissionDecision` |
| Codex | `.codex/hooks.json`: `agent-policy.ps1 -Tool codex`, resolved from the Git root | `tool_input.command`, or native `tool_input.cmd` | `tool_input.workdir`, then `cwd` | Same as Claude's |
| Antigravity | `.agents/hooks.json`, `run_command`: `agent-policy.ps1 -Tool agy` | `toolCall.args.CommandLine` | `workspacePaths[0]`; the hook runs from `.agents/` | `decision` and `reason` |

Only the Claude lead is ever asked; a Codex or Antigravity reply is deny or
nothing. Codex runs a hook only once its definition is trusted through `/hooks`,
and a changed definition is reviewed again. See the
[Codex hook contract](https://learn.chatgpt.com/docs/hooks); Antigravity's
bundled `agy-customizations/docs/hooks.md` owns its contract.

`bash-transport.ps1` stays its own Claude Bash hook, since it rewrites the
input: a command past 2,000 characters or holding `\\` runs from a script file.
Claude matches its permission rules against the rewritten `.` line, so before
rewriting the transport reads the original command with the same classifier and
asks when it finds a git push, a `codex exec`, a permission-changing Codex
launch, an opaque form or anything git-safety would refuse; the reason quotes
those invocations whole, up to 2,000 characters.

`prose-doctrine.ps1` is Claude-only: it injects the writing rules for the
edited file's surface through `additionalContext`, which Antigravity's
PreToolUse reply has no field for. It takes the rules from the checkout that
holds the file, so a lane's worktree edit gets its own worktree's rules, and
shows each surface once per session, agent and checkout.

`puck-mcp.ps1` starts the `puck` MCP adapter for Claude (`.mcp.json`), Codex
(`.codex/config.toml`), VS Code and Antigravity (the workspace plugin
`.agents/plugins/puck/`, registered by `.agents/plugins.json`).

## What the classifier sees

The dialect comes from the tool: Claude's Bash tool is POSIX and its PowerShell
tool is PowerShell, and a Codex `exec_command` may name its shell in
`tool_input.shell`. A Codex or Antigravity command that names no shell is read
both ways and every invocation either reading finds counts, except the POSIX
reading's opaque ones, since a PowerShell variable reads there as a computed
command name. PowerShell commands go through PowerShell's own parser; POSIX shell
text goes through a tokenizer that knows quoting, `$(...)` and backticks (in
double quotes too), heredocs, redirections, `NAME=value` prefixes and `(`, `)`,
`{`, `}` boundaries. It unwraps `env`, `sudo`, `timeout`, `nice`, `xargs`,
`command` (not `command -v`, `-V` or a cluster such as `-pv`), `find -exec`, a
lease's `--`, `bash -c`, `pwsh -Command`,
`cmd /c`, `eval` of literal text, `Invoke-Expression` of a literal and
`Start-Process`, and follows `cd` and `Set-Location`. Quoted text, heredoc
bodies and here-strings are data. A computed command name, `-EncodedCommand`,
`eval` or `Invoke-Expression` of a variable and a shell reading its script from
stdin are opaque. Each argument also records whether it is known only at run
time (a variable or a substitution): git-safety asks the lead and refuses a
lane when such an argument is the git verb, an option before it, a push's
remote, refspec or option, a `gh` command group or `gh api` option or endpoint,
or an option of a verb it refuses only for some options. A script file a
command runs is not read. These are guardrails
at the tool boundary, not a shell security boundary.

The commit policy reads `-m` and `--trailer` text, `-F` and `-t` files, reused
messages and a cherry-pick's source messages. It refuses a message on stdin and
an absent message file, since it cannot read them before the command runs: write
the message file with the edit tool first.

## Tests

From the checkout root:

- `pwsh -NoProfile -File .claude/hooks/tests/command-policies.ps1` runs every
  case in each payload shape its dialect reaches (Claude lead and subagent for
  Bash and PowerShell, Codex with a bash or pwsh shell, and Codex and
  Antigravity naming no shell), with separate lead and lane verdicts, then runs the real scripts as each tool starts them. It
  builds scratch repositories under the temp directory and executes no
  candidate command.
- `pwsh -NoProfile -File .claude/hooks/tests/git-rules-agreement.ps1` checks
  `.codex/rules/git.rules` against git-safety's lane verdicts through
  `codex execpolicy check`, and lists the commands only the hook can catch.
