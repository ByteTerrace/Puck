# Capability channels

**The plan of record for reworking input, commands, addons, and UI onto one
contribution model.** It supersedes `addon-input-plan.md`, whose live content is
carried below.

This document states the model, the current contracts, and the forward work.
The campaign's decision record — closed decisions with their proofs, the
design-round histories, the landed-then-corrected narratives, the Phase 0
cleanup, and the verification war stories — lives in git history; the earned
general verification doctrine lives in
[agent-guide.md](agent-guide.md#verification-doctrine). The plan states the
model and the forward work; it does not restate history.

Puck has one contribution mechanism implemented four times. Commands are 24-byte
records decoded into a registry. SDF content is an instruction stream packed into
words. Overlay content is fixed-word records packed into one storage buffer. World
queries are the same thing inverted. Each was built well and separately, so each
invented its own capacity discipline, its own authority posture, and its own
failure mode:

| Surface | On overflow | On authority | On name collision |
|---|---|---|---|
| SDF program | loud `ArgumentException` at upload | none — trusting in-process API | n/a |
| Overlay records | narrated **once per node lifetime**, then silent forever | none | n/a |
| Commands | n/a | three different idioms across modules | **silent last-writer-wins** |
| Addon input | loud, attributed, sticky fault | grant-checked under the addon's own principal | n/a |

The addon path is the only one that already gets this right, and it is the only
one with an untrusted producer on the other side. That is not a coincidence — it
is the shape everything else should have been built in.

## The model

### Who the adversary is, per principal class

Stated first because everything below depends on it, and because leaving it
implicit let two incompatible stances coexist in earlier drafts of this document.

| Principal | Trusted? | What authority means for it |
|---|---|---|
| `Console` | **Fully.** An operator who can grant themselves anything. | Grants are **honesty**, not security. A revocation must still be *truthful* — see below. |
| `Seat` | **Yes**, locally. A human at a controller. | Seeded permissive so local play is not gated until someone chooses to narrow trust. |
| `Addon` | **No.** Third-party code. | This is where the security boundary lives, and the only place handles are load-bearing. |
| `Peer` | **No**, once a socket transport exists. | Same posture as an addon; not yet reachable. |

Two consequences worth stating plainly, because they cut in opposite directions:

**Console revocation is not a security operation, but it must not lie.** When
`world.revoke console mutate section:audio` succeeds and `world.volume` still moves
the gain and still persists it, nothing was *breached* — the operator could have
re-granted it in one line. What broke is that the system said "revoked" and then
behaved as though it had not. That is a correctness defect in revocation semantics,
and it matters because scripted proofs assert on refusals and because any future
sandboxed console (a stdin driver you want to constrain) inherits the lie. Fixing
it is worth doing on those grounds, not on security grounds.

**Handles are for the untrusted side only, and the disease is currently on the
trusted side.** The ungated session levers live in console modules. Addons — the
population that *gets* handles — have none of them, because their input already
routes through the grant-checked driver. So the handle model does not close the
lever family; **routing those verbs through the server does**, and that is a
grant-table fix. Handles prevent the idiom from ever appearing on the untrusted
side, where it would be a genuine breach rather than an honesty defect. Both are
worth doing. They are different repairs to different populations.

### Authority is a handle, never a name

This is the load-bearing change, and it is a genuine change of kind.

`WorldGrants.Allows(principal, capability, subject)` is an **access-control
list**: the producer names a subject and a table decides. That means the producer
can *name anything* — authority is ambient, and the only thing standing between a
contributor and a subject is a lookup. It is the structure that produces
confused-deputy bugs, and it does not survive contact with contributors you do not
trust.

A capability system inverts it. The host builds each addon a **handle table** at
mount. The guest references entries **by index into its own table** and cannot
construct a reference to anything else — the index space is per-addon and the host
owns the mapping. An addon does not say `body:3`; it says `handle 7`, and the host
resolves that to whatever it placed there.

**A handle resolves a designation, not a decision.** The entry names a
`(capability, subject)` pair plus its vocabulary subset and quota; the host still
calls `Allows` at use time. This matters concretely: `Allows` carries an
exclusivity override, so a *cached decision* goes stale the moment another
principal exclusively reserves the same subject, and keeping it coherent would mean
invalidating every handle table on every grant and revoke. Resolving only the
designation costs nothing to keep correct and loses none of the security property —
a guest still cannot name what it was not handed.

It also settles the obvious objection. **The handle table governs naming; the grant
table governs deciding.** One decision procedure, one record of who may hold what,
and a second structure that exists purely to bound what a producer can say.

What this buys, in order of importance:

- **No ambient authority.** What an addon can *say* is exactly what it was
  granted. Restriction stops being a check and becomes an absence.
- **Designation and authority are one.** There is no gap between naming a thing
  and being allowed to act on it, so there is no confused deputy.
- **Revocation is a cleared slot.** O(1), immediate, and the next reference
  resolves to nothing — loudly, attributed, never silently no-op.
- **Attenuation is expressible.** A handle may carry strictly less than the
  grantor holds: drive this body but only these movement sources; read this
  subject but only its pose.
- **Enumeration is itself a capability.** An addon cannot discover what exists
  unless handed a handle that enumerates. Today an addon implicitly knows its
  slot and its body; under handles it knows nothing it was not given.

**This does not become a second authority system — but only if the derivation is
mechanical, so make it mechanical.** The grant table stays the record of who may
hold what. A handle table is a **pure projection** of it for a principal outside
the trust boundary, rebuilt when the grants it projects change. Two properties make
that true rather than asserted:

- **There is one write path.** `world.revoke` is the only way authority is
  withdrawn; a "cleared slot" is what the projection *becomes*, never an
  independent edit. Because a handle resolves a designation and `Allows` still runs
  at use time, a revoked grant takes effect whether or not the projection has caught
  up — the table can never be stale in the permissive direction.
- **Attenuation lives in the grant row, not the handle.** A handle that carries a
  vocabulary subset and a quota records authority facts today's rows cannot express,
  and if those facts live only in the handle then there genuinely are two records.
  So **the grant schema grows to carry them** — subset and quota become part of what
  is granted, reviewable in the document, and the handle projects them like
  everything else.

This is the plan's own distinguishing test applied to itself: *is there a mechanism
enforcing agreement?* Projection passes. Two write paths reconciled by prose would
not.

Principals inside the trust boundary keep naming subjects directly — the console is
an operator who could grant themselves anything, so handing them handles buys
nothing. Note the failure class that leaves open: a grant row the console grammar
cannot name is unaddressable today (`GrantSubject.Composition` is exactly this) and
would be *unmaterializable* under handles. Grammar completeness on the trusted side
is a live concern that handles never touch.

Delegation between addons is *expressible* in this model and deliberately not
enabled at first. The model must not preclude it; the initial grant surface must
not offer it.

**The motivating case a world author actually cares about — assist versus
cheat, settled in an owner design conversation (2026-08-01).** The first-order
intuition is that capability cannot tell them apart, because both hold `Drive`
— and at coarse grain that is true. It is wrong at the grain this model
provides, because **the difference between an assist and a cheat is not what it
DOES, it is what it SEES.** An accessibility assist observes the participant it
assists — your aim, your velocity, your own body. An aimbot observes everyone
*else*. A wallhack is pure `Observe` with no `Drive` at all. Same `Drive`,
disjoint `Observe` scope — so the lever a world author actually wants is the
`Observe` vocabulary subset and its subject scope, and that maps exactly onto
this section: an addon handed an `Observe` handle over its own participant
**can assist and structurally cannot aimbot, because it was never handed a way
to name anyone else.** Restriction as absence, doing product-level work.

Two adjacent conclusions from the same conversation, recorded here so neither
gets rebuilt as a separate mechanism:

- **Admission is a degenerate case of capability — never build both.** "May
  this addon run in my world" needs no allowlist: an addon granted nothing
  mounts, discloses exactly what was withheld (the mount line already names
  it), and does nothing. That IS "not allowed", and it is more honest than a
  refused mount because the disclosure is inspectable. `WorldDefinition.Grants`
  plus deny-by-default already is the allowlist at coarse grain.
- **Ergonomics is deliberately open** (Open Decision 7): a grant an author
  cannot write correctly gets over-granted into working, so named vocabulary
  sets are probably needed — and designing the naming scheme before Phase 2's
  actual vocabularies exist would fit them badly. Decide it when the
  vocabularies are real.

### A channel

A channel is `(principal, direction, vocabulary, capacity)` **on a lane that is
part of its type, not a field on it** — a bounded, owned region a producer writes
typed records into, or the host fills for it.

The type-parameter form is load-bearing: **a field cannot make a type not
connect.** Lane is `ChannelSpec<TLane>` with phantom `SimulationLane` /
`PresentationLane`, distinct mount entry points returning distinct lease types,
and separate producer contexts where the Presentation context simply has no
writer to hand you. Otherwise the fence below is prose with extra steps. (An
earlier draft listed lane as a tuple member; the correction is recorded in the
ledger's type-system round.)

Channels unify **ownership, capacity, validation dispatch, authority, and failure
attribution**. They deliberately do *not* unify record encodings: four record
shapes stay four record shapes, because the shapes are the part that is already
right.

### Lanes: Simulation and Presentation

Every channel declares one.

- **Simulation** — fixed-point only, replayable, may produce commands. Values on
  this lane are part of the determinism contract.
- **Presentation** — may read presentation-derived state, may never feed a
  command.

The fence exists today only as prose spread across class comments. Making it a lane
makes it structural **for channel-mediated producers**: a Presentation channel type
exposes no command-emitting member, so the path does not exist rather than being
forbidden by a comment.

**A GUEST MAY MOUNT CHANNELS FROM EXACTLY ONE LANE.** This is the fence's real
enforcement and the plan was broken without it. **A guest holding both lanes is
itself the bridge.** C# types fence the host surface; they cannot fence the inside
of a WASM instance. The guest reads presentation-derived state on its Presentation
channel, does arithmetic on it in its own linear memory, and emits an input record
on its Simulation channel — no host type is violated at any point, and presentation
state has entered simulation input. The determinism contract dies without a single
rule being broken.

So an addon wanting both runs as **two instances**, with separate linear memories and
no shared state. Two independent designs reached this conclusion. The rule binds
**instances, not module bytes**: the same compiled module may legitimately mount once
in each lane, so no validator may treat the module itself as lane-bound.

**LANE ≈ TIER — the lane split maps onto the client/server split, and this is a
decision, not an observation (owner-accepted, 2026-08-01).** Simulation-lane
addons must execute SERVER-SIDE, because that is the only place a hosted world's
constraints are enforceable at all: a local player owns their process and their
file — not a threat, not worth defending — while a hosted session's server-held
document is the only copy that counts, and an allowlist can only bind addons the
server RUNS. (A client running a cheat in its own process and submitting the
resulting intents is indistinguishable from a very good player; that boundary is
the intent stream, not the addon host.) Presentation-lane addons run
client-side, where rendering lives (`WorldFrameSource : ISdfFrameSource` is a
`Client/` type — verified). One-lane-per-instance therefore stops being an
awkward restriction and becomes *these run in different tiers*. Consequence for
the tree, EXECUTED in unit 4b: guest hosting lives in
`Server/WorldAddonRuntime`, pumped by `WorldServer.Step` — a World-internal
move the assembly split was indifferent to (the adapter references no
`Puck.World`).

**The fence scales with deployment, and it is strongest exactly where the
threat is.** In a hosted session, Simulation and Presentation guests live in
different PROCESSES, so "a guest holding both lanes is a bridge" becomes
impossible by deployment — the type fence and the mount gate degrade to
defence-in-depth over a real process boundary. Locally, client and server share
one loopback process and the fence is only the type system — which is exactly
the case where the player owns everything anyway and no fence was ever
meaningful.

**Where one-lane-per-instance is enforced — decided by a second blind-design round,
2026-08-01.** Not by caller discipline, and not by the manifest alone. Two gates,
neither subsuming the other:

1. **Mount, before the guest ever runs.** An immutable bound lane is fixed on the
   instance before instantiation completes, and every channel descriptor the guest
   declares is checked against it during the existing decode-once handshake. A
   descriptor carrying a second lane **refuses the whole mount atomically**, loud,
   naming the module and both lanes — never ignore-the-extra-channel, which would
   leave the guest believing it holds channels the host will never serve.
2. **Document parse.** An addon row requesting channels from both lanes is rejected
   at validation, which puts the error in front of the author. This gate is
   diagnostics, not security: it catches the authoring mistake, while the mount gate
   catches a rebuilt or hand-edited module. Both exist.

**Which fence protects whom — three producers, three mechanisms, one sentence each.**
Stated separately because a single claim covering all three is true of only one:

- **A WASM guest is fenced by the ABI, and the lane type parameter buys it
  nothing.** A guest declares zero imports — enforced today, the load-bearing
  check — so it cannot call into C# by any mechanism; it writes bytes into its own
  linear memory and the host alone decides what they mean. What actually governs a
  guest is which host-side decoder the host attaches to its channels' vocabulary.
  The determinism contract is protected from an addon by the ABI, never by C#
  types.
- **A channel-mediated first-party producer is fenced by the lane type
  parameter** — a Presentation producer context exposes no command-emitting
  member, so the path does not exist rather than being forbidden by a comment.
  This is real, and it is hygiene with compile-time teeth, not a security
  boundary, until the next item lands.
- **Unmediated first-party C# stays on the prose fence until the ingress closes —
  and closing it is a PROJECT-GRAPH change, not a type change.** `Puck.World`
  shares an assembly with the server, so anything inside it reaches the
  submission surface directly and no phantom type can intervene; a producer
  holding `IServerLink` has `SubmitCommand` regardless of what its constructor
  declares; a producer handed `IServiceProvider` resolves the live registry
  regardless of what it asked for; and `AddonHost.Instances` hands out raw
  instances whose public members erase the lane. The fence therefore requires the
  registry's mutating surface closed (the Phase 2 ingress item — the two are ONE
  item), a neutral channel core that does not reference `Puck.Commands`, and a
  presentation-driver assembly that references neither `Puck.Commands` nor the
  server's mutable protocol types — never a broad `InternalsVisibleTo`, which
  re-exposes exactly the surface being closed.

**The Simulation ABI is stricter at the RECORD, not at the instruction stream.**
Fixed-point-only is enforced where the host already reads every record — the
decoder — attributed and loud, with no new machinery. A no-float-instruction scan of
the module itself is **not buildable today** (the validator sees Wasmtime module
metadata — exports and imports — never code bodies) and buys little: the scripting
host already disables threads and SIMD and canonicalizes NaNs, so internal guest
floats are deterministic at the pinned runtime version, and a guest computing in
floats still crosses the boundary through the fixed-point record the decoder
validates. **A Simulation addon may use scalar floating point internally** — said
plainly so nobody builds a bytecode parser to close a gap the decoder already
closes. If an authoring-toolchain float check is ever added, it is defense-in-depth
whose verdict must bind to the exact module hash, and it is explicitly deferred.

**Geometry cannot reach collision, and that is structural — preserve it.** The
concern was that scene SDF is not purely presentational (a solid-affecting edit does
rebuild the server's collision field), so a Presentation-lane `geometry` channel
might breach its own fence. Checked: it cannot. `WorldSolidField.TryBuild` compiles
its **own** program from exactly four document sources — the ground half-space,
scene rows carrying a non-null `Solid` facet, screens with `Solid`, and placements
with `Solid` as reach-sized proxy spheres. It never reads the composed render
program, so the emitter/composition path a channel would write into has no route
into collision at all. Solidity is opt-in per document row, reachable only through
section-gated `Mutate`.

(The doc line that invites the opposite reading — *the contact surface a body solves
against IS the rendered geometry* — means the solid rows are compiled from the same
shapes so the two surfaces agree for authored content. It does not mean the render
program is the collision source.)

So **channel-contributed geometry is never solid** is not a constraint to add; it is
what the code already enforces. Two changes would break it, named so neither happens
casually: wiring channel output into the solid-field build (or adding a
`Solid`-bearing channel record kind), and the subtler **placement route** — see the
front-door section.

### Vocabulary is a capability

Each channel's vocabulary is not "what the engine knows" but "what this addon was
granted." Input source ids, query verbs, SDF operations, overlay record kinds —
each is a set, and each grant names a subset. The decoder rejects anything
outside it, attributed and loud.

**SDF operations are capabilities, and the fit is better than it first looks.**
`SdfViewsKernelVariant` compiles a register-lean `CoreOps` kernel and a `Full`
one, and the *first* exotic op anywhere in the composed program pins everything to
`Full` — so one addon's `CellJitter` costs occupancy on every pixel of everyone's
frame. That makes the exotic op set exactly the set you withhold by default. At the
**default posture** the least-authority grant and the least-cost grant are the same
grant, which is rare enough to build on.

**They diverge maximally at the margin, and quota cannot express it.** Granting one
addon one exotic op is minimal *authority* and maximal *cost* — it re-pins the
kernel variant for every pixel of every frame, including for contributors who were
granted nothing. That is a global externality, and "quota is a property of a handle"
is structurally incapable of capturing it: no per-holder budget says *your grant
changed everyone's kernel*.

So an exotic-op grant is a **world-level admission decision that moves the shared
envelope**, never a handle-level one — the same shape as the frozen probe envelope
that per-handle record budgets must sum under. Phase 3 cost admission has to carry
this explicitly, or it gets built per-contributor and the first `CellJitter` grant
surprises everyone.

The vocabulary catalogs themselves must be **generated from their single home**,
not hand-written and checked. **Done for the input vocabulary:** `AddonSourceCatalog`
no longer switches over `InputSources` by hand — every member carries an
`InputSourceValueAttribute` (its `CommandValueKind`) and, where the ABI cannot carry
it for a reason beyond that kind, an `InputSourceUnaddressableAttribute`, and the
catalog derives its resolution table from those once, by reflection. Adding a
control is a one-line attribute at its declaration, never a second edit in
`Puck.Scripting`; an unattributed addition still fails loudly through `Puck.Post`'s
independent `scripting-determinism` completeness leg. This is the area's own earned
rule — *find the mechanism the engine already had rather than re-sync by hand* —
applied to the catalog itself, and the template the SDF op-vocabulary catalog
(below) and any future generated catalog should follow.

### Quota is a property of a handle

Not a separate budget system. A `Present` handle carries a record budget; a
`Drive` handle carries a command budget. A producer cannot overspend because the
handle states what it holds, and exhausting it is attributed to the holder rather
than starving a shared pool.

This replaced the overlay posture directly, and unit 6b landed that replacement:
the old shape was one shared pool with no per-surface quota, where overflow was
narrated once and then dropped silently forever — two contributors and a HUD, and
you could not tell who ate the budget. Overlay capacity is now 1024 elements /
16384 text words / 16 panels / 32 clips, and those ceilings are a
**cannot-overflow backstop, never a budget**: the per-channel reservations are
the budget, and the gap between their sum and the ceiling is explicit Phase-3
headroom no first-party writer may draw from.

Remaining quota values are semi-arbitrary and adjustable — the roster's four
slots, 64 declared sources, 64 command records per tick. None are load-bearing.
Do not design around them; size them from the model.

### A decision is data, never a boolean

Contributed by the orchestrating session, 2026-08-01, on the owner's invitation —
argued from the campaign's own failure pattern (two distinguishable
authority-states collapsed into one observable; the full recital is in the
ledger). `Allows` decided four distinct ways — the exclusivity override matched
the caller; the override matched *someone else*; no row names the subject; a row
or the wildcard hit — and returned a bare `bool` that erased the path before any
caller could report it, so the fix lands at the return, never at the messages.

The contract: `Allows` returns a small verdict — which rule fired, stack-only,
zero-alloc, implicitly convertible to `bool` so the hot path reads unchanged.
Three rules make it load-bearing rather than decorative:

1. **The verdict is a byproduct of deciding, never a re-derivation.** It comes
   from inside `Allows`, on the same control path that decides. A parallel
   `Explain` function would be two implementations of one decision — the
   "correction in prose while the artifact survives" failure, built into the
   API.
2. **Every refusal surface prints the verdict's reason, not its own.** Denied-
   by-reservation names the reserver; denied-for-absence says absence. Phase 2
   inherits this directly: a refused channel record carries the verdict, so
   batch attribution is data ("record 3: reserved by seat1") rather than prose.
3. **A `world.why <principal> <capability> <subject>` verb echoes the verdict
   plus the rows that produced it** — an Immediate, pipe-assertable read, so a
   probe asks the server which rule fired instead of inferring it from motion.

The product payoff is the same mechanism wearing a different hat: the worked
consumer below gates reveals on grants, and a reveal ladder's *"why is this
locked and what would unlock it"* answer derives from the verdict — generated
from the same table that enforces, so a hint system that drifts from enforcement
is structurally impossible. A second payoff: once every authority refusal
carries its verdict, **silence becomes a positive signal** — "no refusal at
all" means *authority was fine, look elsewhere* (earned from the boulder case;
see the ledger).

**Landed (task #10), proven live over the pipe** — `GrantVerdict` (every one of
the fourteen call sites compiled unchanged), every refusal surface, an
`AllowsAllSections` that names the FIRST refusing section, and `world.why`, all
five rules exercised against a live world; the proof transcript is in the
ledger. The once-per-episode latch stayed outside the verdict, and the
constraints below are stamped into `GrantVerdict`'s own doc.

Deliberately NOT proposed: recording verdicts per-tick into the replay stream.
Verdicts are re-derivable exactly like query responses — derive, never record —
but stated bare, the rule is FALSE. Three constraints keep it true:

- **A verdict is a function of (state, position-within-tick), and pinning only
  the tick loses the second coordinate.** Grants and revokes apply synchronously
  inside the command-apply window, so one tick can hold: A denied because B's
  exclusive reservation stands → B's reservation revoked → later checks see a
  different table. Re-derive that first verdict from end-of-tick state and it
  reports no-row where the live decision was beaten-by-reserver. The repair is
  the drain-point repair: **pin the position within the tick that a
  re-derivation replays to.** Queries, channel ordering, and now verdicts — one
  requirement the model keeps rediscovering, not three; any future "re-derive
  it later" claim owes its position pin at the moment it is made.
- **A verdict may depend only on Simulation-lane state** — the mirror of the
  query-verb rule. True today (grants are simulation state); the day a refusal
  reason derives from an attached device, a render setting, or frame timing,
  re-derivation diverges silently and the rule dies.
- **The once-per-episode latch (`m_driveDenied[]`) is NOT part of the verdict
  and must not be folded in.** Whether this refusal is the first of its episode
  is *reporting* state — not re-derivable from the grant table at all. Verdict
  says which rule fired; the latch says whether to print.

### The degradation posture to copy

`ViewStack` already solves multi-tenant contention correctly: 64 named
registrations against 4 real renders per frame, round-robin, and a view that
misses its turn **serves its last resolved handle rather than going black**.
Bounded cost, graceful degradation, no silent loss. Generalize that, and retire
the narrate-once-then-drop posture wherever it appears.

**Ruled (Phase 4 close, 2026-08-02): round-robin does NOT generalize to the
addon answer ring, and that is a correct divergence, not a shortfall.**
`ViewStack`'s contention is MULTI-TENANT — many registrations sharing a scarce
render slot ACROSS FRAMES, where serving last-resolved is safe because
presentation has no determinism contract to keep. The addon answer ring is
ONE guest's own traffic within ONE tick, answered or not THAT SAME TICK — there
is no "next turn" for a dropped ordinal to eventually receive, and the traffic
is Simulation-lane, where what a guest consumes must never depend on who else
was busy. The fix that landed instead: the ring's own single-cell
`QuotaExhausted` squeeze (pre-existing, already correct) is unchanged, and its
provably-irreducible residual — a group with no cell left, ever, under the
guest's own declared `puck_in_cap` — became a durable per-addon lifetime
counter (`world.addons`' `answers-dropped-total`), never a wire-level
aggregate: the ABI's ordinals are pinned to have no reserved value, so a
many-to-one "N things refused" cell cannot be added without either growing the
ABI or lying about which ordinal it answers. The narrate-once-then-drop
posture IS retired at both sites this campaign owned in `WorldAddonRuntime`
(`QuotaDropReported`, `UndeliverableReported`); the fenced sites
(`WorldServer.m_driveDenied`, `m_contended`) are a separate lane's own change.

## The channels

**DELETED, not deferred (owner ruling, 2026-08-02 — the L5 landing).** The
Presentation lane below — the `geometry`/`overlay` channels and the `Present`
capability that gated them — is GONE, not merely unbuilt: `AddonChannelKind`
ordinals 4/5 retire permanently, `WorldCapability.Present` and its
`AddonCapabilityMask` bit are deleted (the mask bit becomes a permanently
reserved hole), and every guest mounts Simulation channels only. The two-lane
framing this section used to plan against no longer describes where the design
is headed; it is kept below as the historical shape the deletion corrected, not
as forward work. See the campaign ledger's L5 entry for the full inventory.

Simulation lane (the only lane a guest mounts today):

| Channel | Direction | Vocabulary | Held by |
|---|---|---|---|
| `input` | guest → host | input source ids | `Drive` handle over a body |
| `request` | guest → host | world-query verbs | `Observe` handle over a subject |
| `response` | host → guest | — | paired with `request` |

Presentation lane (HISTORICAL — deleted, never built):

| Channel | Direction | Vocabulary | Held by |
|---|---|---|---|
| `geometry` | guest → host | SDF ops and shapes | `Present` handle over a scope |
| `overlay` | guest → host | overlay record kinds | `Present` handle over a region |

The `request`/`response` pair is how reads happen without breaking *an import is
arithmetic or it does not exist*. The guest writes request records during its
tick; the host drains them **after the authoritative step of that same tick**,
resolves each handle, dispatches to `IWorldQuery`, and fills `response` before the
guest's next tick — the pinned drain point, not "some time after" (the Phase 2
decisions derive why that exact point is the only latency-neutral one). One tick of
latency, zero imports preserved, and it reuses `IWorldQuery` as-is for the spatial
verbs — five of them, already fixed-point in and out, already bit-identical across
both providers.

**"Unchanged" was an overclaim and is withdrawn.** `IWorldQuery` is five *spatial*
verbs; anything that needs to read progression flags, counters, or raise ticks — the
reveal conditions this plan specifies — is not expressible in them. The `request`
vocabulary therefore grows beyond `IWorldQuery` rather than being it. What must stay
true is the *discipline*, not the interface: every query verb a pure function of
Simulation-lane state, fixed-point across the boundary, and answerable identically on
re-derivation. Growing the vocabulary is fine; growing it with a verb that reads
presentation state would kill replay.

Replay survives because responses are **re-derived** from authoritative state at a
known tick, never recorded. That holds only while every query is a pure function
of Simulation-lane state, which is a constraint on what may become a query verb.

## Capabilities

Five verbs, each parameterized by a handle and a vocabulary subset:

| Capability | Subject | Vocabulary subset |
|---|---|---|
| `Drive` | body handle | which input sources may be emitted |
| `Observe` | subject handle | which query verbs may be called |
| `Control` | screen handle | — |
| `Mutate` | section handle | — |
| `Edit` | state-row handle (`state:<name>`) | — |

A sixth, `Present` (scope handle; which SDF ops / overlay record kinds may be
emitted), was declared alongside `Observe` and DELETED without ever gating a
draw path (owner ruling, 2026-08-02, the L5 landing) — no `geometry`/`overlay`
channel was ever built to check it against, so the vocabulary retired rather
than the enforcement landing. `Observe` is the one that stayed. Reads are
wholly ungated today outside the addon path — `WorldServer.Answer` has no grant
check, because every reader is in-process and trusted. That assumption dies the
moment a contributor reads anything.

Granularity lives in the vocabulary subset and the subject handle, not in the enum.
Five coarse verbs × arbitrary vocabulary subsets × specific handles is finer control
than twenty verbs would give, and it does not grow every time a capability is
carved thinner.

**Deny by default.** Addons already receive nothing at seed; that stays and
extends. A manifest *requests*; a grant *approves a subset*; nothing is implicit.
**Landed** for the coarse (capability, subject) vocabulary that exists today —
`WorldAddonRow.Requests` is the manifest, `WorldDefinition.Grants` is where a
world ships an approved subset, and a row declaring requests is verified to hold
nothing until something actually grants it — the shipped default world is the
worked example, observable on every boot: its addon declares three requests and
ships zero grants, and the mount-time disclosure line reports all three
withheld (the proof-suite subcommand that used to script this went to
`experimental/` with the rest of the quarantine). What remains open is the finer vocabulary-subset grain
(input sources, query verbs, SDF ops, overlay kinds) Phase 2's channels add —
today's subset IS the subject, since the coarse six verbs are what exists to
request or grant.

### The roster slot is no longer the unit of existence

An addon needs a `Drive` handle to move a body. It does not need a **roster
slot** — that is an input-routing artifact of how human pads reach seats, and
it is no longer the gate that decides whether an addon exists.

**Landed, and unit 4b finished the thought: the roster is out of the addon
path entirely.** `Server/WorldAddonRuntime` ticks every mounted, enabled addon
inside `WorldServer.Step` — mounted-and-channelled is existence, and no addon
claims a slot at all (the roster's claim machinery survives for the editor and
replay devices, which still claim through it). A driving addon acts through
its disclosed Drive handle; its typed records are validated by the Simulation
pump and applied through the same `ApplyIntentSubmission` gate a seat's drain
runs — a `Drive` grant is still what decides whether anything actually moves,
and the manifest bounds what can materialize at all (requested ∧ granted). An
addon with no materialized handle still ticks every tick; an act through a
stale or fabricated handle is refused loudly with its verdict answered back,
instead of being silently dropped or the addon never existing at all.

Mounted-and-channelled is existence; holding a `Drive` handle is driving. The
two are independent, and a read-only or presentation-only addon has a home
that never depended on the local seat count — the ceiling was never a number
to raise, it was a gate to remove.

## Content as documents

`SdfInstruction` is already a flat record of primitives — op, shape, blend,
material, two vectors. Nothing serializes it, so contributed geometry has no
representation and every emitter is hand-written C# calling an imperative builder
that accepts a NaN radius without comment.

`puck.sdf.v1` makes contributed geometry expressible, reviewable, cacheable, and
above all **validatable before it reaches the builder**. The same argument applies
to overlay records, whose vocabulary is already closed at five kinds.

This is what makes *no raw draws* stop being a restriction: a contributor hands
over the same instruction stream first-party emitters produce, and the engine
bound-checks, Lipschitz-analyses, and costs it exactly as it already does.

The trust boundary lands cleanly — **first-party C# calls the builder; documents
deserialize into builder calls through a validating front door.** One evaluator,
two doors, one of them untrusted.

### What the front door inherits

Four standing verdicts in the SDF wiki constrain it. They are not re-openable
here, and the decoder enforces them rather than inventing its own policy:

- **Unbounded procedural displacement is rejected.** *"Procedural detail is
  acceptable only when its hash, amplitude, derivative, and deterministic replay
  behavior are explicit."* So the parameter validation for `CellJitter`,
  `Displace`, and `DomainWarp` is an *enforcement of that rule*, not a second
  definition of safe.
- **Per-tile instruction-tape pruning is rejected** for ordinary programs. Cost
  admission is a build-time, per-contributor ceiling; tape pruning is a runtime,
  per-tile specialisation. Different mechanisms, different questions — say so in
  the Phase 3 design or a reviewer will read one as reviving the other.
- **Backend-conditioned vocabulary is rejected.** A grant is a subset of the *same*
  ISA on Vulkan and Direct3D 12. Nothing forces that symmetry structurally today,
  so the grant schema must.
- **Voxel and clipmap blobs are rejected as a core representation.** The
  `geometry` channel carries instructions, never a baked brick.

**The placement route is how contributed content could still reach collision.** A
solid placement's proxy sphere takes its radius from the referenced creation's
geometry reach, scaled. So if channels ever contribute *creation* documents and an
authored solid placement references one, the contributed document influences the
collision field through that reach — a hostile creation inflating its bounds fattens
a solid proxy. Opting in still requires an authored, `Mutate`-gated row, so this is
not a bypass; but the radius becomes attacker-influenced, which is enough. **A
creation referenced by any `Solid` placement gets its reach validated and clamped at
the compose boundary.**

**The solid field is precedent for vocabulary-as-capability, not an exception to
it.** Which ops may be solid already has a hard ceiling: the field evaluator is
warp-free and rejects the excluded set, and `TryBuild` forwards that rejection
verbatim as a loud apply-time refusal. Authored solid geometry therefore already
passes an op-vocabulary gate — the same shape this plan proposes for channels,
shipping today.

**Hardening is not just finite checks.** `SampledRegion`'s `brickWordOffset` lane
is the brick's base word *in the host's pool* — a raw internal resource address.
Brick allocation is host-arbitrated and never author-chosen, so a document that
could emit that shape unchanged would be naming memory it has no business
addressing. Parameters that encode host-internal addressing must not round-trip
through an untrusted decoder at all; they are resolved by the host or the shape is
outside the vocabulary. Find the rest of this class before Phase 3, not during it.
**Found (2026-08-01):**
[the host-addressing survey](reviews/2026-08-01-sdf-host-addressing-survey.md)
classified every author-suppliable parameter (~155, plus 16 host-baked derived
lanes) — the class has four more SDF members (`packedDims`, `screenIndex` and
its material-lane back door, the dynamic-transform slot pair, instance tape
ranges), three derived overlay members (glyph start/count, clip index), and one
adjacent (`cellBase`). The containing rule, which also resolves the derived-lane
sub-class the plan had not named: **the document vocabulary is the
builder/writer argument surface with host-assigned slot bases; packed lanes and
table indices never round-trip.** The survey's class-C list is the decision
sheet Phase 3's design round starts from.

**Quotas compose under a frozen envelope.** GPU buffers freeze at construction from
a worst-case probe, and every optional emission branch owes an equivalent probe
branch. A per-handle record quota is a *grant-time* ceiling; the probe envelope is
a *construction-time* one. The sum of live quotas must never exceed the envelope —
raising a quota without raising the probe reproduces exactly the outgrow-the-buffer
failure the capacity doctrine already documents.

**Reuse the scoping precedent.** `BeginMaterialScope`/`OwnsMaterialScope` already
solves "one shared builder, several contributors who should not reach into each
other" for material palettes. The op-vocabulary and instance-budget problem is the
same shape one layer up. Cite it; do not re-derive scoping.

**One caveat on the vocabulary's source.** The C#↔HLSL sync-pair table is
hand-maintained prose, which fails this plan's own distinguishing test. A generated
op-capability catalog built by reading that table inherits its drift risk until the
ISA enum and the `CoreOps`/`Full` classification are its single generated home.

## Phases

**0 — Cleanup.** **Done.** Building on a dead schema or two modifier systems
would have poisoned everything after it; the full cleanup record — schema and
modifier unification, the deleted unreachables, the missing-guard findings and
their controlled reproductions — is in git history. Two obligations it leaves
open remain live here: the **session-lever routing
decision** (whether `world.volume`, the render levers, and `world.save` route
through the server or acquire an explicit check — a revocation-honesty repair on
the trusted side, which Phase 1's handles do not reach; do not close it by
narrowing the trace to the verbs that already pass), and the
**binding-destination constraint** (any registered verb is bindable from data
and `CommandRegistry.Push` carries no principal, so `Mutate` over
`section:bindings` transitively grants the entire verb surface — destinations
need a constrained command class, per-page grants, or both, and `Push` acquiring
a principal is probably unavoidable; see Open Decision 3 and the ingress unit).

**1 — Authority spine.** Handle tables, `Observe`/`Present`, one authority check
at one boundary, the manifest as data, grants declarable and reviewable.
**The manifest and grants half is landed:** `WorldAddonRow.Requests`
(`WorldCapabilityRequest`, a capability/subject pair) is the manifest — what an
addon's row ASKS for, authored as data and validated at parse. `WorldDefinition.Grants`
(`WorldGrant` rows) is a world SHIPPING a hold reviewably, applied at boot through
the identical `WorldServer.Grant` path `world.grant` submits through — never a
second decision procedure. `Server/WorldAddonRuntime`'s mount prints one loud
line per manifest-carrying addon naming exactly what the settled table honors
and what it withholds, and requesting grants nothing on its own: deny by
default holds regardless of what a manifest declares — while since unit 4b the
manifest also BOUNDS what can materialize as guest-usable authority
(requested ∧ granted; an unrequested hold is reported and inert). Handle tables landed under Open
Decisions 1 and 5 (closed — see the ledger).

**2 — Channels.** Extract the abstraction from the addon ABI, then retrofit the
four surfaces onto it **as host-owned channels**. The ABI moves to v2 in this phase.
See the decisions below — two of them must be settled *before* the extraction starts,
because ABI v2 sets them for good.

**3 — Content as data.** `puck.sdf.v1`, overlay records as documents, cost
admission for granted op **and shape** sets — `FirstExoticTouch` pins the expensive
kernel variant on exotic *shapes* as well as ops, so admitting ops alone would leave
the externality wide open.

**4 — Live. Done.** `AddonHost.Reload` wired to a verb, metering extended to the
doors that still lack it, degradation under contention (see
`docs/capability-channels-STATE.md`'s BUILT entry — the answer-ring's true
residual is a durable lifetime counter, never a wire aggregate, because the
ABI's own ordinal contract has no reserved value to carry a many-to-one drop
count).

> **"Per-handle quotas" was FALSE and is corrected here** (Phase 4 census,
> 2026-08-02). A `WorldHandle` is four designation fields with ZERO payload,
> under the handle table's own designates-never-decides contract; the one
> quota that has actually landed — the query dispatch budget — keys on the
> GRANT ROW and is read after handle resolution, never off the handle. The
> deliverable is therefore *which doors get meters*, with the keying already
> settled by that precedent. Reload is also a TAPE-FORMAT question before it
> is a wiring question: receipts pin once in the header, so a live reload
> across a recording is invisible to the tape today.

### Phase 2 — decisions that must precede the extraction

Product of an adversarial design attack plus an independently drafted competing
design kept blind to this document; the round history — who diverged, who won,
why, and where the two converged without contact — is in git history. A blind
second design is evidence, not an oracle.

**Retrofit all four surfaces now; open only two of them.** Put commands, SDF
geometry, overlay records, and queries onto the channel plumbing immediately for the
ownership, quota, attribution, and deterministic-ordering wins — overlay overflow
alone stops being "later writers lost because earlier writers filled the buffer" and
becomes per-channel and attributable, and the toast's tail reservation becomes an
ordinary priority quota instead of a special call sequence. But do **not** open
`geometry`/`overlay` to untrusted producers until Phase 3 gives them document forms.
`ISdfSceneEmitter.Emit` is executable host code, not a bounded data vocabulary:
there is nothing to validate until a document defines schema, ordering, hash
identity, permitted ops, bounds, and material-reference rules.

**Simulation values become fixed-point, and this is the only cheap window.** The lane
spec says "fixed-point only" and the tree does not honour it: `CommandValue` carries a
`Vector4` of IEEE floats and the snapshot recording serializes them as singles — so
the replay currency itself is float. The GUEST half of this is done: unit 4b's
addon path enters a guest's raw `FixedQ4816` bits straight into the intent
(the old driver's lossy Q48.16→float conversion is deleted with it). What
remains is the seat half — introduce a fixed-point command value for Simulation
snapshots; physical floats and authored binding constants quantize **once**, on
entry. The recording format break that carries it is unit 5's window. Skip it
and the window closes with floats left in the replay currency.

**Pin the drain point: requests resolve after the authoritative step of the tick they
were written in.** "After the tick" is not a pinned point — and this document's own
progression section demands pinning for exactly this class. The tree makes the answer
computable rather than a matter of taste: addons already tick *inside* the fixed
step, before the server steps, so today's snapshot pose is end-of-previous-tick.
Draining after the step keeps that same one-tick staleness; draining before it makes
reads **two** ticks stale, a regression against the snapshot being replaced.
Consequence: the launcher builds and applies its snapshot *before* the simulation
step while addons tick *inside* it, so routing channel output through the snapshot
needs a launcher-level begin/end pump around the step.

**A request budget is compute, not space.** Every other quota bounds records in a
region. A request costs a host `IWorldQuery` dispatch, and the field-backed provider
raymarches — so a guest filling its request quota every tick at 240 Hz is a CPU denial
of service that no space budget can describe, on the one lane where work cannot be
dropped to recover. An `Observe` grant carries an explicit per-tick **dispatch**
budget, in the grant row, for the same reason vocabulary subsets live there.

**Degradation is a Presentation-lane posture only.** "Generalize `ViewStack`
everywhere" is wrong as written: its rule is *a view that misses its turn serves its
last handle*, which makes behavior depend on contention. That is correct and elegant
for presentation and a determinism bug by construction for simulation — what the
simulation consumed would depend on who else was busy. Simulation quota is reserved in
full at mount, and the mount **fails deterministically** if it cannot be; only
presentation degrades.

**A malformed Simulation batch commits nothing.** Never its valid prefix. Partial
commit produces the "record 0 acted, record 4 faulted" state that makes attribution
meaningless and replay unreconstructable.

**The handle defects that had to precede any guest crossing — both landed.**
`WorldHandle` now stamps `TablePrincipal`/`TableCapability` at mint (a mismatched
resolve fails before the index/generation check), and `PlayerRoster.DriveTarget`
carries the second belt that REFUSES on subject disagreement rather than
re-minting. The defect narratives, the adversarial corrections, and the proof
transcripts are in the ledger (Open Decision 1 and the no-op correction). One
forward constraint survives them: **mint by requested subject, never by
guest-chosen position** — whoever designs the `request` verb that lets a guest
ask for a handle over a subject it names owes this constraint at that point
(today nothing mints by guest choice at all).

**Close the principal-free ingress in this phase.** `CommandRegistry.Push` is public
and carries no principal, which is what makes the bindings section a
privilege-escalation gateway. Remove `Push` from the public surface: the registry
keeps definitions, ids, maps, text submission and snapshot application; a router-side
mixer becomes the only snapshot producer; and a sealed channel writer becomes the
only channel-mediated ingress, reachable solely from a Simulation producer context.
Every validated batch carries a **host-bound** principal and lane — never one read
from a guest record. That converts *did someone remember to check* into *there is no
other door*.

**The assembly split — ratified by a second two-design divergence round
(2026-08-01; the round's reasoning is in the ledger).** The shape: a neutral
channel/WASM core with no `Puck.Commands` or `Puck.Input` reference, a
Simulation adapter that has both, a presentation driver denied both. Rules are
stated as whole-project allowlists (the normative text the gate checks); the
fence is the project map's existing downward-only rule applied to a core placed
on a lower row. The binding rules:

- **The ABI vocabulary lives UP in the Simulation adapter, not down on the leaf
  row.** The core owns its OWN ABI phase discriminant with pinned wire values;
  the mapping to `CommandPhase` lives in the Simulation adapter as a visible,
  testable seam — never "simplified" back into the enum, whose ordinals other
  code may evolve. Corollary: **vocabulary is per-lane**, so `InputSources`
  stays in `Puck.Input` and `AddonSourceCatalog` lives in the adapter.
- **The sealed writer is internal to the Simulation adapter — no
  `InternalsVisibleTo` at all.** Presentation may not reference Simulation, so
  it cannot even NAME the type; IVT is assembly-wide and would hand a friend
  raw host, decoder, and lifecycle access alongside the writer.
- **The constructor rule:** a Presentation producer context accepts only
  CONCRETE, READ-ONLY, core-declared snapshot sources — never
  `IServiceProvider`, `object`, general factories, delegates, effectful
  callbacks, interfaces, or unconstrained generics — with member-type closure
  over every signature slot a value can occupy, parameters included, and core
  types sealed so inheritance cannot smuggle a slot the declaration does not
  name.
- **Live precedent to retire in the same change — CORRECTED (unit 6b's
  constructor-chain round, 2026-08-02): the whole view-composition locator
  chain, not `WorldScreenBinder` alone.** `WorldScreenBinder` resolved
  nothing (pure store-and-forward of the provider into late `SdfCameraView`
  constructions); the retained `IServiceProvider` also sat in
  `SdfCameraView`, `NestedWorldView`, and `SdfEngineNode` (the last also
  retaining four `Func<>` fields — three are the screens contract's per-slot
  data closures, documented at their declaration, and one is dormant; those
  stay, named, because the rule stays falsifiable only when what it does not
  cover is written down). The chain retired TOGETHER as one
  `SdfViewGpuServices` bundle (eagerly resolved once at the composition
  root, stashed and forwarded unchanged), because retiring one while
  deferring the rest proved impossible: a runtime `screen.source <n> view` and a
  camera-dimension reconcile both construct views AFTER `ConfigureViews`, so
  the binder must retain something. **One exemption stands, pinned at its
  own declaration:** `Puck.Platform.NativeWindowFactory`'s retained provider
  is a presence PROBE for the optional, licensed Switch VI backend — a null
  answer is the result, not a failed resolution — not service location for a
  producer's dependencies. This is the ONLY exemption; any future one owes
  the same explicit pin at its declaration.

**This split is Phase 2's long pole; cost it before scheduling, not inside it.**

**How two legitimate co-drivers COMBINE — open, and it is not a priority question.**
An addon driving a body while a human holds the controller is **assist** — aim assist,
walk assist, the whole accessibility category — not contention to be legislated away.
Co-driving is the feature. What is wrong today is that `WorldBody.SubmitIntent` is a
plain overwrite, so when two grant-holding principals submit for one body in a tick,
one silently replaces the other by loop order. That is wrong because it is
**arbitrary**, not because the wrong party won: an assist does not want to overwrite
the human's intent, it wants to MODIFY it — nudge the aim, straighten the walk. That
is composition, and the server has no vocabulary for it.

Settle it with the channel work rather than bolting it on. Three things it owes:
a **deterministic, pinned** combination rule (order matters, so it needs pinning the
same way the drain point did); a decision on whether co-driving deserves its **own
grant shape** rather than riding plain `Drive` (answered by the unit-7 sketch: no);
and an interim posture of **loud-and-arbitrary rather than silent-and-arbitrary**.

**What the interim fix changed is OBSERVABILITY, NOT BEHAVIOUR — read this before
concluding co-driving works.** `SubmitSeatIntents` no longer suppresses an occupied
seat (it yields only on genuinely empty input), and the collision is now reported,
attributed, once per episode:
`[body:0 driven by both seat1 and addon:default this tick — addon:default's intent applies]`.
But the measured position outcome is **bit-for-bit what it was before the fix**: the
addon still wins and the human's input still produces no visible motion. The erasure
is now *reported*; it is still an erasure, and it stays one until the composition
rule lands. **An interim that reports honestly is a different thing from an interim
that works, and only the first one shipped.** The addon wins because it submits
second, not because anyone decided it should — the composition rule must convert an
outcome that happens to be deterministic into an outcome that was *selected*.
(Minor, same family: the contention line currently rides the `[world.grant: …]` echo
prefix, which is a claim that this is a grant event when it is not. Give it its own
prefix when composition gives it a home.)

**The acceptance test already exists — use it, do not re-derive it.** Assign a device
to a seat, synthesize genuine seat input with `player.signal <source> press` (NOT
`player.fly`, which enqueues a console segment under the console principal and never
touches `SubmitSeatIntents`), grant an addon `Drive` over that same body, and choose a
motion axis the addon cannot produce so the two are separable. Remove or avoid scene
geometry on the path. That recipe is the only test in the tree that exercises a human
and an addon on one body, which is exactly what any composition rule has to be judged
against.

**And note what blocks assist TODAY, because the obvious fix is the wrong one.** The
addon snapshot's `buttons` field is written as zero every tick: an addon can PRODUCE
input and cannot OBSERVE it, so a genuine assist cannot see what the human is doing in
order to modify it. **The fix is not to fill in the field.** It is the same gap as
`request`/`response` — an addon cannot read anything — so `Observe` is what unblocks
assist. Widening the fixed 40-byte snapshot would spend the one ABI-breaking window
on the very structure Phase 2 deletes.

### Phase 2 — the implementation sequence

The costing the split's ratification demanded, as ordered units. Each lands
alone, verified by running the world; a unit that cannot land alone is two
units. Dependencies are stated so parallel sessions pick compatible work.

1. **The architecture gate — LANDED.** `build/Architecture.props` carries the
   policy, `build/Puck.Architecture.targets` plus `PuckArchitectureGate.cs`
   enforce it in every in-scope project's build (hooked
   `BeforeTargets="CoreCompile"`, checking the RESOLVED reference set),
   `puck architecture` reports it, and all 38 projects declare
   `<PuckKind>`/`<PuckLayer>`. Whole-solution build: zero `PUCKARCH`. The
   ratification round, the near-fatal hook-point catch, and everything the
   build taught are in git history.
2. **The vocabulary move, up-shape — LANDED.** `Puck.Scripting` shed its
   `Puck.Commands`/`Puck.Input` references; `AddonCommandPhase` pins the wire
   values in the core; `Puck.Scripting.Simulation` carries
   `AddonCommandPhaseMapping`, the moved catalog, and the
   `IAddonSourceResolver` bridge; the shipped `puck-addon-default.wasm` drives
   unchanged through the new seam. Wire format unchanged, so no guest artifact
   regenerated.
3. **~~Addon execution moves server-side~~ DISSOLVED into unit 4 by its own
   design round (2026-08-02; the round is in the ledger).** What the round
   SETTLED, binding on unit 4 (convergence without contact — treat as
   decided): move-everything rejected; the roster-claim and binding-resolution
   jobs DIE rather than move; the server-owned tick point is the TOP of
   `WorldServer.Step`, before the intent drain — the one point where the ABI
   pose phase (end-of-previous-tick) and the landing tick are both provably
   unchanged. Unit 4b landed this whole (`1860472e`): `WorldAddonDriver` is
   deleted, `Server/WorldAddonRuntime` pumps at the settled tick point, and the
   tier scout that mapped this move was retired — its dependency map described
   a client-side type that no longer exists, and the outcomes it fed are
   carried forward in the ledger and in the code.
4. **The channel abstraction in the core + the one deliberate ABI break —
   carrying the tier move. LANDED WHOLE: 4a as `f195a65e`, 4b in this
   change.** (The break is the `AbiVersion` handshake bumping to 2 — an
   integrity pin, not a version surface; nothing v1-shaped survives beside
   it.)
   - **4a — manifest and mount: LANDED.** `WorldAddonRow` gains a required
     `Lane` (`AddonLane` in `Puck.Scripting`, with an explicit
     `Unspecified = 0` the validator refuses by name, because a positional
     record used as a JSON source-gen target runs no field initializer for an
     omitted member and the lane decides which TIER untrusted code runs in);
     `Hash` becomes required in both lanes; requests are bounded by lane at
     DOCUMENT PARSE with a fail-closed mapping that lists every capability;
     `Slot` is deleted. Touched no ABI, so no guest artifact regenerated.
     **`Slot` had no authors** — not the shipped default row or any other
     shipped JSON row — so its preferred-slot branch never executed, its
     failure-reporting branch was unreachable, and `AddonHost.SlotOwner` had
     zero callers. That took the "exactly three ad-hoc ownership precedents"
     doctrine down to two, recorded in `src/Puck.World.Server/README.md` and
     `src/Puck.World.Data/Protocol/WorldPrincipal.cs` with the reason: DELETED for having no
     authors rather than unified, which is the outcome to prefer whenever a
     precedent turns out to be unexercised.
   - **4b — records and dispatch: LANDED, whole, as the one deliberate ABI
     break.** What shipped: the 32-byte kind-discriminated cells on one output
     and one input ring; the channel descriptor table decoded at handshake with
     the lane bind and the Input-requires-Request/Response structural rule; the
     two-phase mount (handshake without `puck_init`; `Admit` runs it after
     attenuation, quota, and disclosure); the Simulation pump (whole-batch
     refusal, payload domains at the decoder); `Server/WorldAddonRuntime`
     pumped by `WorldServer.Step` at three pinned points (guests tick at
     top-of-`Step`; contributions apply post-drain through the one extracted
     `ApplyIntentSubmission` gate, addons after seats by pinned rule; asks and
     reads resolve post-advance); `GrantedBody` disclosure + `Ask`/`Answer`
     with verdicts as data and multi-part `BodyPose`; authority materializes
     requested ∧ granted with the manifest gate enforced again at act
     application (a fabricated handle answers attenuated-to-empty); the grant
     door admits `Observe` over a concrete body; sticky lanes release when
     their guest stops driving; grants/revokes joined the recorded replay
     stream as position-pinned server input so replay RE-RUNS the guests
     (Open Decision 6's loopback half, decided here as banked). The artifact
     was regenerated and both pins moved together
     (`sha256-64/785cb89dbc95cfd0`). Verified live, red before green: the
     stale artifact mounts faulted while the world exits 0 (the trap, proven);
     grant drive+observe walks the ghost through
     disclosure → ask → pose → act; observe withheld produces zero motion;
     a mid-walk revoke freezes the body on its very next act with the
     attributed stale-handle refusal. Design deltas earned during the build
     are recorded in the ledger, and the landed ABI is
     `Puck.Scripting.AddonAbi` itself. Quota row fields were DESCOPED to
     lane scope (Simulation quota IS the guest's declared, ceiling-bounded
     ring capacities, reserved at mount; arena-byte row quota ships with the
     Presentation consumer that needs it).

   The original unit-4 design inputs banked from the unit-3 round follow, each
   now consumed by the landing above:
   - **`WorldAddonRow` presupposes one lane**: it has no lane discriminator,
     and its one routing field, `Slot`, is a Simulation-lane roster concept
     that is meaningless for a Presentation row. The manifest gains a lane
     field and resolves `Slot`'s scope in the same schema change — a
     data-shape decision, made here, not inherited.
   - **`IntentSubmission.Principal` is client-supplied and trusted verbatim**
     (`WorldClient` fills it from the roster; `WorldServer.Step` feeds it
     straight into `Allows`). Fine and unexploitable over loopback; over a
     socket, a client names its own principal — including an Addon's. This
     is the CONCRETE LINE behind "a hosted world's grants are enforceable
     only server-side," and it is another missing question, not a wrong
     answer: the check runs correctly on an identity nobody verified. The
     channel rule already ratified for guests ("a host-bound principal,
     never one read from a record") gets its transport-side sibling when a
     socket exists.
   - **The addon-facing vocabulary stops being binding-resolved,
     deliberately — but chords survive as DATA.** Bindings express human
     control preferences and must never widen an untrusted principal's
     authority; an addon loses the CLIENT'S binding table as its ambient
     authority source, not chord-shaped input itself: a chord an addon may
     fire becomes a typed record the manifest requests and the world grants.
     Chords stay data (the owner's standing posture); they stop being
     ambient reach.
   - **Replay: RE-RUN THE GUESTS, never record their derived output** — both
     drafts converged, and the third support is a principle three decisions
     now share from unrelated directions: recording a derived representation
     that can drift from its source is the `SnapshotRecording`-ordinals
     defect and the canonical-hash defect wearing a third coat. Costs
     honestly owned: format bump, embedded module bytes, WASM execution
     during verification — and the pinned-runtime "cost" is a policy this
     repo already holds (the Wasmtime pin never bumps). Decided in the same
     change as the tick move, never mixed with tap-relocation.
   - **The co-driving double-correction is CHOSEN, not discovered**: the
     tick move changes the contention mapping while unit 7 owns the
     composition rule — so unit 7's shape gets SKETCHED before this unit
     implements, and the contention re-record happens once here with that
     sketch in hand rather than twice.
   - **Pose crosses as a `request`/`response` under `Observe`, and a pose
     response carries the canonical orientation** (`FixedQuaternion`), never a
     "yaw" scalar that is lossy for a body that pitches or rolls — carry the
     primitive, derive the convenience. The guest stdlib gains generated
     `FixedVector3`/`FixedQuaternion` mirrors (arithmetic only) with a
     *named, obviously-derived* heading projection for callers who genuinely
     want the grounded scalar. (Derived while resolving the default-addon
     frame-mismatch defect.)

   **The branchless-capability panel verdict lands here (2026-08-01;
   owner-commissioned, two independent reviewers plus the orchestrator;
   4/4 NOT-NOW after the partner session's refinement).** The full
   bit-tensor/rank-select rewrite of the decision core is NOT-NOW on measured
   facts recorded in git history; what the panel RATIFIED binds this unit's
   design:
   - **Vocabulary subsets are u64 masks natively** (the 64-slot ABI source
     vocabulary), attenuation = AND, widening = OR, admission bottom = 0,
     quota checks = subtract + sign bit — built where its consumer is.
   - **The wildcard is its own bit, never smeared into the concrete mask as
     all-ones** — smearing destroys the concrete-vs-wildcard verdict
     distinction, revocation identity, and zero-slot projection at once.
   - **Rank/select may replace the handle-table PROJECTION only, never
     mint-time identity**: generations are irreducibly historical state. The
     per-slot (Subject, Generation) shadow and subject-compare rebuild
     survive verbatim under any representation.
   - **Per-domain typed mask wrappers** — raw u64s erase the kind discipline
     `GrantSubject`'s record equality provides for free.
   - **The constant-time claim is struck.** The sellable properties are
     exhaustive provability and determinism, never timing.
   - **The genuinely brandable core is small and real**: the five-rule
     verdict polynomial (≤16 input combinations) and `IsLegitimateSubject`
     as a finite LUT are `VerifiedCode basis: exhaustive` material whenever
     the row store lands — value proofs, never timing proofs.
   - **Ingress closure (unit 6) sequences AHEAD of any core rewrite, never
     beside it** — every authority defect this campaign measured was a
     MISSING QUESTION, a verb that never reached the decision; and when the
     core does eventually go branchless, the exhaustive brand worth having is
     not over the verdict polynomial — **it is over the set of paths that
     reach it.**
5. **Fixed-point replay currency + Open Decision 6**, in one unit because the
   plan already binds them: the quantize-once seat move and the channel-record
   replay semantics settle inside the same format window. **CORRECTED
   (2026-08-02, unit 5): the "recording v2" line item this entry originally
   named is VOID.** `SnapshotRecording` (and its whole chain — `InputRecorder`,
   `RecordingSnapshotSource`, `ReplaySnapshotSource`, `ScriptedSnapshotSource`,
   `DeterminismHarness`) had ZERO live callers and was deleted outright rather
   than reformed — there is no second recording format; the one recording
   format is the live world tape (`WorldReplayTape`/`WorldReplaySnapshot`,
   reshaped in this unit under the everything-stays-v1 ruling: the shape
   token pins at 1, the magic is the opaque shape identity, and a shape
   change re-keys artifacts, never a counter), whose own ten ordinal-cast
   sites get the same pinned-wire-value treatment `AddonCommandPhase` set
   as precedent.
6. **Ingress closure + the four-surface retrofit**: `CommandRegistry.Push`
   leaves the public surface, the router-side mixer becomes the only snapshot
   producer, the sealed writer (internal to the adapter) becomes the only
   channel ingress, the presentation producer context lands under the
   constructor rule, `WorldScreenBinder`'s retained `IServiceProvider` is
   retired, and commands/geometry/overlay/queries go onto channel plumbing —
   with only the Simulation three OPEN to untrusted producers (Phase 3 owns
   opening the other two).
7. **Co-driving composition** — the rule, its pinning, and the own-grant-shape
   decision, against the acceptance test already specified above. **Sketched
   below**, because unit 4 must not implement a record path that forecloses it.
8. **Guest artifact regeneration** — once, at the end of the ABI-breaking
   units, per the standing regeneration section.

Units 3–6 are strictly ordered. Unit 7 needs 4 (channels) and profits from 5.
Phase 4 consumes this sequence's output and stays its own phase.

#### Unit 7 sketched, because unit 4 needs it as an input

Deliberately a SKETCH: the shape, the decisions it forces, and what it needs
unit 4 not to foreclose. Not a design, and not a licence to build it early.
(The sketch round's findings and their derivations are in git history.)

**The body already arbitrates between producers — by PRECEDENCE, never by
merging.** `WorldBody` resolves one movement image per step from a pinned
ladder (tape segment, else submitted intent, else producer intent, else
default), every tier a one-tick image consumed and cleared on the same step.
The ladder has never once combined two images; it selects among them. So the
design question is whether co-driving joins the ladder or breaks it — and
joining it (assist as a new tier below submitted) is cheap and wrong: a tier
that loses to the human contributes nothing when the human is driving, which
is precisely when an assist is supposed to act.

**Recommendation: break it, deliberately, and confine the break.** The fold is
not a general facility. It is one named operation over an ordered list of the
tick's contributions, with the order pinned by grant shape and principal rather
than by arrival, and every contribution carrying which named operator applies to
it. `SubmitIntent`'s single slot with last-write-wins is what has to go; the
one-tick-consume discipline around it is what has to stay.

**On whether co-driving deserves its own grant shape: no, and the reason
relocates the question.** An assist must READ the human's intent to modify it,
and reading is `Observe` over the body. Co-driving is therefore already
expressible as `Drive` + `Observe` over the same subject — a conjunction, not a
new capability. Adding a fourth would put the same authority in two places, which
is the failure the verdict work exists to prevent.

**Consent — RULED (orchestrator, 2026-08-02): the axis is real, and its owner
is the PLAYER, never the grant table.** The grant model answers *may this addon
touch this body* (the world author's question); an assist raises a second
question — *does the person currently driving this body accept help* — whose
answerer is that player, in the moment. Collapsing the two is exactly how an
accessibility feature and a cheat become the same mechanism. Consent belongs
with the player's own data (durable stance in the per-profile preferences bag,
in-the-moment override as a live act in seat session state) and must never be
expressible as a world grant row — a row the author writes is the author
speaking for the seat. Two boundary facts fixed now so unit 7 cannot drift on
them: consent GATES composition, it never grants authority (an addon with
consent but no Drive grant still drives nothing — the two axes compose by
conjunction, matching the Drive+Observe finding); and consent is
Simulation-lane state (it changes what the simulation consumed, so it rides the
document/command path like every other input to the fold, never a
presentation-side toggle). The mechanism — verb, chord, diegetic act, default
stance — stays unit 7's.

**Ordering constraint unit 4 must respect: assist is blocked on OBSERVATION, not
on channels.** The snapshot's `buttons` field is written zero every tick, so an
addon can produce input and cannot read any — a genuine assist cannot see what it
is meant to modify. The fix is not to fill the field (that spends the one
ABI-breaking window on the structure unit 4 deletes); it is that `Observe` must
have a read path. So unit 7 depends on unit 4's RESPONSE/observation records
specifically, and a unit-4 design that ships input records without a
grant-checked read path leaves unit 7 unbuildable.

**What unit 4 must not foreclose**, stated as constraints rather than requests:
a body's per-tick contributions must be enumerable rather than collapsed at
submission; a contribution must carry its principal and the capability it was
admitted under, since the fold order is defined over those; and the read path
must be able to answer "what is the current holder's intent this tick" under
`Observe`, because that value is the assist's input.

**The interim posture stays interim.** The landed change made the collision
reported and attributed; it did not change the outcome by a single bit. A reader
who sees a contention line and a passing build will conclude the feature works,
and it does not.

**The acceptance test already exists and must not be re-derived** — it is
specified above, and it is the only test in the tree that exercises a human and
an addon on one body.

### Phase 3 — the decision sheet

#### VERIFIED PREMISES — build against these and nothing else


Builds B and C are unbuilt, so this has no code home yet. Six premises the
design rested on were verified; **five were REFUTED.** Each replacement below is
what survived, and re-assuming the refuted version is the failure this section
exists to prevent.

1. **There is no contributor cap.** No mount cap, no grant cap, no fixed word
   ceiling. The `128` an earlier derivation used was `MaxPopulation` — the BODY
   table, a different axis. The real fixed ceilings (`MaxInstances` 16384,
   `MaxScreenSurfaces` 32, the 65535 material sentinel, the boot-frozen word
   envelope) are ceilings on DIFFERENT objects, so "which binds first" has no
   answer; carry them as independent doors. Parameterize on the envelope, never
   on a population figure.
2. **Packed words are NOT additive across contributors.** Instructions,
   materials, segments, rigid leaves and instance-directory entries are; the
   **instance-grid block is GLOBAL**, derived from the COMBINED extent and median
   radius. Two contributors with one tiny static instance each cost a disabled
   one-cell grid measured alone, and roughly 32K prefix words when placed far
   apart — present in neither individual census. A ledger sized from summed
   actuals can be outgrown, and it fails at `UploadProgram`. The way out —
   granting only DYNAMIC instances (1 word each, additive; only
   static-active-maskable ones feed the grid) — has a price: dynamic instances
   sit outside the mask-first cull, so the lease's instance ceiling becomes a
   per-tile cost bound too. Pin both derivations and take the minimum.
3. **Screens have no host-assigned base.** Dynamic slots and materials already
   have host-assigned contiguous per-emitter bases, so those two axes need no
   engine change. A screen index names one of 32 GPU descriptor bindings plus a
   decal partition plus a light row — rebasing means REBINDING A DESCRIPTOR, not
   adding an offset. First-party screens use arbitrary absolute indices with no
   duplicate rejection, so count headroom can be legal while no contiguous base
   exists at all.
4. **Revoke frees the row and NOTHING admitted.** The codebase's only revocation
   response is lazy re-resolution at next use, which works because a query is
   EPHEMERAL. Admitted geometry is persistent — it lives in the compose set with
   no "next use" to fail. Either contributions re-submit per frame so a next use
   exists, or Phase 3 builds the repo's first eager eviction path. Re-granting
   the same principal narrower reproduces the failure with no revoke at all.
5. **Summed headroom is sufficient for COUNTS, false for RANGES.** Words,
   instructions, materials and instances take the overlay model verbatim. For a
   contiguous RANGE the only proven shapes are bump-allocate-and-never-free or
   fixed-stride partitioning with parked-slot cost. **Fragmentation does not
   exist as a concept anywhere in this codebase** — no free list, no coalescing,
   no compaction — so a variable-size runtime range allocator would be the first,
   with nothing to reuse.
6. **Grant order is unrepresentable.** Grants are `HashSet`s with no ordinal; the
   deterministic projection sorts by SUBJECT to be reproducible, not to rank.
   Grant A, grant B, re-grant A yields A→B or B→A depending on whether the update
   counts as new, and smooth-union and material wins differ between them. Any
   design needing pinned composition order must first give order a
   representation.

Both palette ceilings are load-bearing in different regimes and neither
substitutes for the other: per-document validation at decode (memory safety —
`sdfMaterialLoad` has no bounds check, so an out-of-range id is a GPU
out-of-bounds read: refuse, never clamp) and per-composed-program summation
(guards the 65535 sentinel collision). Which binds first depends on the declared
envelope size. Two rules that generalize past this phase: **inherit the builder's
refusals, never its repairs** (a first-party clamp REPAIRS; an untrusted door
must REFUSE, so it validates every positional reach itself), and **a lease
exhaustion and an envelope overflow never share a counter** (tenant spend,
answered as a verdict, versus host misconfiguration, assert-grade).


Derived from [the host-addressing survey](reviews/2026-08-01-sdf-host-addressing-survey.md)
(~155 parameters classified against the running shader consumption paths), then
put through its blind design round the same day: an independent designer given
ONLY the survey, the SDF wiki verdicts, and the cited code — forbidden this
plan — derived the same eight decisions in seven cases, beat this sheet on the
eighth (hash identity, below), and surfaced four questions the survey's
addressing lens structurally cannot see (decisions 9–12). Convergence without
contact on 1–6 and 8 is the strongest evidence this sheet has; treat those as
settled going into the schema. The round also sharpened several convergent
decisions in place — where a decision below carries a mechanism its first
writing lacked (the material base-translation, refuse-never-repair,
grant-subset-equals-enum-check, declared-count-drives-capacity), that is the
round's work.

1. **The vocabulary level is the builder/writer argument surface; packed lanes
   and table indices never round-trip.** Forced by two independent facts: the
   class-B lanes (host addressing) and the sixteen host-baked derived lanes,
   which make packed-lane round-tripping unsafe even for honest authors — a
   document supplying `Repeat` spacing alongside a mismatched baked reciprocal
   breaks march-safety invariants no finite-check catches. This also preserves
   every builder-side validation throw for free.
2. **Material references index the document's OWN palette section**, validated
   `0 ≤ id < paletteCount` at decode, sentinel range unreachable, palette size
   ceilinged below the sentinel base, and every contributed document decodes
   inside a mandatory `BeginMaterialScope`. The round added the mechanism that
   makes "unreachable" structural rather than validated: the scope's
   `materialBase` translation means a document ordinal `k` lands at
   `materialBase + k`, so the document *has no spelling for an absolute id* —
   and positional strides (`WallpaperFold`/`RepeatPolar`/`CellJitter`) are
   validated against the document's own palette span and **refused on
   overflow, never silently clamped**: a validating front door refuses, it
   does not repair, because a silent repair changes what the author sees
   without telling them. The existing positional clamp stays as backstop only.
   The survey found two live unclamped escalation paths behind unvalidated
   ids — this is the sharpest single decision on the sheet.
3. **Slot-bearing shapes declare COUNTS, never absolute indices** — screen
   surfaces and dynamic-transform slots are document-declared quantities the
   host maps to granted bases at decode (the `SdfEmitContext.SlotBase`
   contract promoted to the document boundary). The handle-table shape, one
   layer down; an author-chosen absolute `screenIndex` would show another
   tenant's live framebuffer on the author's geometry. The round added the
   capacity corollary: `MaxDynamicTransformSlot` is effectively unbounded and
   engine capacity derives from the highest slot named, so the **declared
   count, never the highest reference, drives capacity**, with every
   reference validated under the declaration — otherwise a document declaring
   2 slots and naming slot 40000 silently inflates the derived buffer.
4. **Glyph text enters at `Text` level only** (string + frame + em height; the
   host resolves atlas rects, distance scale, layout). Raw `Glyph` stays a
   first-party seam: its `distanceScale` is a march-safety coupling the host
   owns.
5. **Unknown enum values are decode REJECTIONS, never defaults.** The shader's
   benign default arms (union-like blend, P1-like fold) are crash-safety, not
   a schema; shipping whatever the default arm renders is unspecified content.
   The round unified this with granting: enums travel by NAME, and the
   decode-time membership test runs against the GRANT'S subset — the closed
   enum sets are exactly the granularity grants restrict, so subset
   enforcement and enum validation are one check, not two.
6. **Admission is op AND shape sets** (`FirstExoticTouch` pins the expensive
   kernel on exotic shapes too), plus the survey's three analysis obligations:
   the displacement amplitude-times-frequency ceiling, `AnalyzeLipschitz` run
   in the front door, and `Repeat`'s caller-owned in-cell rule recorded as an
   admission note since no document validator can check it. The round
   supplied the argument that makes the front-door Lipschitz run
   NON-REDUNDANT with the later whole-program pass: the packed step scale is
   **one value per program**, so one tenant's aggressive warp multiplies
   every tenant's march cost — a per-contribution bound is the only fair
   partition of a shared-fate resource, and no whole-program check can
   retrofit it.
7. **Hash identity is over the received UTF-8 BYTES, computed before decode —
   never a re-serialization, never the packed words.** This sheet first said
   canonical serialization with pinned float formatting; **the blind round's
   argument beat it and is adopted**: canonical float formatting is its own
   determinism liability (shortest-round-trip rendering has been
   runtime-version-sensitive), and canonicalize-then-hash creates a second
   serialization surface where hashed-bytes ≠ decoded-bytes bugs breed —
   this repo's own dominant failure mode, moved into the identity path. Same
   bytes + same code version → same program is the determinism contract in
   its native shape; dedupe, if ever wanted, is a second ADVISORY hash,
   never identity. Validation owns the STJ hazard list, which the round
   extended past this sheet's two: non-finite numbers must be checked AFTER
   narrowing to float (`1e39` parses as double and becomes Infinity on
   conversion — and the builder finite-checks almost nothing);
   **duplicate keys are rejected** (STJ silently takes the last occurrence,
   so a reviewer reads the first `rate` and the engine executes the second);
   unknown members `Disallow` everywhere except the root's `Extensions`
   regime; omitted collections arrive null (`?? []`); no `System.Numerics`
   types on the wire (fixed-length number arrays, exact length validated).
8. **Contingent on Phase 2, deliberately not decided here:** the
   geometry/overlay channel RECORD forms (the writer-call surface the overlay
   half of decision 1 maps onto), the verdict field a refused record carries,
   what a channel record is to the replay stream (Open Decision 6) — plus,
   from the round: the document's channel envelope (whole vs stream vs
   deltas), quota partitioning across concurrent grants, the grant-subset
   representation decision 5 consumes, update cadence vs the probe/capacity
   contract, the dynamic-transform per-frame value feed, screen-surface
   source pairing, and the host-mediated cross-tenant carve grant (the
   deliberate escape from decision 9).

The four decisions below came from the round alone — questions the survey's
addressing lens structurally cannot see, because they are about what an op's
SEMANTICS reach, not what its lanes address. Any future vocabulary addition
needs both lenses.

9. **The composition boundary is union-family only.** A root-level
   `Subtraction` in a contributed document carves OTHER TENANTS' geometry, and
   an intersection-family blend makes the enclosing instance unmaskable
   (`UnmaskableBoundRadius` admits it to every tile), defeating cost
   isolation. At the document's top level only `Union`/`SmoothUnion`/
   `ChamferUnion` are admissible, smooth/bevel radii under an admission
   ceiling (a large smooth radius is a halo reaching into neighbors);
   subtraction/intersection stay fully available INSIDE the document's own
   field scope. Enforced by validation, not host-wrapping — `MaxFieldScopeDepth`
   is 1, and a host wrap would spend the only level and strip contributors of
   intra-document CSG. Carving into someone else's geometry becomes a granted,
   host-mediated capability later; v1 ships closed.
10. **Composition order is semantics, so the host pins it: grant order, never
    arrival order.** Smooth-union halos and material wins depend on stream
    order; without the pin, identical documents produce different composed
    pixels across runs — a determinism hole no single document can close from
    inside.
11. **Every contribution seam opens with `ResetPoint`, and overlay clip
    fencing is INTERSECTION in the decoder, not wrapping in the packer.**
    Point-transform and fold state persist along a chain until reset, so a
    predecessor's open transform must never leak into the next contribution.
    And overlay clip scopes do not nest (`BeginClip` is last-call-wins), so a
    host cannot fence a contributor by wrapping its records — the decoder
    intersects every record's declared clip rect with the grant's viewport
    rect and emits the intersection. The screen-space analogue of the
    material scope, in the one place it can actually hold.
12. **A quota grant is a MEMORY COMMITMENT.** The capacity-probe doctrine
    freezes buffers from worst-case forms; an untrusted contributor's probe
    form is its granted quota ceiling — so over-granting is a resource bug
    even if no document ever spends its quota. Owed a sentence wherever the
    quota numbers get decided.

## Traps that survive

**The hash pin is enforced at boot.** `AddonHost.Load` checks `ModuleHash`, so a
rebuilt `.wasm` that is not re-pinned fails to mount. The pin lives in whichever
world's `addons` row mounts the module — no shipped world mounts
`puck-addon-default` today (the `default` world that once did was retired
under the 2026-08-06 four-world charter; `Puck.World/Assets/worlds` now ships
only `play`/`dive`/`kart`/`jump`, none of which authors an `addons` row), so
this trap is dormant until a world author mounts it again.

`dotnet run -c Release wasm/build.cs` rebuilds, copies to
`src/Puck.World/Assets/addons/puck-addon-default.wasm`, and prints the hash.

**The world is document-only.** The run loads the checked-in `--world` path or
the shipped default (`play.world.json`); a missing or invalid document refuses
the boot.

**`world.save` is lossy** — it discards uncommitted sculpt edits and does not
fold session-layer rebinds at all (those persist only via
`identity.bindings.save`, into the seat's owned identity world, a different
document). Hand-patch JSON rather than round-tripping it for either gap.

**Generated Rust is regenerated, never hand-edited.** `*_generated.rs` and
`fixed_vectors.rs` under `wasm/puck-stdlib/src/` come from
`dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib`. That is a CLI verb
and stays in scope.

**`editor.enter` masking an addon's motion is correct, not a defect.**
`WorldEditorSession.Enter` records `PriorSource` and sets `IntentSource.Idle`
deliberately — *the honest idle: live device input is masked while tapes and
`player.press` still drive* — and `Exit` restores it. An addon is live device
input; if it kept driving it would walk your avatar while you edit.
`player.control live <slot>` is the documented override. This was reported as a
defect during end-to-end testing and is not one.

**Do not "deduplicate" these:**

- **`SdfFieldEvaluator` against the GPU `mapCore`** — a deliberate dual under the
  sync-pair doctrine. It looks like duplication and is how the CPU query path
  stays honest.
- **`AddonAbiRustPort`'s generated Rust mirror** — written down twice, with
  generation enforcing agreement. That is the answer, not the problem.
- **`world.wait` having no `settle`/`step` sibling** — deliberately absent because
  a second verb would duplicate the stdin barrier. The correct instinct, already
  applied; use it as the standard when judging the rest.

The distinguishing test: *is there a mechanism enforcing agreement?* Generation and
derivation pass. Prose and convention do not.

## Machine setup

The .NET side needs nothing beyond the normal toolchain. The WASM side does:

```
rustup target add wasm32-unknown-unknown
```

`cargo build --release` from `wasm/` uses that target (pinned in
`wasm/.cargo/config.toml`). Tests must override it, because there is no runner for
`wasm32-unknown-unknown`:

```
cargo test --target <host-triple>        # rustc -vV prints host:
```

`wasm-tools` (`cargo install wasm-tools`) is optional — useful for inspecting a
module's imports and exports.

## Verification

Build, then run the world:

```
dotnet build -c Release --no-incremental
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 4
```

`Puck.World` must reach the addon mount line and exit 0.

**A green build proves nothing about behavior.** Every severe defect found while
this plan was being written was found by a measurement matrix or an stdin drive,
and none by a build passing. Vary a parameter across cells and read what the
running world actually does; a diff read and a clean compile are not evidence.

The campaign's earned verification doctrine — the rules about controls,
doc-parameter traps, derivation width, prose corrections, summary echoes, and
the shared-tree git protocol — lives in
[agent-guide.md](agent-guide.md#verification-doctrine); the war stories each
rule was earned from are in git history.

## Worked consumer: progression and reveals

Settled with the owner while designing this plan. Recorded here because these are
rulings *about the capability model*. The reveal graph itself **does not exist and
nothing plans it** — the port arc that would have built it was deleted along with
its plan. These rulings are therefore constraints waiting for a builder, not
review notes on scheduled work; whoever builds a reveal graph inherits them.

**A reveal is abstract.** It may warp a player, flip a debug view, unlock the
editor, open a gate. So its payload is **an authored list of things the engine
already knows how to do** — a command, a document mutation, a filled handle slot.
There is no effect language to invent, nothing to keep in sync, and every effect
inherits the validation its own vocabulary already carries. The command kind uses
`ICommandInjectionSink`, which folds a pre-resolved command into the deterministic
tick stream and whose own documentation already anticipates non-human drivers.

**Conditions are predicates over the same vocabulary `Observe` reads through.** One
vocabulary, two consumers, so a condition can never ask something an addon could
not, and the two cannot drift. An author needing a predicate the vocabulary cannot
express writes an addon that observes and emits, rather than growing the condition
language forever.

**Shape.** Reveals are monotonic — a growing set, so evaluation is edge-triggered
and latches on satisfy, and the graph is a fixpoint over something bounded that
only grows. Nodes are satisfied by any of several clauses; sequence is emergent
from dependency edges rather than an authored concept. Monotonic **counters** sit
beside monotonic flags, because a predicate over current state cannot express an
accumulation over history and every game needs one. Each raise records its **tick**,
which makes before/after/within-N expressible for one extra field. A clause may
become permanently false — the *set* is monotonic, an individual clause need not
be, and a closed path is unremarkable when a reveal has several.

**Scope defaults to per-participant.** A reveal may declare world scope for
cooperative goals. Catch-up between local participants needs no transfer machinery:
"raised for me if raised for any local participant" is just a clause, which makes
sharing authored data rather than a mechanism.

**Authority — the rule that keeps this safe, stated precisely.** A per-participant
reveal **acts as the participant whose progression raised it** for payloads that
participant could perform themselves: a warp, a command, a presentation flip. It
holds nothing that reaches anyone else.

**Two payload kinds cannot act as the participant, and the earlier wording's own
example list contained the exception.** A *filled handle slot* is an authority
grant, and a principal does not grant to itself — grants come from the granting
authority. A *document mutation* is the same shape. Both act under the **document's
own authority**, exactly as world-scoped effects already do, and the reveal document
is what authorises them. Leave this implicit and the reveal graph gets built with
reveals holding document-level authority "as the participant", which is precisely the
confused deputy the handle model exists to kill.

**"Document authority" is a placeholder and owes a real principal before anything
builds on it.** None exists — `WorldPrincipal` is `Seat`, `Console`, `Addon`,
`Peer`, and every `WorldMutation` carries one, so *the document did it* is not
currently expressible. Recommendation: a distinct **`Progression`** principal,
seeded with exactly the authority its reveal document declares and nothing more.
That keeps effects attributable (a grant traces to progression, never to whichever
seat happened to trip it), reviewable (the document states what progression may do),
and revocable through the same grammar as anything else. Settle it in Phase 1 with
the rest of the principal work rather than as an afterthought inside a reveal graph.

**The guarantee is about impersonation, not impact.** "No reveal ever acts upon
another participant" is false in the plain reading — a gate opening affects
everyone, and a world-scoped reveal warps every participant off one participant's
achievement. What the mechanism actually prevents is a reveal **exercising another
participant's authority**. State it that way.

Still open, and a reveal graph owes it a sentence before it ships: *which*
participant a latched reveal belongs to across seat reclaim — the seat, the
profile, or the human. Monotonic
progression plus transient seat occupancy makes that ambiguous today.

**The server evaluates all progression.** Client-side evaluation would make
progression a claim rather than a fact.

**Replay.** Effects are *derived*, never recorded — the condition re-evaluates from
authoritative state and the reveal re-fires. Recording them as input too would
double-fire. Nothing extra is stored and replay becomes self-verifying: a
divergence in progression is a divergence in the simulation.

**Ordering.** Several reveals can raise in one tick and their effects can interact.
Document order, at a pinned point in the tick. Choose it once and write it down, or
it is a determinism bug that only appears on another machine.

**Lanes.** A reveal *raises* on the Simulation lane, always. Its effects land
wherever they belong — a warp is Simulation, a debug view is Presentation. Staging
(camera, timing, audio) is Presentation and must be evaluable apart from the raise,
or it could influence when the reveal fires and replay dies.

## Documents that flip with a phase

These describe today accurately and become wrong the moment a phase lands. They
are listed so they are updated *in the change that invalidates them*, not swept
later — the drift this plan exists to remove is exactly what a deferred sweep
produces.

| Document | What flips | Phase |
|---|---|---|
| `docs/project-map.md` `Puck.Scripting` row | **Done (unit 4b).** The rows describe the split core + adapter and the pump; nothing claims virtual-pad conversion any more. | 2 |
| `docs/project-map.md` `Puck.Overlays` row | "A new surface is a new writer" gains per-CHANNEL leases (not per-handle — see the Phase 4 correction above) | 3–4 |
| `docs/vision.md` "refuse to grow a noun" | **Done (L5, 2026-08-02).** `Present` dissolved without ever gating a draw path; the count settled back at five verbs and handles. Revisit if the channel model changes the subject taxonomy | — |
| `src/Puck.World.Server/README.md` principals-and-grants | The reader-facing summary of the lookup model (re-homed there by the 2026-08-02 documentation overhaul) | 1 |
| `src/Puck.Scripting/README.md`, `wasm/README.md` ABI tables | **Done (unit 4b).** The Scripting README is the ABI narrative's single home; the wasm README is guest-authoring only and points at it. | 2 |
| `run-document` skill addons section | **Done.** Deleted along with the run document's `AddonDocument`; the skill itself has since gone with the whole engine-tier run document. | 0 |

Six rows left this table on 2026-08-02 without being written: they named
sections of the capability register and the capability catalog, both deleted
that day for asserting a verification status that `Puck.Post`'s quarantine made
false. Those obligations are void, not deferred — there is no
document to flip, and **no surviving document states the grant model, the addon
capability surface, or the principals-and-grants picture** outside
`src/Puck.World.Server/README.md` and this plan. If a phase needs a deeper
reader-facing statement of any of them, it has to be written from scratch.

## Regenerating the guest artifacts

Regenerate whenever a change needs it, in the change that needs it. There is no
deferred one-shot and no reason to leave the tree in a half-regenerated state:

1. `dotnet run --project src/Puck.Cli -c Release -- wasm-stdlib` for the generated
   Rust.
2. `dotnet run -c Release wasm/build.cs` to rebuild the artifact and print its hash.
3. Every world that mounts the addon, together with any baked fallback pin —
   no shipped world mounts `puck-addon-default` today (the `default` world
   that once did was retired under the 2026-08-06 four-world charter), so
   there is currently only one pin site to move, not two. Moving one site
   without another that exists is the trap, not moving them at all.

**A moving hash is the correct outcome, not a problem.** Determinism pins the
mapping — same document plus same input yields bit-identical state at a fixed code
version — never the values across versions. A deliberate correction is *expected*
to move hashes, and the answer is to re-record what it invalidates in the same
change. Never preserve a wrong result to keep a hash stable.

**Nothing behind us is owed anything.** No previous consumer, no stored replay, no
prior artifact constrains a change here. If something recorded under an older shape
stops matching, it is re-recorded or deleted — never accommodated, never given a
compatibility path.

Symptom worth recognising rather than debugging: if the ABI moves and the artifact
is *not* rebuilt, the committed `.wasm` fails its version handshake or region
checks and **mounts faulted**, while `Puck.World` still runs and exits 0. A faulted
addon logs one loud line by design. That means a green run is not by itself
evidence the addon loaded — check the mount line when the ABI has moved.

## Open decisions

1. **Handle table residence.** Closed. Record arenas stay in guest memory; the
   handle table is host-side with only the index crossing; slots carry
   generations; a `WorldHandle` is bound to the table that minted it.
2. **Whether `puck.sdf.v1` eventually replaces builder authoring for first-party
   emitters** or remains the untrusted-contribution door beside it.
   Recommendation: beside, indefinitely.
3. **Whether an addon may emit command names directly. CLOSED at the point of
   no return (unit 4b) as C's shape, sharpened:** sources to act, `request` to
   ask — and the sources resolve through a FIXED, host-owned interpretation
   onto the intent surface (the 4b interpreted set:
   move/turn/primary/secondary), never through the client's binding pages.
   That deletes A's ambient-reach defect outright rather than waiting on
   binding destinations to become a constrained class: an addon no longer
   reaches ANY registered verb, only the intent channels the server maps its
   granted acts onto. Command-name emission (B) stayed out. The historical
   options, kept for the reasoning:

   - **A. Sources only** (current). An addon declares input source ids; bindings
     resolve them to commands, and an addon stays exactly a gamepad, so the whole
     page/chord/modifier machinery keeps working for it at no cost.

     **Its security rationale is withdrawn.** This option was justified as *two
     independent attenuations* — the granted source subset plus the world author's
     binding pages. The second is not an attenuation. Nothing constrains a binding
     page's *destination* command, so a page can point a granted source at any
     registered verb, and `Push` carries no principal to refuse it. The layer
     amplifies rather than narrows — see the privilege-escalation finding in
     git history. A is still the right *starting* posture — it changes nothing
     and adds no surface — but it buys none of the safety it was credited
     with, and cannot until binding destinations are a constrained class.
   - **B. Sources plus a granted command subset.** The manifest may also name
     command names directly, approved individually. Gains reach for verbs with no
     sensible source binding. Price: it bypasses the binding layer, so world
     authors lose their veto over what a mounted addon can invoke.
   - **C. Sources to act, `request` to ask.** Invocation stays source-bound;
     anything an addon wants to *know* comes over `request`/`response` under
     `Observe` instead of through a read-back verb. Keeps A's security property
     while removing its main motivation, since most of what B is wanted for is
     reading rather than acting.

   C landed, with the fixed-interpretation sharpening above; growing the
   interpreted set (or the request vocabulary) is data behind a declared
   range, never a new ambient surface.
4. **Per-seat grant authority.** The AUTHORING half is closed —
   `section:grants` left the permissive `Seat` seed. The ISSUING half is a
   live risk and stays open:
   `HoldsForAdministration`'s actor rule exempts `Seat` from the
   holds-what-it-grants check UNCONDITIONALLY, the same as `Console` — and
   every `Editor*CommandModule` already submits under
   `m_session.PrincipalOf(slot)`, a `Seat` principal. The moment a per-seat
   grant verb is added, a local seat could `world.grant addon:x observe all`
   (or `present all`) without itself holding that subject, and the addon —
   the untrusted principal this whole model exists to bound — would draw
   blanket read (or draw) authority through the seat's exemption, never
   through a channel vocabulary grant a manifest requested and the console
   approved a subset of. Seeding `Observe`/`Present` to every seat's WHOLE
   domain at boot (the same permissive local-play default every other
   capability gets) is what makes the handout maximal rather than narrow —
   the seat is never required to narrow what it hands out to what it itself
   uses. Whoever adds a per-seat grant verb in Phase 2 owes this a decision:
   narrow the seeded `Observe`/`Present` grant, gate seat-administered grants
   by what the seat itself holds (undoing part of the Phase 0 exemption for
   this one capability pair), or accept it as a known consequence of `Seat`'s
   trust posture and say so in the same change. The seed change did NOT close
   this: it removed a seat's authority to AUTHOR grant rows, while this is a
   seat's exemption when ISSUING a grant it does not itself hold. The same two
   gates, and only one of them has been shut.
5. **Wildcard projection.** Closed. `ProjectSubjects` projects only
   per-instance kinds; the wildcard (and `Composition`, and every kind not
   yet invented) is real authority at `Allows` and never a handle-table slot.
6. **What a channel record IS to the replay stream — CLOSED (unit 4b + unit
   5), by construction.** The loopback-tape half closed in unit 4b:
   `replay.verify` re-drives a fresh server whose own attached
   `WorldAddonRuntime` re-runs the mounted guests deterministically, and the
   grants/revokes that authorize them joined the recorded server-input stream
   as position-pinned, interleaved entries. Unit 5 closes the remaining
   `CommandSnapshot`/recording-format half by dissolving the premise rather
   than picking one of the three original candidate postures: `CommandSnapshot`
   is ephemeral — built, applied, and dropped inside the tick that produced it
   — and the one thing that used to persist it (`SnapshotRecording` and its
   chain) had zero live callers and was deleted, not reformed (see unit 5's
   entry above). A channel record never enters `CommandSnapshot` (4b already
   routes guest contributions entirely outside the router/snapshot path), and
   with no recording pipeline left that could serialize a snapshot at all,
   there is no recording pipeline for a channel record to be absent FROM. The
   three candidate postures (record channel records as first-class replay
   input alongside the snapshot; re-execute the guest deterministically and
   re-derive them; fold them into the next tick's snapshot) are moot rather
   than decided among.
7. **Grant ergonomics — how an author NAMES a vocabulary subset.** Cited from
   the handles section since the admission/assist conclusions landed, and owed
   an entry ever since. The problem is real and it is a security problem
   wearing a usability costume: if "this addon may assist" means enumerating
   sixty-odd input source ids plus a query-verb list, no author writes it
   correctly, and the ones who try **over-grant to make it work** — which
   converts a fine-grained model into a coarse one in practice while looking
   fine-grained in the schema. Probably wants named vocabulary sets (an
   `assist` set, an `observe-own-participant` set) that a manifest requests
   and a grant approves by name. Deliberately NOT designed yet, for a stated
   reason: a naming scheme invented before Phase 2's actual vocabularies
   exist would fit them badly, and the sets must be derived from the real
   vocabularies rather than guessed ahead of them. Decide when unit 4 ships
   its record vocabularies — and note the constraint that outlives the
   decision: **whatever the sets are, they must be generated from the
   vocabulary they subset, never hand-maintained beside it**, or they become
   the campaign's own second-source defect at the one layer authors read.
