# Four Corners sharded — the `puck canary` shape

This designs the `puck canary` manifest that replaces `run.ps1`/`play.ps1` as
the committed proof of Four Corners' multi-authority sharding. It does not
build anything: `run.ps1` keeps proving the claim by hand until a later lane
implements the runner changes below.

## What the claim is

Five independent `Puck.World` processes — the four ground quilt authorities
(`quilt-nw`, `quilt-ne`, `quilt-se`, `quilt-sw`) plus the floating island
(`quilt-island`) — each bind a distinct real TCP endpoint and never colocate.
A body crossing a horizontal seam or the vertical up-boundary hands off to
the neighbour's authority; the crossing narrates the neighbour's endpoint,
not `boot/...`; camera and movement control survive the handoff on both
sticks; two probe bodies placed either side of a seam settle to
non-overlapping positions read back through two separate authority
transcripts; producer-driven bodies nobody ever drives complete multi-hop
autonomous ownership migration across at least three authorities; and every
authority reports zero rejected wire commands. `run.ps1`'s `.DESCRIPTION`
and `Require` calls are the ground truth this design restates as canary
`expect` entries — read it beside this document.

## What the current runner already expresses

`src/Puck.Cli/Canary` already runs a two-process federated shape:
`CanaryLeg.AuthorityWorldPath` boots one companion authority
(`AuthorityCompanion.Start`) that must report `[world.listen: bound …]`
before the primary leg process launches with `--connect 127.0.0.1:38473`,
and `CanaryCommand.PrepareFederatedWorlds` copies every `*.world.json`
sibling of the leg's own world into one scratch directory so a
`references`-resolved neighbour document is present when the primary reads
it. `travel-frame-seam-remote`, `travel-frame-portal-remote`, and
`travel-frame-boot-remote` are this shape today.

That is a real subset of Four Corners: one seam, one neighbour, no island,
no autonomous producers, no multi-hop retained control. If the claim were
narrowed to "one ground authority hands a body to one neighbour and the
neighbour's endpoint is named," the existing manifest schema and runner
already express it with zero code changes — the discriminating leg's own
mechanical re-check (`positiveOnDiscriminating` in `CanaryCommand.
RunSelected`) already enforces the positive/negative contract this design
reuses for every additional observation below.

What it cannot express is the full five-authority mesh: a fifth peer with
no "client" role, four ground authorities that are all listeners rather
than one listener plus one dialer, per-authority assertions read against
five separate transcripts, and the autonomous/dual-hop/tape evidence
`run.ps1` also carries.

## How neighbour addressing actually resolves

Each quilt document's `adjacencies[].destination` is a symbolic id
(`"ne"`, `"island"`); the live endpoint is never embedded in the crossing
document itself. `references[]` maps that same id to a sibling document
path (`quilt-ne.world.json`), and `destinations[]` names it as a durable,
globally-scoped reference. At authority-crossing time the process resolves
the symbolic id by reading the sibling document's own `host.authority`
field from wherever `references[].document` points — which is why
`PrepareFederatedWorlds` copies every sibling into the same scratch
directory before patching: the patched `host.listen`/`host.authority` on
each copy is what the crossing peer will read. Five authorities need the
same copy-once/patch-N-times shape `run.ps1` already implements by hand
(`$topology` plus `Get-FreeLoopbackPort`) — `PrepareFederatedWorlds` is the
right function to generalize, not a new mechanism.

## Manifest additions

1. **N-ary authority list, no client/companion asymmetry.** Replace the
   singular `authorityWorld: string` with a `authorities: [{ id, world }]`
   array on the leg (five entries: `nw`, `ne`, `se`, `sw`, `island`). Every
   entry is a listener; none carries `connect`, so `CanaryLeg.Connect` and
   the hardcoded `--connect 127.0.0.1:38473` argument stop applying to this
   shape entirely. The leg's own `world`/`script` become just the entry the
   assertions read by default (conventionally `nw`, since that is the
   process `run.ps1` treats as the human-playable one), not a structurally
   different role.
2. **A script per authority.** Each authority needs its own stdin script —
   `run.ps1` proves this directly: `nw` drives seat 1 by hand
   (`player.pose`/`player.fly`/`player.signal`), `ne`/`se`/`sw` each drive
   their own seam crossing and contact probe, and `island` only waits and
   reads `world.adjacencies`. The manifest needs `scripts: [{ id, script }]`
   (or a `script` field per `authorities[]` entry) rather than the single
   `script` a two-process leg carries today.
3. **Per-authority assertions.** `CanaryAssertions.Evaluate` reads exactly
   one `CanaryTranscript` today; a companion's stdout/stderr are written to
   `authority-stdout.log`/`authority-stderr.log` for diagnosis but never
   assorted against `expect`. Every `line`/`response`/`relation`/`sequence`
   assertion needs an `authority: "nw"` (etc.) selector, defaulting to the
   leg's own primary transcript so existing two-process manifests need no
   edits. `CanaryCommand` needs to keep all five transcripts (not just the
   primary's) alive past the run and hand the right one to each assertion.
4. **Dynamic per-authority ports, not one fixed endpoint.** The single
   `"127.0.0.1:38473"` constant becomes five `Get-FreeLoopbackPort`-style
   allocations, one per authority id, written into that authority's own
   `host.listen`/`host.authority` on its copy in the shared scratch
   directory (see above) — `PrepareFederatedWorlds` generalizes from "patch
   the one companion" to "patch every authority, including the one the
   primary script drives."
5. **A wider per-leg timeout for this shape.** `MaximumLegTimeoutSeconds`
   (60s) and `MaximumExitSeconds` (30s) are sized for one or two processes;
   `run.ps1` already runs the slowest authority for up to 90 startup ticks
   plus the full scripted sequence at `--exit-after-seconds 56`, across
   five concurrently-spawned `dotnet` processes on a shared machine. Either
   raise both ceilings for manifests declaring more than one
   `authorities[]` entry, or add a distinct, explicitly wider ceiling class
   for multi-authority legs so a two-process manifest cannot accidentally
   claim the same budget by mistake.
6. **Tape capture needs no new manifest concept — reuse commands.**
   `replay.record <name>` / `replay.stop` are ordinary Immediate verbs;
   `replay.stop` already re-drives its own recording once and reports
   MATCH/MISMATCH inline, in the same process, before the script continues.
   Each authority's script arms recording near its start and stops it near
   its end (before the runner-owned trailing `wire.errors`), and the
   MATCH line becomes an ordinary `line`/`response` assertion against that
   authority's transcript — no runner change beyond item 3 above. A
   stronger, cross-boot `replay.verify <name>` (a second process reading the
   persisted `.puckreplay` from the first process's `--state-dir`) is
   possible but needs the runner to let a leg phase reuse a prior phase's
   `--state-dir` across two sequential process launches — `CreateRunDirectory`
   mints a fresh directory per leg today. Design choice for the building
   lane: `replay.stop`'s inline re-drive is the same MATCH/MISMATCH
   computation `replay.verify` performs, so it already carries the
   determinism evidence the brief asks for without the second-phase
   plumbing — reach for the two-phase form only if a reviewer wants the
   fresh-process boundary as independent proof of the persisted bytes.
7. **Orleans stand-in parameterization.** Keep each `authorities[]` entry
   naming a role (id, world, and today's only launch strategy —
   `dotnet run` the Puck.World artifact) rather than hardcoding a process
   launch inline in five call sites. `AuthorityCompanion.Start` is already
   the seed of that abstraction for one companion; generalizing it to a
   small strategy the runner can later swap per-id (a grain activation
   standing in for one `authorities[]` entry, reporting the same
   `[world.listen: bound …]`-shaped readiness line the runner already polls
   for) keeps the manifest format itself silent about how an authority is
   hosted — the manifest names ids and worlds, never process mechanics.

## Observations bound to each variable

Restated from `run.ps1`'s own `Require` calls, one canary `expect` entry
each, `authority`-scoped as noted:

| Variable | Observation | Authority |
|---|---|---|
| Distinct bind | `[world.listen: bound <endpoint>]` on stderr, one per id, no two equal | all five |
| No silent colocation | the four ground authorities' bound endpoints are pairwise distinct from the island's | all five |
| Automatic horizontal crossing | `[world.adjacency: 'boot/<edge>' seat 1 crossed` fires without a scripted transfer verb | nw, ne, se, sw |
| Automatic vertical crossing | `[world.adjacency: 'boot/up' seat 4 crossed` into the island | nw, ne, se, sw |
| Remote authority named | `remote authority <destination endpoint>` on stdout, matching that neighbour's own bound endpoint | nw, ne, se, sw |
| Durable addressing | `player.where`/`world.view.camera` responses carry `instance:`/`entity=<endpoint>/…` — never `entities=boot/` | nw, ne, se, sw |
| Diagonal peer derivation | `derived=corner` naming the diagonal neighbour's endpoint | nw, ne, se, sw |
| Retained camera control | two `world.view.camera` reads straddling a right-stick signal differ in yaw by more than a floor | nw, ne, se, sw |
| Retained movement control | two `player.where` reads straddling a held left-stick signal differ in position by more than a floor | nw, ne, se, sw |
| Cross-authority contact pair | a probe body's `ContactOut` coordinate on one authority and its counterpart's `ContactIn` coordinate on the neighbour settle to a non-overlapping pair | each seam pair |
| Autonomous multi-hop migration | ≥3 `[world.transfer: … arrived (anonymous)]` lines across ≥3 distinct authorities for a body seat 5/6 never drove | nw + at least two neighbours |
| Grounded before/after a post-transition jump | two `[world.contacts: … grounded=true …]` reads bracketing the jump | nw |
| Zero rejected commands | the runner-owned terminal `wire.errors` line already asserts this per process (existing invariant) | all five |
| Deterministic tape | `replay.stop`'s inline MATCH verdict on the authority's own recorded stream | at least one authority (nw is sufficient; every authority is stronger) |

## Falsifier per leg

- **Positive leg.** The full script above, all five authorities live. Every
  observation in the table must hold.
- **Discriminating leg.** Same five worlds and topology, but the driven
  bodies never receive `player.fly`/`player.signal`/`player.press` (only
  `world.wait` and read-backs) — the exact shape `travel-frame-seam-remote`'s
  own discriminating leg already uses. The bind-endpoint and
  zero-rejected-commands observations still hold (they are not about
  motion), but every crossing/addressing/contact/retained-control/
  autonomous-migration observation must go red, because nothing ever moves
  to trigger a crossing. The runner's existing `positiveOnDiscriminating`
  mechanical re-check — replaying the positive leg's `expect` list against
  the discriminating leg's transcripts and requiring every one to fail —
  generalizes directly once assertions carry a per-authority selector: run
  it once per authority's pair of transcripts.
- **Tape control.** A companion scripted variant (not a separate manifest
  leg — a documented follow-up check for whoever builds this) that begins
  `replay.record` only after several ticks have already elapsed must report
  `DivergedAtStart` (tick 0 mismatch) rather than MATCH — proving the
  MATCH/MISMATCH distinction the tape observation leans on is live and not
  vacuously true. `run.ps1` carries no tape evidence at all today; this is
  new coverage the canary form adds, not a restatement of an existing
  `Require`.
- **Killed/mispointed authority (documented, not automated).**
  `run.ps1`'s own `.DESCRIPTION` states the intended failure mode: killing
  or mispointing a companion authority should turn the corresponding
  transfer/address observation red rather than silently colocating traffic,
  and `unavailable: closed` should keep a truly absent edge closed rather
  than becoming a hole. Nothing here builds an automated third leg for it —
  it is out of scope for two legs by the canary format's own rule (a
  manifest is exactly `positive`/`discriminating`), and the building lane
  should decide whether it belongs in the discriminating leg's own
  narrative or stays a documented manual check.

## What is not decided here

- Whether `authorities[]`/per-authority `script`/`authority`-scoped
  assertions land as new manifest members validated by
  `CanaryManifestLoader`'s existing strict-member-list discipline, or as a
  second manifest shape (`canary.federated.json`) the loader dispatches on —
  the building lane should read `CanaryManifestLoader.ReadLeg`'s
  `RequireOnlyMembers` calls before choosing; either preserves today's
  two-process manifests unchanged.
- Where the wider per-leg timeout ceiling (item 5) lives — a per-manifest
  override field, or a derived value (`60 + (15 * authorities.Count)`, the
  shape `run.ps1`'s own per-id `startupWait` staggering suggests) — needs a
  call from whoever measures real five-process spawn variance on the target
  machine before picking a number.
- The Orleans stand-in strategy interface (item 7) is a design placeholder,
  not a chosen shape; it should be sized against the actual O v2 hosting
  brief when that lane is ready, not guessed at here.
