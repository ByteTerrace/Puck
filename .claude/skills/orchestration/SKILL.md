---
name: orchestration
description: "Leads Puck lanes and partners. Use for briefs, choosing agents or Codex models, landing lanes, Antigravity, GPU and build scheduling, idle partners, routing findings; review-passes runs passes."
---

# Orchestration

This skill is the lead's procedure for coordinating assignments, machines,
merges and reviews. [AGENTS.md](../../../AGENTS.md) owns branch authority and
repository rules; this skill routes verification and review execution to their
owners. The user's current instruction outranks this skill; a rule that argues
against a requested change is stale and is corrected in the same change. What
the owner says in a session overrides this skill and is written into it in that
session.

Load [lane work](references/lane-work.md) before branch operations, delegation or a long run; it owns the detailed procedures.

## Plan from the milestone

Plan backward from the milestone's critical path. Find the least-prepared item
on it and put the best-fit partner there. Busy is not progress: never queue
filler to make a partner look busy. Idleness is a prompt to re-read the path, not
to create work.

## Own tracks, not tasks

Partners own tracks and outcomes. A brief gives the outcome and the
constraints, never the method, the design or the order of the edits. A partner
picks its next work from its track or a shared ready queue and announces it. The
lead keeps cross-track conflicts, merges, owner-level calls, and scope and
order. Every brief ends with the partner's own next one or two items, and the
partner works its queue without waiting for the lead between items. A wait (a
GPU leg, a review slot, a landing) blocks the lane, never the partner: the
partner takes its next item from its track or the ready queue and announces
it. A queue the
partners cannot read is no queue: keep the lead's queue where partners can reach
it, or delegate it to them. Every queue item has exactly one recorded owner, and
a handoff records the new one; an item with two owners is a defect to resolve
before either partner continues.

A delegated lane does its brief itself and starts no sub-agents of its own. A
lane that fans out hands back before its children finish, their reports never
reach it, and the integration it owed falls to another round. When work splits,
the lead splits it into lanes.

## Write and track assignments

[Lane work](references/lane-work.md#delegated-work) owns what a brief holds and how
shared-file edits are sequenced; its report shape is the three headings that
[Running long tasks](references/lane-work.md#running-long-tasks) names. Add to every
brief:

- an id, used in the brief file name, the commit trailer and every message;
- the base commit, verified by the lead before the brief goes out;
- a fresh read of the worktree's AGENTS.md and owning skill, correcting stale
  startup repository text while preserving current owner and system precedence.

When a lane demonstrates stale-rule behavior, the lead starts a fresh session
before launching its continuation. Editing a skill alone does not require a new session.

Verify each premise of a brief yourself before sending it; a wrong premise costs
every lane that received it. When a brief proves wrong, recall every lane working
from the same premise before replying to the lane that found it, and say that a
stop is a stop, not a pause.

Verify any base claim yourself with
`git merge-base --is-ancestor <base> <integration-head>`, checking its exit
status, and count the commits behind with
`git rev-list --count <base>..<integration-head>`. A two-dot range counts the
commits reachable from the right side and not the left; an empty range does not
establish ancestry.

The assignment's last commit carries one of these trailers:

```text
Brief: <id> done
Brief: <id> blocked: <question>
```

The lead's ledger and plan files that a delegate never writes are the lead's
own scratch checklists under `<worktree>/.claude/scratch/<lane>-*`. The repository's documents,
`docs/plans/open-items.md` and the plans under `docs/plans` among them, belong
to no one lane: a delegate edits them for what its change delivers, ticking an
item it closed and correcting text its change made stale, and the brief says so.

The message is the notification; git is the record. Inspect the reported
changes and what the lane ran to show them working before merging
([landing bar](../verification/SKILL.md#the-landing-bar)).

## Land each lane on its own

A lane lands as soon as it meets the
[landing bar](../verification/SKILL.md#the-landing-bar): the lead merges the
integration head into it, builds what the merge touched, and fast-forwards the
integration branch. "What the merge touched" includes what consumes it:
- a change to a shader, interface or generated header builds `Puck.slnx`, test projects included;
- a change to a world document runs every `worlds/debug/*-tests.puck` with `--reproduce`, plus the shipped-source suites (`Puck.World.Transpiler.Tests`, `Puck.Cli.Worlds.Tests`);
- every check runs from a CLI and test binaries rebuilt after the merge, never stale copies.

Never hold a finished lane for others to make a batch; a
lane that cannot land alone is a dependency to name in the brief. A wait for a
review or a review slot never holds a lane that meets the landing bar, since a
review is optional
([`review-passes`](../review-passes/SKILL.md#when-a-lane-gets-a-review)). Gates,
parity, canaries and counters checks run when the owner asks for a
qualification, on the head the owner names. A red check on the integration
branch is the next piece of work, not a reason to stop landing.

Use [verification](../verification/SKILL.md#what-uses-the-gpu) for GPU grants,
lease acquisition, time boxes and failure handling. Load it before a GPU leg or
classifying a flake. Investigation of a failure is a lane of its own.

### Launch capabilities

This table describes configured capabilities and known launch behavior; a grant
is not proof a command succeeded. Record runtime probes in the landing commit.

| Launch | Network | Git writes | Push | GPU | MCP | Hooks | Skills |
|---|---|---|---|---|---|---|---|
| Native `codex exec` | Configured on | Own-branch commits; checkout and common Git directory granted by profile | Refused for a lane | Lead grant and lease | Project `puck` registration | Current definitions trusted through `/hooks` | `.agents/skills` link |
| Codex companion `task --write` | Sandbox has no network | `workspace-write` excludes Git; lead commits | Refused | No sandbox GPU; lead verifies | Project `puck` registration | Same per-user trust, on supported tool paths | Same link |
| Claude worktree agent | Harness permissions | Own-branch commits; own-tree isolation | Refused | Lead grant and lease | Shared `puck` server | Project registration | `.claude/skills` |
| Antigravity `agy -p` | Terminal sandbox has no network | Own-branch commits | Refused by git-safety | No sandbox GPU; lead verifies | Workspace plugin registration | `.agents/hooks.json`; hooks start in `.agents/` | `.agents/skills.json` |

A native Codex lane commits directly; git-safety and git.rules guard shared
state. Linked worktrees need a grant to their separate common Git directory
and the main checkout's shared lease directory; discover them rather than
requiring one machine's checkout path. An untrusted Codex hook is skipped:
review and trust each current definition and re-review changed definitions.
Hooks cover supported activated tool paths, not every possible execution path.
See [machine setup](../../../docs/development/contributing.md#setting-up-a-machine).

Every tool resolves its hooks from the worktree it runs in, so a branch older than
the current `.claude/hooks/` has none, and Codex runs a command whose hook fails.
Before handing an old branch to any lane, the lead merges the integration tip into
it, leaving any conflicts for the lane to resolve. A worktree another agent
created can deny a Codex lane its `.git/worktrees/<name>` writes; give the lane a
worktree the lead creates.

Before launching Codex in a main checkout or fresh worktree, build/copy its CLI
as [verification](../verification/SKILL.md#run-verbs-from-your-own-cli-copy)
describes and run `dotnet <lane>-cli/Puck.Cli.dll agents link` there. Then launch
`codex exec --cd <worktree> -m <model> -c model_reasoning_effort='"<effort>"' "$(cat <brief>)" < /dev/null`.
Without the `< /dev/null`, a background launch can stop at "Reading additional input from stdin..." and never
start. Keep the output in a lane-named log. A resumed Codex session retains its launch
sandbox; after changing permissions, start a fresh continuation instead of
`--resume-last`. Companion restrictions are specific to `task`, not native exec.

An Antigravity lane runs `agy -p "$(cat <brief>)" --output-format text` from
its worktree, without `--mode`: headless flag validation rejects `--mode auto`
before sign-in, while the user-level `agentMode: "auto"` applies afterwards.
Auto mode ignores the ask list; git-safety refuses pushes and shared-state Git
operations. A headless run executes only commands its user-level allow list
names (`git`, `dotnet`, `pwsh`, `node`); any other command, such as `rm` or
`Remove-Item`, is auto-denied and ends the run with no output, so a brief that
deletes files says to use `git rm`. The home policy is the block contributing
records; login and trust material stay local. The workspace plugin exposes the MCP
configuration because a bare `.agents/mcp_config.json` is not a discovery root.

The lead machine's local agents can build on an unpushed lane head, so a lane
that depends on another starts there before it lands; merge the landed head into
it afterwards.

Partners cannot see unpushed lanes. When a partner designs against code that
exists only on the lead's machine, send it those lanes' contracts and answer its
contract questions from the local code; a stale remote makes false "failing"
claims.

The lead runs any merge an agent is denied. Agents resolve conflicts in
generated files by running their generators, never by hand-merging: the world
schemas and the portal's TypeScript (`puck schema`), the name registry
(`puck registry`), the vocabulary page (`puck vocabulary`), the console verb
catalog (`puck verbs`, which boots a headless and a windowed World, a GPU leg) and
`packages.lock.json` (`puck locks`).

The integration branch is where the lead lands work. Work reaches `main` in
complete milestones, only when the owner asks and never on a lane's request,
because a push to `main` deploys to Azure, under
[lane branch procedures](references/lane-work.md#branches-commits-and-pushes).

## Schedule machines

Allocate by each machine's fixed capability (CPU class, GPU, RAM, OS, network),
mapped once and checked every turn. Free-memory and free-disk snapshots
fluctuate with what else is open and do not define a machine's capability.
Allocate one GPU leg per GPU at a time, and let CPU work, builds included, run
beside it: a correctness leg's verdict does not depend on load, and a leg that
times out under load is re-run once alone and reported as a flake. Only
wall-clock work needs a quiet machine, and the lead takes it: a lane judges
by counts and lists the timing reads it wants, and the lead runs them in a
window it has cleared of every other lane's builds, suites, evals and World
runs. A brief that measures boot, compile or frame time grants no GPU and
takes no timing while other lanes run. Booting `Puck.World` is a GPU leg
even when only its startup is measured. Use
[`verification`](../verification/SKILL.md#what-uses-the-gpu) for GPU work
classification and execution.

Run a load governor on any machine that hosts many agents, through
`puck host load`, which samples the machine's load and names its state. Under a
watcher, `puck host load --watch` prints one line per transition (`CAPACITY`,
`LOADED` when capacity ends without pressure, `PRESSURE`, `GPU busy` and
`GPU idle`), so each change arrives once:

- On capacity, admit the next job. A heavy job (a solution build plus a full
  suite, about 7 GB at its peak) is one at a time per machine: a full
  `Puck.World.Tests` or `Puck.World.Presentation.Tests` run never runs beside
  another. `puck gate` and `puck affected` acquire the existing heavy lease
  around each heavy suite, sample headroom while holding it, and release it on
  every exit. Nested runs under `puck lease heavy` serialize on child leases
  after validating the ancestor holder's process identity.
- On pressure, admit nothing. `puck gate` and `puck affected` admit each heavy
  step only with more free memory than the capacity threshold and more free
  disk than the pressure threshold, so a running agent waits rather than being
  killed.
- On GPU idle, start the next GPU leg. A device test host counts as GPU work.
- The thresholds scale with the machine; the code is their source of truth
  (`HostLoadThresholds.For`, [CLI reference](../../../docs/reference/cli.md#puck-host-loadadmission-lines-for-the-machine)).

Deliberate CPU contention, such as a burner for a flake proof, is CPU work like
any other and may run beside another lane's GPU leg. A leg of that lane that
times out under it is re-run once alone and reported as a flake
([`verification`](../verification/SKILL.md#flake-or-failure)); a wrong value
under load is a failure whatever ran beside it.

An agent stops only the processes it started, by the PIDs it recorded at launch,
and never kills by a command-line pattern. On a shared box a filter on a common
string (a scratchpad path, a lane prefix) matches processes the agent cannot
attribute, other lanes' build and test commands among them, and those fail with
verdicts that are not evidence, so a lane that failed while the kill ran re-runs
its failed legs alone before anyone acts on them. A brief that starts background
work says to record each PID and to stop only those.

Every build, a Codex brief's included, passes `-nodeReuse:false`
([`verification`](../verification/SKILL.md#build-before-you-test) says why).

## Select models and route findings

All three families implement, and model selection is by fit. Choose the model and effort per lane by fit: put the strongest available
model on subtle or correctness-heavy work. Codex's two models, Astra
(`gpt-6-astra`, the frontier model) and Sol (`gpt-6.1-sol`, the workhorse), are
both free to use and encouraged, for implementation and for review-and-fix
passes; pass `--model` and `--effort` on every launch
([`review-passes`](../review-passes/SKILL.md#before-launching)). Keep every
Codex slot implementing; a review is optional
([`review-passes`](../review-passes/SKILL.md#when-a-lane-gets-a-review)).
Delegating to subagents needs no one's permission. A follow-on wave much larger
than the last (dozens of agents, or a second full fan-out) is a spend decision:
state its plan and cost to the owner in one sentence before launching it.

### The Claude roster

When the owner pairs with the lead, the lead stays free for that conversation
and subagents do the work: implementation, investigation, verification runs and
reviews. The lead writes briefs, inspects results, merges and reports. The
project agents under `.claude/agents/` carry the four Claude models the owner
allows, in order of rising cost:

| Agent | Model | Effort | Use for |
|---|---|---|---|
| `scout` | Haiku 5.5 | medium | Read-only search, log triage, live-World probes and captures. No source edits or commits; authorized CLI bootstrap is allowed. |
| `implementer` | Sonnet 5.5 | high | A specified brief with a finish line. It is the default writer. |
| `specialist` | Opus 5.5 | high | Design judgement across projects, diagnosis of an unknown cause, review-and-fix passes. |
| `principal` | Fable 5.1 | high | Correctness-critical or long-horizon work: determinism and fixed-point maths, the bake and its kernels, renderer architecture, a defect other lanes failed to crack. |

Route each brief to the cheapest role that fits it (the owner's ruling): a
brief with a known cause or a specified result, such as content, a document
change, a world edit or a fix with a repro, goes to `implementer`, even when it
is large. `specialist` is for an unknown cause or a design call across projects.
`principal` is for the problems the table names and nothing else. When a lane on
a cheaper role stalls, escalate that lane; never start on the expensive role
because a brief is long. Codex is part of the same lineup: Sol is a peer of
`implementer` and takes the same briefs, and Astra is comparable to `principal`
and takes only what `principal` would. Spread implementer briefs across
`implementer` and Sol, so both families' slots stay busy. Concept art and
other image generation go to Codex as the primary artist and judge (the
owner's ruling): its built-in `image_generation` tool makes the images and its
verdicts decide which to keep, and a Claude partner writes the brief and
checks verifiable constraints, overruling only a hard-constraint violation.
Concept art a modeller builds from carries no glaring mistake (the owner's
ruling): every panel passes a written audit by both partners covering
attachment and continuity, joint range, physical coherence of the pose (thrust
against motion, weight over the feet), agreement with the turnaround, the
camera's perspective, compositing seams and the character's fixed facts. A panel
that fails is regenerated or removed, never kept with a caveat, and the lead
reviews every panel before merging.

Pass `effort` on the Agent call to move one lane up or down: `xhigh` for long
agentic coding on a hard lane, `max` only when correctness outweighs cost.
Opus 5.5 and Haiku 5.5 run at `medium` when nothing sets an effort, which is
why each agent file names one. Pass `model` only to put a role on another
model.

Writers run in their own worktree (`isolation: worktree`), branched from the
lead's HEAD (`worktree.baseRef` in `.claude/settings.json`), so their edits
never touch the checkout the owner is pairing in. Every agent reaches the live
World through the session's `puck` MCP server. That World runs the main
checkout's build and sources, so a lane sees its own edits running only after
the lead merges and reloads, or in a World of its own under a GPU grant (booted
with `--devices none`), named by `world` on every call. Reads and captures of the live World are always
allowed; a brief names the live mutations a lane may make. The machine belongs
to the agents (the owner's ruling): the lead boots a World for a check and stops
it when the check ends, and it leaves one running for the owner only when the
owner asks to play or pair in it.

Codex lanes reach the same World: the repository's `.codex/config.toml` starts
the same `puck` MCP launcher, so a companion `task` run in this checkout or a
worktree of it can call `puck_worlds` and the rest.

| Codex model | Id | OpenAI's positioning | Effort | Price per 1M in / out |
|---|---|---|---|---|
| Astra | `gpt-6-astra` | Most capable, for the most demanding work | `low` to `max`; refuses `none` | $10 / $50 |
| Sol | `gpt-6.1-sol` | Near-Astra for complex coding at lower cost | `low` to `max`, default `medium`; refuses `none` and `minimal` | $2 / $10 |

Both have a 1,050,000-token context and 128,000 output tokens. The companion's
`--effort` stops at `xhigh`; `max` needs `codex exec -c
model_reasoning_effort='"max"'`, which the Codex CLI applies (its header prints
the effort). Prices are API list prices, a guide to relative cost; Codex draws
on the plan's allowance. Effort is not capability across models: OpenAI's Codex
guidance says Astra at `low` can outperform Sol at `high`, so a lane that needs
Sol at `high` or above tries Astra at `low` or `medium` first. Raising effort
never supplies a missing file, tool or permission; fix the brief first. Codex
also offers GPT-5.6 Terra (everyday work, routine code changes) and GPT-5.6 Luna
(fast, focused or repetitive edits), the scout tier of that family. Astra asks more questions when input
could change the result and follows `AGENTS.md` and skills more literally, so an
Astra brief states its authority and finish line outright. The owner's own Codex Desktop thread holds a writer lock while it is
open, so the lead never resumes it with `codex exec resume`; the lead and that
thread talk through the owner, or through a file both read.

Antigravity is the third family, running Gemini 4 Argon (Google's flagship,
on the owner's Google AI Ultra plan; an Antigravity lane launches as above). Its published results
put it level with Astra overall (Artificial Analysis Intelligence Index about
53) and ahead on repository issue fixing (DeepSWE), app and UI generation and
security remediation (CWE-bench), behind Opus and Astra on long terminal-driven
and long-horizon work (Terminal-bench, FrontierSWE), with a 1M-token context
and about twice Astra's output per task; most figures are vendor-reported.
Route it accordingly:

| Give Argon | Keep from Argon |
|---|---|
| Independent review and audit passes (a third family's blind spots differ from Claude's and Codex's) | Long implementation lanes with many build/test/World steps |
| Security review of a change or an area | Lanes that need network or GPU legs from inside its sandbox |
| Whole-area analysis that needs a large context | Anything that must push or mutate shared Git state |
| Portal, dashboard and other TypeScript/UI work | |
| Research synthesis | |

Judge the family balance lane by lane. It is not a quota and not a reason to
reassign work already under way. Owner steering adjusts judgement; do not harden
a small direction into a quota or an always rule.

The lead rules on what a review raises: a scoped fix, a recorded open item, or a
dismissal with its reason. Every finding gets a destination before it is set
aside: a lane, or a named deferral where scope is tracked. A finding called out
of scope without a destination is not set aside. There is no second round.

Route reviews by where the work lives. A pushed branch can be reviewed in any
machine's Codex slot; a local-only lane needs the lead's own slots. When the
lead's slots are full, send reviews of pushed branches to partners.

## Audit the process before the owner does

The lead's job is throughput of landed, correct code. Finding a process defect
is the lead's work; one the owner has to point out is a lead failure. Measure the
thing the owner cares about, not machine load alone:

- time from a lane's last commit to its landing on the integration branch;
- implementation slots (Codex and agent) not implementing right now;
- the board's age against the newest lane change.

In Claude, arm a recurring audit as a background monitor, which reports while the lead is
busy; a session cron fires only when the lead is idle and misses exactly the
drift it exists for. On each audit, act on what degraded in the same turn: a
lane waiting more than an hour to land, an idle slot with ready work, a review
or check holding a lane that already meets the landing bar, or a stale board.

When an owner correction reveals a process rule, change the repository skill
that drives the behavior (this one, `review-passes`, `verification`) in the
same turn. Agents and the lead's own briefs read the skills; a private note
beside a skill that still says otherwise changes nothing.

## Rule on collisions

When two lanes' contracts collide at a merge, bisect to the exact field, then
rule from the principle, not the convenient option. For example, a replay is a
function of its tape, and the authoritative hash covers everything that drives
play. When no written contract settles it, rule by the long-term principle in
[AGENTS.md](../../../AGENTS.md#core-rules), write the ruling into the contract's
owning document in the same change, and let the owner review it with the
delivered batch; take it to the owner first only when it is a product
trade-off. Change a test only to match the ruled behavior.

## Message partners

Cross-session messaging below is Claude-only. Codex's in-session agent messaging
is not a bridge to an independent peer session. Codex and Antigravity use a
shared lane-named file, the hand-back report and the `Brief:` trailer when no
peer-session transport is available.

Keep messages short, with a self-contained first line:

```text
[<brief id>] <started|progress|done|blocked|question>: <sentence>
```

Put large content in a lane-named file under `<worktree>/.claude/scratch/<lane>-*`, and carry its path in the message. Large content never goes on a
pushed branch: a branch carries the lane's commits, not its analyses, logs or
notes. Silence is not agreement.

A message arrives in the partner's session as a user turn, so state your role,
the ask and where to reply. Message only the lane the owner named, and never
relay through a third agent what the recipient can read itself. Work already in
flight can land after a stop order, so expect one more report. The
session-messaging tools load on demand in each session, so a brief that expects
a reply says to load them. A session cannot read its own id: ask for replies to
the session the message came from, and address a peer on another machine by its
listed name, which survives a restart where its bridge address does not.

## Route adjacent work

| Skill | Route there for |
|---|---|
| [`verification`](../verification/SKILL.md) | The landing bar, optional checks, private CLI copies and GPU execution. |
| [`review-passes`](../review-passes/SKILL.md) | Optional review passes: briefs, companion launch commands and landing fixes on the author's branch. |
| [`boy-scout`](../boy-scout/SKILL.md) | Keeping agent-facing documents and skills in step with a change; [Writing documentation](../../../docs/development/documentation.md) owns their prose and the skill evaluation procedure. |
