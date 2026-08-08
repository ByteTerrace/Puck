# Puck.World console-verb reduction — cold handoff

You are picking this up with **no prior context**. This file is self-sufficient: read it, then
do §2. Everything here was verified by running the game or by the compiler.

**The code wave is MERGED AND PUSHED.** One documentation commit is not. There are four
outstanding items, two of them decisions only the owner can make. That is §2.

---

## 1. Sixty-second orientation

Puck.World's console is its control plane — every capability is a verb driven over process
stdin. The surface had grown to 355 verbs, most of the excess being sugar. Owner charter,
verbatim: reduce it "AS MUCH AS POSSIBLE without truly losing any abilities. In other words:
get rid of all 'syntactic sugar' type stuff."

**Done: 355 → 239, no ability lost.** Landed on `features/maths-excursion` as squash
**`1ecba40f`**, pushed to origin.

The shape of it: 49 per-section document verbs collapsed into one path-addressed door
(`world.row.set <path> <json>` / `world.row.remove <path> <key>`, `<path>` being a dotted
document member path like `kits`, `hud.panels`, `views.seatRig`); per-field read-modify-write
wrappers folded into that door; editor step-twins folded onto the verb they step;
`screen.{camera,capture,desktop,qr,view}` → `screen.source <index> <kind>`;
`world.instance.seat.*` → `player.*` with a trailing `instance:<name>`; `channel.role.*`
retired. Authority is untouched by owner ruling — `world.grant`, `world.revoke`, the grant row
verbs and `identity.deliver` all survive.

---

## 2. WHAT IS OUTSTANDING — start here

**Every item in this section is ruled.** One remains as work (2c); the rest are closed. Read
2c and 2d, skip the others unless you are reconstructing how the wave finished.

### 2a. The skill-doc sweep — RESOLVED

Landed as **`9420bc5d`**. It fixed `.claude/skills/`, which the code landing missed because
that diff was confined to `src/` and `docs/`. Those files are loaded as authoritative agent
guidance, so naming dead verbs made them actively harmful. It also put the wave's rules into
`console.md` (one document door; stepped-twin folding; the recompose trap). Nothing to do.

### 2b. The trunk commit title's count — LEFT AS-IS BY RULING

`1ecba40f`'s title says "two hundred forty-one"; the real landed count is **239**, measured by
booting the tree and piping `help` (see §4). The branch eventually squashes into local `main`,
so intermediate titles vanish and this ledger carries the true count. Do not rewrite the title
and do not add a correction line for it.

### 2c. `SnapPoseMode.Warp` / `.Face` — RULED: DELETE, RENUMBER, RE-RECORD

Verified: the only `SnapPose` constructions in `src/` outside the codec are two in
`PlayerCommandModule`, both `Mode: Pose`. Warp/Face are reachable only by decoding wire byte
0/1, which nothing encodes. **`Pose` is wire byte 2.**

The ruling: delete both, renumber `Pose` to 0, and re-record every persisted replay tape the
renumbering invalidates — all in ONE change, separate from the doc sweep. No read-side
tolerance for the old bytes and no permanent hole at 0/1; supergreen doctrine. Verify with the
`world.save` oracle plus a replay playback check, and remember editor/presentation surfaces
need a windowed run.

### 2d. `player.bind` cannot address a named sub-page — RULED: RECORDED, no separate fix

`UpsertRebind` hardcodes `Group: WorldDefaultBindings.PlayGroup`, so `player.bind` can only
reach the play group's resting page or a `(group, chord)` row — never a named sub-page like
`editor-camera`. Consequence: a `player.bind`-installed binding for any editor-group verb never
fires. **Verified PRE-EXISTING** (`git show 7667d991:src/Puck.World/WorldBindingCommandModule.cs`
already hardcoded it), so this wave surfaced it rather than caused it.

This description IS the durable record — the fix rides whatever next touches the binding
surface rather than getting a change of its own. Leave the text here for whoever that is.

### 2e. Housekeeping

- The census worktree (`.claude/worktrees/verbs-census`, branch `worktree-verbs-census` @
  `5cfd0de2`) is squash-residue now that `1ecba40f` is in, and disposable.
- Three agent worktrees are also residue: `worktree-agent-a9d43b8a8b51bb0c2` (world document),
  `-a4206113b80597506` (editor), `-a6c625e579fc43a06` (player/screen).

---

## 3. Environment — read before running anything

| | |
|---|---|
| repo | `D:\Source\ByteTerrace\Puck` |
| branch | `features/maths-excursion` @ `1ecba40f` |
| census worktree | `.claude\worktrees\verbs-census` (holds `skill-doc-sweep` and this file) |

- **`origin/main` (3cfe27fb) IS STALE — never base on it.** It predates the split of
  `Puck.Cli`, `Puck.World.Data`, `Puck.World.Server` and still has `src/Puck.{Demo,Post,Bench}`
  in tree. `EnterWorktree`'s default `fresh` base lands you there. Sanity check: `ls src/` must
  show `Puck.Cli`. The live trunk is `features/maths-excursion`.
- **`experimental/` is OFF LIMITS** by owner ruling — do not read, build, cite, or port from it.
- **Never operate in the shared main checkout** — other sessions work there. Use a worktree.
- **`tests/Puck.Maths.Tests/frontier.json` + `RESULTS.md` are a rolling ratchet.** Running the
  Maths suite advances one index per key and dirties them. That is the Maths lane's ledger, not
  yours — `git restore` them, never commit them from an incidental run.

Bootstrap:

```
dotnet publish src/Puck.Cli -c Release -o src/Puck.Cli/publish
```

- **Content search:** `dotnet src/Puck.Cli/publish/Puck.Cli.dll search <pattern> <path> -M 0`.
  **Never grep/rg/Select-String** (owner mandate). `-M 0` is required or results silently cap at
  250. Use `-g "*.cs"` for extensions, `-F` for literals.
- **Semantic C#:** `… Puck.Cli.dll references <SimpleMemberName>` — simple names ONLY;
  `Type.Member` silently returns nothing and reads as "no callers". Add
  `--project <csproj> --configuration Release` if the solution load crashes.
- Build: `dotnet build Puck.slnx -c Release` (warnings-as-errors) → currently **0/0**.
- Laws: `dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release` → **6/6**.

Running the game:

```
dotnet run --project src/Puck.World -c Release --no-build -- \
  [--headless] --exit-after-seconds 15 --width 640 --height 480 \
  --state-dir <unique tmp> < script.txt > out.log 2> err.log
```

**Capture BOTH streams** — read-backs on stdout, refusals and `[world.mutation: …]` on stderr.
**Unique `--state-dir` per run.** Editor/screen/audio/host/view verbs are presentation-only and
do NOT register headless, so any proof touching them must be windowed. Verify by RUNNING, never
by a gate — `Puck.Post` is quarantined and the engine contract has no gate today.

---

## 4. How to re-measure the count yourself

```
printf 'help\n' > /tmp/help.txt
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 15 \
  --width 640 --height 480 --state-dir /tmp/vc < /tmp/help.txt > /tmp/h.out 2> /tmp/h.err
awk 'match($0, /^[a-z][A-Za-z0-9._-]* - /) { print substr($0, 1, RLENGTH-3) }' /tmp/h.out \
  | sort -u | wc -l        # expect 239
```

`.runs/reconcile.sh` does this for both composition tiers and diffs names AND affordance
metadata — per-verb routing / valueKind / bindable and per-channel shape / consumer, both
tables of the `world.affordances` payload — against recorded baselines. The metadata pass
(`.runs/afford-diff.ps1`, invoked by reconcile.sh) exists because a name-only diff silently
missed a `world.console` bindability flip — it FAILS the run on any drift in either table,
naming each row and field, and a re-record of `.runs/afford-baseline.txt` is the deliberate
way to accept one.

**Count the registry's own enumeration; do not trust reports.** One agent believed it had
deleted 7 verbs it had not, and no build or test flagged it — only booting did.

---

## 5. Findings — do not re-derive these

1. **A binding value-kind mismatch is FATAL at recompose but only NARRATED at boot.** The boot
   sweep writes vocabulary errors to stderr and continues; `WorldSeatBindings.RecomposeSeat`
   REJECTS the whole seat document and keeps the prior mapping, so every later `player.bind`,
   profile load or context regroup is silently discarded (17 rejections, measured). **Force a
   recompose (`player.bind 1 keyboard.p editor.status`) and assert stderr has no
   `recompose rejected` line.**
2. **`CommandDefinition.WithWireArgs` used to hardcode `ValueKind: Digital`** while `.Verb` took
   the parameter — why `player.claim` works as a bindable-constant precedent and naive folds
   don't. Now parameterized, default Digital.
3. **`context.Source` is the honest bound-vs-typed discriminator** (null on every text path).
   `context.Value.Kind` is only coincidentally reliable while everything declares Digital.
4. **`world.save` is the ability-preservation oracle** — canonical form, so two forms of one act
   must be BYTE-IDENTICAL across two fresh boots. **Always pair with a discriminating control**
   that must NOT match; a verification that cannot fail is a lie. A saved row round-trips
   through its own set verb, so harvest payloads from `world.save` instead of hand-authoring.
5. **Never run `puck schema --check`, and spend no effort on its failures** — they are git
   line-ending normalization noise, not drift, and the gate is owner-DEFERRED. Regenerating with
   bare `puck schema` is fine when a change moves what the schema describes; ignore endings
   entirely either way. The general verbs cite `puck schema` as DOCUMENTATION ONLY — no runtime
   schema refusals.
6. **Retired verbs hide in runtime strings and skill docs, not just comments.** Four
   operator-facing refusals named deleted verbs after the wave, and eight skill files did.
   Sweep `src/`, `docs/` AND `.claude/skills/`, and read each hit for whether it asserts a live
   verb or describes a still-real mechanism — `SnapPoseMode.Warp` and `WorldScreenBinder.TryQr`
   still exist and do the work; only the claim that a console verb spells them that way was
   wrong. `puck citations` checks every cited verb token — backticked in the skills AND `<c>`-tagged
   in `src/` XML docs — against vocabularies swept from the code; a hand-written pattern read clean
   while 40 stale tokens remained. It REFUSES (exit 3) when a literally-registered verb is missing
   from the enumeration, naming the file rather than the citation: that direction of rot makes a
   checker accuse correct documentation, which is how the previous script went red on two accurate
   lines the day a verb was added.

---

## 6. Artifacts beside this file

| file | what |
|---|---|
| `LEDGER.md` | the classification: totals, per-class counts, kill list, proof tables |
| `LEDGER-tier3-grammar.md` | the path-addressed `world.row.*` design spec |
| `ledger.tsv` | per-verb class / routing / bindability / replacement |
| `classify.py` | regenerates the counts — python, which this machine's stub cannot run; it gates nothing |
| `reconcile.sh` | re-measures the surface; diffs names AND metadata; fails on metadata drift |
| `afford-diff.ps1` | the verb+channel metadata diff reconcile.sh runs; exits nonzero naming row and field |
| `puck citations` | validates cited verb tokens (skills + `src/` XML docs) against code-swept vocabularies; not a `.runs` artifact — it lives in `Puck.Cli` |
| `verbs-landed.txt` | the 239 verbs as landed |
| `verbs-win.txt` | the 355-verb baseline at 7667d991 |
| `afford-baseline.txt` | the verb+channel metadata baseline, recorded at the landed 239-verb surface |
