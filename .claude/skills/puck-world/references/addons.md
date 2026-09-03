# Addons — the WebAssembly guest runtime

A `WorldAddonRow` in the world document's `addons` section mounts a
deterministic WebAssembly guest. World-side runtime:
`src/Puck.World.Addons/WorldAddonRuntime.cs` + `WorldAddonWire.cs`, behind the
`IWorldAddonHost` seam `src/Puck.World.Server/IWorldAddonHost.cs` declares.
The ABI itself (cell layouts, exports, mount steps, determinism posture) is
owned by `src/Puck.Scripting/README.md` — read it before touching the guest
boundary; this file carries the World-relevant surface.

## Contents

- The row, mounting, and the prepare/commit transaction
- The three pump points
- Channels and the wire
- Requests, queries, verdicts
- Fuel
- Receipts and replay
- Verbs
- World events
- The shipped example
- Puck.Scripting in one paragraph

## The row, mounting, and the prepare/commit transaction

`WorldAddonRow(Name, ModulePath, Hash, Fuel, Enabled, Requests?,
MemoryWatches?, Revision)`. `Hash` is REQUIRED (`sha256-64/<16 hex>`): an
unpinned guest makes state depend on a file on disk — a determinism hole
before a security one. The hash is verified BEFORE descriptor decode, so
the pin covers the channel table too; a mismatch is a load fault. `Revision`
(`ulong`, default 0) is the instance-revision token: any change from what a
currently-mounted guest was prepared under means fresh guest instantiation,
even when every other field is unchanged — the document-authored equivalent
of a reload. It is NOT promised monotonic (undo and a whole-document load
legitimately move it backward — configuration undo, not time travel: the
pre-undo guest's memory is not part of the document and is never restored);
an author driving it forward is expected to increment with checked overflow.

**The doors are `world.row.set addons <row-json>` / `world.row.remove addons
<name>`** — the ordinary document-mutation pipeline (`WorldMutation.UpsertAddon`/
`RemoveAddon`), nothing else. There is no separate runtime-facing lifecycle
surface: mounting, unmounting, reloading, enabling, and disabling an addon are
all expressed through the SAME row, gated by `Enabled` and `Revision`. Mounting
a guest — compile under the pin, ABI admit, instantiate, run `puck_init` — is
prepared as the addon runtime's own half of `IWorldAddonHost.TryPrepare`, the
LAST fallible gate `WorldServer.TryApplyMutation` runs (after authority,
compose, whole-document validation, capacity, and solids). A LIVE mutation
reaches it only when it touches the `addons` section (`UpsertAddon`/
`RemoveAddon` — every other mutation kind never calls it); a whole-document
swap (`world.reset`/`.load`/`.reload`, and the final step of `world.undo`)
reaches it UNCONDITIONALLY, because any section can move a channel-table
dependency the row-level predicate cannot see. Preparation builds a
disposable, uncommitted `PreparedAddonInstall` covering the WHOLE runtime
delta (every guest the candidate document would mount, plus the complete
replacement `AddonHost` registry and, when the candidate's channel
declarations moved, a freshly staged channel table and host). A row reuses
its guest — memory AND fault state alike — only when it is STRUCTURALLY
equal to the row that guest was last prepared under (every field, including
`Requests`/`MemoryWatches` content) AND the channel table is unchanged;
runtime fault state never participates, so a sticky-faulted guest stays
faulted until ITS OWN row's structural identity moves, and resubmitting an
identical row never resets a healthy guest's memory either. A channel-table
change replaces every enabled row's guest at once, even one that compares
structurally equal to its own prior self. `IWorldAddonHost.Commit` then
publishes the whole plan by reference adoption alone — no I/O, allocation,
compilation, or fallible call; narration and superseded-guest disposal wait
for a separate `Finish` call the caller makes only after its own
document/journal publication is itself durable. An enabled row that cannot
prepare refuses the WHOLE mutation (or rebuild, or undo), the candidate
discarded, the live document byte-identical, the tick surviving. A server
with no addon host attached refuses an addon-affecting mutation by name
rather than accepting it with no effect. A disabled row is never compiled —
it reports `DISABLED` in `world.addons` (see "Verbs" below) but has no
runtime entry.

Boot uses the identical contract: `WorldAddonRuntime.TryCreate(definition, server)`
(called AFTER the `WorldServer` constructor has applied the document's grant
rows, so the mount-time disclosure reports a settled table) runs `TryPrepare`
against no prior state and `Commit`s it — an enabled row that cannot prepare
refuses the whole world installation rather than silently booting without it.
The composition root (`WorldPostBuildWiring.Install`) resolves the runtime
explicitly and turns that refusal into an ordinary attributed boot refusal
(stderr line, clean non-crashing exit) — the same `false` + printed-reason
shape every sibling boot gate uses; `Create` is the throwing wrapper
`TryCreate` powers for the one caller (the replay tape's `addonHostFactory`
delegate) that must return a value or throw. `world.undo` joins the same door: every addon-bearing
intermediate journal candidate is proved via a throwaway prepare-and-dispose
probe before the kept prefix is accepted, and the FINAL restored document runs
one real prepare-and-commit: a kept journal entry whose pinned module has gone
missing refuses the WHOLE undo. Per-row gates inside a prepare pass: enabled →
compile under the pin → the Response-channel gate (a row with `Requests` but no
Response channel refuses — no verdict could ever reach it) → the
capability-disclosure line (staged as a thunk — evaluated only at `Finish`,
against whatever grant table is live when it prints, so a rebuild's disclosure
reads the candidate's own newly-installed grants)
→ `Admit()` (runs `puck_init` under fuel, against the staged guest's own
private memory only — no host imports, contributions, handles, or output
escape) → receipt. Mount order is document order. The Wasmtime host is
constructed lazily on the first prepare pass that actually needs one (an
addon-free world pays nothing); a discarded plan disposes only the host IT
itself constructed, never one it merely reused.

**Narration is staged, not printed, until commit.** The mount/disclosure lines
below are built during prepare (against the settled grant table, which cannot
move before the caller's own synchronous commit) but held on the plan and
printed only once `Commit` actually publishes it — a refused mutation must
never have printed a mount claim that never became true.

The disclosure line prints for EVERY freshly-prepared row: requested vs
granted vs withheld vs `holds beyond its manifest (inert — never
materialized)`. **Requesting is not receiving** (deny-by-default regardless of
manifest), and **a hold outside the manifest mints no handle** — authority
materializes only at `requested ∧ granted`. See [authority.md](authority.md)
for the untrusted class rules (budgets required, handles, reach masks, no
wildcards).

## The three pump points

Guests are pumped ONLY from inside `WorldServer.Step`, at three pinned
points — which is what keeps guest driving reproducible under replay
WITHOUT recording it (the tape pins receipts and re-runs the guests):

1. `TickAddons` — first statement of `Step`: compose the input batch (tick
   cell, then last tick's staged pending), run `puck_on_tick`, decode and
   vocabulary-validate. APPLIES NOTHING.
2. `ApplyContributions` — after the intent drain: resolve Drive handles,
   check authority, fold acts per body, submit through the same
   `ApplyIntentSubmission` path seats use (on a human-occupied body the
   contribution enters the co-driving pool; on an unoccupied body it
   overwrites).
3. `ResolveReads` — after the population advances: disclosures → asks →
   queries, merged (budgeted, multi-part answers atomic) into the NEXT
   tick's batch, so a verdict, a minted handle, and a pose all describe the
   same settled instant.

## Channels and the wire

`AddonChannelKind`: `Input = 1`, `Request = 2`, `Response = 3` (ordinals 4
and 5 — the former Geometry/Overlay lanes — are retired permanently; a
descriptor naming them refuses the mount). Pairing rules: Request without
Response (or vice versa) refuses; declaring Input requires the
Request+Response pair (disclosures ride Response — an Input-only guest is
provably inert).

`WorldAddonWire.WorldAddonChannelResolver` is the ONE place guest channel
names meet the world's `WorldChannelTable`: the guest addresses its own
declared name table by position; resolution returns the world ordinal (role
channels at their fixed `ChannelRole` slots 0–5, composition channels from
6 up); unresolved is a `-1` sentinel and the declaration is reported inert,
never a mount fault. `Fold` writes `FixedQ4816.FromRawBits(act.Value)`
verbatim — the world channel's convention IS the wire convention, no
negation, no remap. `Submit` splits role ordinals into
`IntentSubmission.Intent` and composition ordinals into `HeldChannels`
(the same split `SeatController` uses). Every channel is per-tick
declarative — the host holds no cross-tick channel state; a guest that
stops emitting reads zero next tick.

## Requests, queries, verdicts

Output cells are `Act` (drive) or `Ask` (request a handle). The request
vocabulary (`Puck.Scripting.AddonAbi.RequestVerbs`) is closed and has TWO
verbs today: `BodyPose = 0` (a query, `AnswerParts = 4`) and
`SubmitMutation = 1` (`Count = 2`; a guest's declared `VerbCount` is a
non-empty prefix) — a guest holding a Mutate handle over a document section
acts through it with a JSON payload (kind ordinal + guest-memory pointer +
length in the request cell's `A`/`B`/`C` lanes) rather than a query.
**A guest CAN edit the document at the ABI/authority level** — this is not
withheld/inert. `Addons.WorldAddonMutationDecoder` wires 10 of
the 73 declared kinds today: the 5 HUD kinds
(`UpsertHudPanel`/`RemoveHudPanel`/`UpsertHudElement`/`RemoveHudElement`/
`SetHudDefaults`, ordinals 41-45), the 2 placement kinds
(`UpsertPlacement`/`RemovePlacement`, ordinals 19-20 — the FULL
`WorldPlacement` wire shape the document validator accepts: transform
(position/yawDegrees/scale), repeat, mirror, emission, solid, inhabit,
faceSources — including the full 8-variant `WorldScreenSource` union each
face source carries — region, and attach), the 2 state kinds
(`UpsertStateRow`/`RemoveStateRow`, ordinals 46-47, every non-generator
`WorldStateRow` variant: int/fixed/bool/text), and `SetInputHold`
(ordinal 48). Every OTHER declared `WorldMutation` kind still
decodes to `AddonMutateRefusal.DecodeFailed` → `Verdict::MalformedPayload`
regardless of grants or verb masks — a decoder gap, not an authority one;
wiring a new kind in is additive (a new `case` arm), never a change to the
decoder's own contract. The decoder's own division of labor: it turns wire
JSON into TYPED values and enforces a row's own intrinsic shape invariants
(a rect's width must be positive, a region's radius must be finite and
positive, a repeat's counts must be at least 1) — cross-referencing checks
that need the whole document (does `creationId` resolve, does a `state` row's
`Min`/`Max` pair validate, does a placement's `scale` sit inside the
authoring envelope) stay with `WorldDefinitionValidator`, exactly like the
HUD arms already left capacity/authoring checks to it.
`ObservationVerbs` has 11 members: `GrantedBody` plus the ten world-events
verbs the "World events" section below documents (`EventRegionEnter/Exit`,
`EventSeatJoin/Leave`, `EventCollisionBegin/End`, `EventRouteEngaged/
Disengaged`, `EventMachineMemoryChanged`, `EventGap`).

Ask resolution gates on the manifest BEFORE subject inspection — an
unrequested or out-of-range ask answers `AttenuatedToEmpty`, never
`NoSuchSubject` (which would be a body-enumeration oracle). Query order:
resolve handle → `IsRequested` → `Allows` → budget → dispatch.
`AddonVerdict`: `None`, `HeldConcrete`, `HeldWildcard`, `HeldAsReserver`,
`NoHold`, `BeatenByReserver`, `AttenuatedToEmpty`, `NoSuchSubject`,
`QuotaExhausted`, `StaleHandle`, `Applied`, `MalformedPayload`,
`PayloadTooLarge`, `Rejected`. An outer cell or ABI malformation faults the
whole batch and sticks the instance; a `SubmitMutation` pointer/JSON fault is
localized to `MalformedPayload` or `PayloadTooLarge`, and an authority denial
answers with its verdict cell. Starvation is not denial (a starved ordinal is
retryable). Handle pairs validate at APPLICATION, never at decode. The
`addon.mutate` refusal catalog declares 11 reasons and maps each to this wire
vocabulary through `AddonMutateRefusals.ToVerdict`.

`AddonCapabilityMask` bits: `Drive = 1<<0`, `Observe = 1<<1`,
`Reserved = 1<<2` (the permanently reserved hole where `Present` was —
never compacted, never reused; naming it in an Ask resolves to no
capability), `Control = 1<<3`, `Mutate = 1<<4`, `Edit = 1<<5`.

## Fuel

Per-row `Fuel` (0 → `AddonAbi.DefaultFuelPerTick` = 1,000,000). Measured
every tick regardless of outcome (determinism); totals saturate — a
lifetime figure that survives an unrelated reprepare pass reusing this guest
untouched (including a resubmission of the byte-identical row), and also
survives a sticky fault (a faulted guest is reused, not rebuilt, by an
unrelated reprepare pass — see "The row, mounting, and the prepare/commit
transaction" above), but resets for a guest a reprepare actually
re-instantiated (a `revision` bump or any other structural row change),
since that is a genuinely fresh instance. Exhaustion traps deterministically
→ `OutOfFuel`, a sticky fault: the guest is skipped every tick until its own
row's structural identity moves (bump `revision`, or change any other
field) — resubmitting the byte-identical row never restarts it. Fuel bounds
a guest's COMPUTE, never its authority.

## Receipts and replay

`WorldAddonReceipt(Name, Hash, Fuel)` — taken from the INSTANCE that
mounted, never the row; only guests that reached the admitted set get one.
The replay tape pins the receipts at record-start (and Drive re-checks them
before tick zero on every re-drive) and refuses a re-drive whose fresh
mounts disagree (see [replay.md](replay.md)). The ABI and replay format are
still version 1 and develop in place. Re-record tapes after changing their
layout or execution semantics; do not preserve obsolete fields or add
development version histories. A changed guest hash is independently
refused by the receipt comparison. A live `world.row.set addons`/`world.row.remove addons` mutation
is captured and re-executed on replay through the ordinary tape-covered
mutation leaf (see [replay.md](replay.md)'s mutation-outcome pin) — there is
no separate lifecycle leaf left to reason about arming against.

## Verbs

- `world.row.set addons <row-json>` / `world.row.remove addons <name>` — the
  ONE door. `Enabled` and `Revision` express the whole lifecycle: an absent
  row is unmounted; an `Enabled: false` row is disabled (never compiled); a
  STRUCTURAL change to an already-mounted, still-enabled row (content,
  `Revision`, or any other field — including `Requests`/`MemoryWatches`
  content) is a reload — fresh guest instantiation, memory wiped, lifetime
  fuel counters restarted; resubmitting the byte-identical row is a no-op
  for the runtime (the existing guest, healthy or faulted, is reused
  untouched). Buffers and applies at the tick boundary like any mutation;
  the addon-prepare gate (see above) is the LAST fallible step, so a bad
  row's refusal is loud, names the reason, and leaves the document (and the
  mounted set) untouched.
- `world.addons` — Immediate, ungated: one segment per DOCUMENT row, in
  document order — the joined configuration/runtime read-back, never a
  mounted-guest-only enumeration. A disabled row reads `DISABLED` with no
  cost figures; an enabled row always reads
  `<name> <ENABLED|FAULTED(detail)> fuel-budget fuel-last-tick fuel-total
  answers-dropped-total` — the cost surface — because an enabled row that
  cannot prepare refuses the whole mutation/rebuild/boot that would have
  installed it, so the document and the runtime can never disagree about
  what is actually mounted.

## World events

The host delivers world events as host-written `Observation` cells (verbs
1-12 on `AddonAbi.ObservationVerbs`, prefix growth beside `GrantedBody` —
the ABI pin never bumps). Five families are WORLD-scoped, collected once per
tick by `Server/WorldEventFeed.cs` after the population settles: seat
join/leave, region enter/exit (a placement's `WorldPlacementRegion` facet —
a named sphere, addressed by the carrying placement's own `Id`), collision
pairs (a flat proximity test — NOT the physical contact resolver, which has
no body-vs-body form here), control-application engaged/disengaged, and
federation link established/dropped.
Application edges are queued from `WorldGrants`' own set writes, so every
member added or removed fires one — including the context-button auto-engage
and a revoke-driven dissolution. Link edges compare a per-adjacency staleness
count against that row's authored `livenessGraceSeconds` (`0`, the default,
disables the row's sensing entirely, so an unauthored world emits none); `A`
is the adjacency row's 0-based document ordinal, `B` the staleness in
simulation ticks on a drop and `0` on an establish. The sixth,
machine-memory watches, is ADDON-scoped (`WorldAddonRow.MemoryWatches`, each
a `(screen, address, length)` row) and reads `Server.WorldMachineHost`
DIRECTLY (`WorldMachineHost` implements `IWorldMachineMemoryPeek` itself,
reached through the always-present `WorldServer.Machines`) —
**publishes in every boot shape**: machines boot and step server-side,
headless included, so
this family is no longer inert there — the former settable
`WorldServer.MachineMemoryPeek` seam, populated only when presentation
composed a screen binder, is gone.

A family materializes for a guest through the SAME requested ∧ granted rule
every other capability here uses: `WorldAddonRuntime.IsEventGated` checks
`IsRequested ∧ Allows ∧ TryGetEventBudget`. The gating subject IS the
family — `Observe/body:<n>` (collision + route), `Observe/region:<name>`
(enter/exit), `Observe/seat:<n>` (join/leave), `Observe/screen:<n>`
(machine-memory), `Observe/adjacency:<name>` (link established/dropped —
the authored `adjacencies` row's own name, Region's federation-seam twin) —
legitimate for UNTRUSTED principals only (no trusted
principal reads Observation cells). `WorldGrant.EventBudget` is a SIBLING of
`Budget` on the same row. Its numeric value is a nonzero admission gate, not
a consumed meter; it is REQUIRED (with `events:<n>` on `world.grant`) for
`screen:`/`region:`/`seat:`/`adjacency:` subjects — which still ALSO need the
pre-existing `budget:<n>` untrusted-Observe requirement, since that door
does not know a subject carries no query verb — and OPTIONAL on `body:<n>`.

**Overflow behavior: ordered prefix, drop-newest, per-mount
gap counter.** `EmitEvents` writes edges into whatever ring room remains
after reservations/disclosures, in `WorldEventFeed`'s pinned collection
order; once the ring runs out, the rest of that tick's qualifying edges
drop and a per-mount saturating `EventGapCount` increments. An `EventGap`
cell (verb 10) reports the count at most once per batch, only when it moved
since the last report and room remains; otherwise the current count is
reported by the next batch with room. No numeric per-subject throttle exists beyond the
ring — `EventBudget`'s value is an admission gate (nonzero), not a rate
limiter of its own.

Four of the five families are never taped — they re-derive from sim state
during replay. The link family is the exception: whether a neighbour
delivered is transport ingress no sim state determines, so the tape carries a
`LinkDelivery(adjacencyName)` entry per refreshed row per tick and the
re-drive feeds it through the same `WorldEventFeed.ObserveLinkDelivery`
entry point the live poll uses. The delivered CONTENT is still absent, so a
replay reproduces WHEN a seam went dark and never what the neighbour showed.
`world.grant`/`world.revoke`, which do ride the tape, carry
`GrantSubjectKind.Region`/`Seat` and `WorldGrant.EventBudget` through the
shared grant leaf.

## The shipped example

`src/Puck.World/Assets/addons/puck-addon-default.wasm` (source under
`wasm/puck-addon-default/`) — none of the four shipped worlds mounts it today
(the `default` world that once did was retired under the four-world charter);
it ships as an asset, ready to author into any world's `addons` row: the
dead-reckoning clamp-walk ghost — consumes the `GrantedBody` disclosure for
a Drive handle, asks for Observe, dead-reckons off pose answers, and emits
NOTHING when no pose is held (a refused grant leaves the ghost standing,
not guessing). Boot it into action with
`world.grant addon:default drive body:1 budget:60` plus
`world.grant addon:default observe body:1 budget:60` (both untrusted
budgets are required). Its `Mutate/section:kits` request is the worked
withheld-request example.

## Puck.Scripting in one paragraph

`src/Puck.Scripting/README.md` is the single home of the ABI contract:
cell/descriptor byte tables, the guest exports, the ten-step mount order,
the per-tick batch order, fault-vs-verdict postures. Determinism: Wasmtime
pinned EXACTLY (the package version is the native engine version; fuel
accounting is codegen-dependent, so a silent bump can move the
fuel-exhaustion tick and break stored replays — the pin never floats);
every knob explicit (fuel on, threads/SIMD off, NaN canonicalization on);
NO host imports (`AddonModuleValidator` refuses any import, keeping guests
runnable in any wasm runtime); no float crosses the boundary
(`FixedQ4816` raw i64).
