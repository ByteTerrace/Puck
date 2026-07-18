# Puck.World moldable-state plan — 2026-07-17

**Status:** EXECUTED. Adversarially reviewed (two independent passes:
architecture/doctrine, storage/identity/protocol), findings applied, then
refined per owner direction (cloud = prepared-not-proven; the quality bar
promoted to a standing rule) — then implemented end to end as six commits on
`claude/moldable-state-plan-a86832`: `74f26b4` Phase 1 (2026-07-17, the world
document + ouroboros gate), `cea24c6` Phase 2a (2026-07-17, the mutation
vocabulary + journal undo + version handshake), `579fbc4` Phase 2b
(2026-07-17, principals + capability grants + the addon-as-principal
keystone), `41f3c54` Phase 3 (2026-07-18, the player document + layered
bindings), `93d8159` Phase 4 (2026-07-18, cloud readiness — local-proven),
`df07af3` Phase 5 (2026-07-18, session write-back + the expo world). All
§5 exit criteria are met; the editor/UI-arc handoff note is
`docs/reviews/2026-07-18-world-moldable-state-handoff.md`.
**Branch context:** `features/it-starts` (client/server loopback separation, B2
intent-source axis, and locomotion-as-data kit rows are in the tree).
**Scope discipline:** edits live in `src/Puck.World` except where a phase
explicitly flags an engine seam (`src/Puck.Storage` in Phase 4; `Puck.Commands`
only if the Phase 3 brief decides binding entries need stable ids — see §2.4).
`Puck.Demo` is off-limits (read-only precedent).

## 0. What this plan is

Puck.World still hard-codes state that must become **moldable** — authorable as
data, scoped per player and per world, mutable at runtime through the
client/server protocol, and durable through per-user cloud storage
(blob.byteterrace.com, Azure.Identity). The next major arcs — the editor system
and the UI system, both of which must potentially support networking and
plugins — will build **on top of the surfaces this plan creates**. This plan is
therefore the substrate arc: it does not build the editor or the UI, it makes
every piece of state those arcs will touch reachable as data, addressable by
stable identity, and mutable through one protocol vocabulary.

Owner decisions recorded up front:

1. **Cloud is real — this arc prepares, it does not prove.** Per-user storage
   exists at `https://blob.byteterrace.com` behind Azure.Identity. Every shape
   this arc lands (document revisions, storage grain, version-token seam,
   identity contract) is designed so the cloud drops in later with **zero
   rework**; the actual wiring, identity implementation, sync policy, and
   proofs are a later arc's execution (§2.5 states the lands-now /
   deferred split).
2. **Plugins mean Puck.Scripting.** WASM UI addons at minimum; server-side
   plugins are not ruled out. This arc prepares the data/protocol surfaces the
   addon ABI expansion will consume; the ABI expansion itself is a later arc.
3. **Everything through the protocol.** Every mutation of moldable state is an
   `IServerLink` message, even for local play. Networking and multi-client
   editing must fall out of the shape, not be retrofitted.
4. **Data home for player-scoped state:** decided below (§2.3) — a separate
   per-user document family, not a `puck.run.v1` section.
5. **The quality bar is top-tier, always (§0.1).** Deduplication,
   simplification, correctness, and performance are never traded away for
   feature velocity. Zero-allocation, lock-free, deterministic, bitwise —
   these properties are wanted **whenever feasible**. Reputation is at stake.
6. **Many worlds, one substrate (§2.6).** The product is a variety of worlds
   — RTS, FPS, RPG, MMO, puzzles; mini-games that aren't all that mini. The
   client and server implementations must not be constrained to any one game
   concept: genre lives in world data, engines, and addons — never in
   protocol or client/server shape. This plan implements no genre; it keeps
   the substrate neutral.
7. **Capabilities are first-class (§2.7).** The single-owner model the host
   already practices (engagement latches, machine-input ownership, addon
   slot ownership) is promoted to one named primitive — principals holding
   grants — and the client/server contract supports **addons as principals**
   on equal footing with players.

## 0.1 The quality bar (standing rule, enforced at every review gate)

These are not aspirations; the Fable review gate at the end of each phase
checks them explicitly, and every phase brief states how its deliverables
satisfy them.

- **Deduplication first.** Before building anything, find the existing
  primitive — the adversarial review already killed one phantom rebuild (the
  binding stack that was in `Puck.Commands` all along). Every phase brief
  lists the primitives it *adopts* before the ones it creates; a new type
  duplicating an existing shape is a review finding, not a style preference.
- **Simplification.** Fewer types, smaller surfaces, supergreen deletes.
  Retiring `puck.world.profiles.v1`, the `inputBindingTable`, and any shape a
  phase obsoletes happens in the same change — no parallel old/new paths
  linger past their phase.
- **Correctness.** Thick validators, loud fallbacks, per-phase proofs that
  exercise the real path (not mocks). A change is not done until its proof
  script demonstrates it end-to-end.
- **Performance properties, whenever feasible:**
  - **Zero allocation on steady-state paths.** The per-tick pipeline (intent
    fold → sim step → snapshot emit → binding resolution) allocates nothing.
    Document/JSON work is confined to boundaries: load, save, and mutation
    application. Binding composition compiles **once per change** (then
    `PagedInputBindings.Reload`), never per frame. Mutation application
    rebuilds only the derived state of the changed section.
  - **Lock-free.** The sim tick owns its state single-threaded; anything
    asynchronous (storage I/O, future transport) communicates by queue
    handoff into the existing pre-tick drain windows — never a lock on the
    tick path.
  - **Deterministic, bitwise.** Anything touching sim state stays on
    `Puck.Maths` fixed-point — documents carry authored values that quantize
    once at compile/load, exactly as `FixedWorldKit` does today. Round-trip
    parity proofs are **bitwise** (the Phase 1 baked-default parity bar), not
    "close enough."
  - Performance *claims* follow the repo's measurement discipline: no
    asserted numbers without a measured baseline.

## 1. Current state (verified 2026-07-17)

The full inventory lives in the recon notes; the load-bearing facts:

- **Bindings** are one global table in `Program.cs:121-161`
  (`Dictionary<string, IReadOnlyList<CommandBinding>>`) feeding **two**
  consumers: `SharedInputBindings` (the `IInputBindings` seam handed to
  `AddFixedStepSimulation`, `Program.cs:277` — resolved per `(slot, source)`)
  and `BindingCommandSource` (the console dispatch path, `Program.cs:162` —
  slot-blind flat dictionary). Not per-player, not persisted, no rebind verb.
  `SeatController` never sees bindings — it consumes the *output* of command
  dispatch (held axes, lane edges, analog samples).
- **The binding-profile stack is already engine-layer.** `Puck.Commands`
  publicly owns `BindingProfileDocument` (`puck.bindings.v7`), the
  `BindingProfile.Compile` validator/compiler, `CompiledBindingProfile`, and
  `PagedInputBindings` — a per-slot `IInputBindings` resolver with chorded
  pages, modifiers, and hot-reload (`Reload(CompiledBindingProfile)`).
  Puck.Demo contributes only *content* (a default-document factory and a thin
  `ProfileDocumentStore<T>` wrapper). There is nothing to lift; World's gap is
  that it never adopted the stack. `Puck.World` already references
  `Puck.Commands` and `Puck.Storage`.
- **Kit rows** (`WorldDefinition.WorldKit`, the `ActionSpec` predicate/effect
  vocabulary) are genuinely data-shaped, but all five built-in kits share
  `MotionTuning.Default`; the `ActionSpec.Jump/Dash/Surge` factories hard-code
  their magic numbers; action lanes are capped at 2 (`ActionLanes`,
  `WorldBody.ActionLaneCount`); kit→entity assignment is the R1 hash `KitFor`
  (`WorldPopulation.cs:172-181`) with an explicit `RESERVED SEAM:
  policy-as-data` comment.
- **`WorldDefinition.Default` is authored in code with no loader.**
  Scene albedos/boulders, spawn points, five screens, two cameras, wander
  tuning, population defaults (4 local / 124 network / 128 max), render
  defaults and quality presets, window title — all literals.
  `WorldApplicationDefaults` (`WorldDefinition.cs:676-683`) says it out loud:
  no checked-in world data file or loader yet. Two **abstract polymorphic
  hierarchies** live inside it — `WorldScreenSource` (six kinds) and
  `WorldCamera` (Fixed/AvatarEye) — with no `[JsonPolymorphic]` /
  `[JsonDerivedType]` annotations and no `JsonSerializerContext` anywhere in
  Puck.World; round-tripping them is real serializer design work, with
  `Puck.Scene` (`ScreenSourceDocument`, `SceneObject`) as the precedent.
- **Session state resets every launch:** render/quality settings, population
  count, `DefaultPeerSource = Wander`, screen inserts.
- **Protocol:** `IServerLink` (SubmitIntent/SubmitCommand/SubmitSession/Query)
  over `LoopbackTransport`; `SubmitDefinition(WorldDefinition)` and
  `SubmitWorldMutation(WorldMutation)` are **declared but reserved**
  (`IServerLink.cs:26-39`); `WorldMutation` is an empty abstract-record marker.
  `WorldServer.Step` already drains a pre-tick queue (intents buffer via
  `EnqueueIntent`; commands/sessions apply in the pre-Step window) — mutations
  slot into the same discipline. No wire version field.
- **Persistence today:** only `WorldProfileDocument`
  (`puck.world.profiles.v1` → `%LOCALAPPDATA%/Puck/World/<b1d5c0de-0002…>/profiles/profiles.json`)
  through `ProfileDocumentStore<T>` / `IJsonObjectBlobStore`. Note its shape:
  it is a **catalog** of named profiles (amber/cobalt/moss/violet) plus
  `LastUsed`, and any of the 4 local seats can occupy any profile
  (`SessionRequest.SetProfile(slot, profileName)`). That catalog cardinality
  is correct and must survive this arc (§2.3).
- **Storage stack:** the local backend is production (world profiles, demo
  bindings). `AzureBlobObjectBlobStoreBackend` is complete, registered in DI,
  and **unreachable** — no caller constructs an `AzureBlobObjectStorageTarget`,
  and no user→container identity mapping exists. `DefaultAzureCredential`
  (parameterless chain) is the credential path for service-URI targets. Only
  `CreateOnly` is conditional; `Overwrite` is unconditional. Azure ETags are
  available on download but currently discarded. `Guid.ToString()` is a valid
  Azure container name (verified against container-naming rules).
- **Puck.Scripting:** working deterministic Wasmtime 44 host, `puck.addon.v1`
  byte ABI (40-byte snapshot in / 24-byte pad-command records out, fixed-point
  only, fuel-metered, Post-gated). No host imports — a guest cannot call back
  into the host at all. UI addons and server-side plugins require an
  ABI-version event on a designed capability surface; **that is a later arc**.

## 2. The design

### 2.1 The primitive: three scopes of moldable state

Every piece of moldable state gets classified onto one axis — **scope** — and
inherits its authority, transport, and persistence story from that scope.
Nothing gets a bespoke pipeline.

| Scope | Document | Authority | Persistence | Examples |
|---|---|---|---|---|
| **World** | `puck.world.def.v1` | Server | Data file (checked-in or user path); `world.save` writes back | Kits, tuning, actions, scene, screens, cameras, spawns, wander, population defaults, render defaults, binding overlays |
| **Player** | `puck.world.player.v1` — a **catalog of profiles** per user | Server (per session), user-owned durably | Routed store: local always, cloud per-user when identity present | Per profile: display identity/color, motion prefs, invert-look, binding profile, preferences |
| **Session** | none (live state) | Server | Not durable by itself; survives via write-back into the world doc (`world.save`) or player doc (explicit save) | Which profile each seat occupies, current population, peer source, screen inserts, live render settings, unsaved rebinds |

**Cardinality is explicit:** one cloud user (container) → one player document →
a *catalog* of N profiles → each of the 4 local seats selects one profile
(selection is Session scope, exactly as `SetProfile(slot, name)` works today).
Couch co-op under a single signed-in user is the normal case, not an edge case.

Rules that keep this one system instead of three:

- **Stable string identity everywhere.** Kits, screens, cameras, spawn sets,
  profiles, binding pages — every row a document carries is addressed by a
  string id. Mutations target ids; plugins and the editor reference ids; ids
  never carry meaning beyond identity.
- **Versioned documents with `[JsonExtensionData] Extensions`** — the same
  extensibility posture as `PuckRunDocument`. Unknown sections survive
  round-trips; that is the data-side plugin story until the ABI arc.
- **One thick validator per document** (the run-document doctrine):
  `WorldDefinitionValidator` grows to gate the loaded world doc; a
  `WorldPlayerDocumentValidator` gates player docs (binding sections
  additionally pass `BindingProfile.Compile`, which is already the binding
  validator). Loaders never half-accept: a malformed doc falls back to the
  baked default, loudly.
- **The baked defaults remain, as seeds.** `WorldDefinition.Default` stops
  being *the* definition and becomes the value serialized to create the
  default data file (and the fallback when no file exists). Same for the
  default player document.

### 2.2 Everything through the protocol

The server owns all three scopes at runtime. Clients never mutate moldable
state directly — they submit, the server applies at a tick boundary, the
result flows back in the normal snapshot/notification path. Every submission
carries its acting **principal** and is checked against the grant table
(§2.7) before it applies.

- **`SubmitWorldMutation(WorldMutation)` becomes real.** `WorldMutation` grows
  from an empty marker into a kind-tagged record vocabulary that is *the*
  editor substrate: `UpsertKit`, `RemoveKit`, `SetKitAssignment`,
  `UpsertScreen`, `RemoveScreen`, `UpsertCamera`, `SetScene`, `SetSpawns`,
  `SetPopulationDefaults`, `SetRenderDefaults`, `UpsertBindingOverlay` — one
  record per world-doc section, addressed by stable id, coarse-grained (whole
  -row upsert, not field pokes). Mutations buffer like intents and apply
  between ticks; derived state (compiled kits, screen bindings, spawned
  population, composed bindings) rebuilds from the changed section.
- **`SubmitDefinition(WorldDefinition)` becomes real** as the whole-document
  swap (load a different world file at runtime; also the editor's
  "revert/load" verb).
- **Player-document messages** join `SessionRequest`: `SetPlayerSection`
  (targets one profile's section: bindings | identity | motion | prefs) and
  `GetPlayerDocument`. The existing `SetProfile` (seat→profile selection)
  stays what it is — Session scope. Note the protocol's section granularity is
  a *message* grain; the storage grain is per-profile (§2.5) — the two are
  deliberately not the same, and §2.5 states what that means for concurrency.
- **Client-scoped state still routes through.** Bindings are *applied* on the
  client (the per-seat `IInputBindings` resolver), but the durable record is
  server-owned: the client submits `SetPlayerSection(bindings)`, the server
  persists and acknowledges, the client applies the acknowledged version.
  Local play pays one loopback hop; networked play and cloud save fall out
  for free.
- **Versioning that actually rejects:** `SessionRequest.Join` gains a protocol
  version integer, and `ApplySession` **validates it** — an incompatible
  client gets `Accepted: false` with a distinct reason in the reply. Without
  the rejection path the field is theater. That is the entire wire-versioning
  story this arc — the socket transport (serialization, deltas, auth) remains
  a later arc.

### 2.3 Player data home (the decision delegated to this plan)

**Player-scoped state lives in its own document family,
`puck.world.player.v1` — not in `puck.run.v1`.** Reasons:

1. Scope mismatch: a run document describes *a world/run*; profiles travel
   with *a person* across worlds and machines. Cloud save is per-user; making
   it a run-doc section would couple a user's data lifetime to a world file's.
2. The storage stack already thinks in per-user containers
   (`ObjectBlobAddress.ObjectId` → container). A per-user document family
   drops straight onto it.
3. Precedent: `WorldProfileDocument` already models the catalog correctly;
   this plan extends each catalog entry with a binding profile and
   preferences rather than inventing a new shape.

`puck.world.player.v1` **absorbs and retires** `puck.world.profiles.v1`
(supergreen: migrate the on-disk file once at load — read old shape, write new,
delete old path — and delete the old types; no read-side tolerance kept). The
document is a **catalog**: `Profiles[]`, each with a stable id and sections
`identity` (display name, color), `motion` (speed, turn, invert), `bindings`
(a `BindingProfileDocument`), `preferences` (open bag) — plus a document-level
`Revision` (see §2.5) and `Extensions`. Machine-local conveniences that should
not roam (e.g. `LastUsed` boot seating) stay in a small local-only sidecar,
not in the cloud document. The world doc may carry *defaults* for new players;
it never owns player data.

### 2.4 Bindings: layered resolution (per player AND per world)

The owner requirement is bindings updatable **per player and per world**. That
is a layering problem, not two features — and the machinery already exists in
`Puck.Commands`; World's work is adoption plus a merge rule.

**Where resolution actually happens.** The composed result must feed *both*
existing consumers: the per-seat `IInputBindings` seam (today
`SharedInputBindings`, to be replaced by one `PagedInputBindings` per seat)
and the console dispatch path (today the slot-blind `BindingCommandSource`).
The Phase 3 brief decides whether the console source becomes per-slot or is
rebuilt from the composed documents; what is non-negotiable is that both
consumers derive from the same composed profile — no second table.
`SeatController` is untouched by all of this; it sits downstream of dispatch.

**The layering model: document pre-merge, then one compile per seat.**

```
effective document = engine default BindingProfileDocument   (World-authored, replaces inputBindingTable)
                   ⊕ world overlay(s)                        (from puck.world.def.v1 — contextual pages, e.g. a kart world's vehicle layer)
                   ⊕ player profile bindings                 (the seat's selected profile, from puck.world.player.v1)
                   ⊕ live rebinds                            (session layer; folded into the player profile on profile.save)
```

⊕ is a **document merge with an explicit key**: an entry is identified by
`(page id + ordered chord, source)`. A later layer's entry at the same key
replaces the earlier one; entries at new keys append; whole pages unknown to
earlier layers append. The merged document then goes through the existing
`BindingProfile.Compile` once per seat, and the compiled result is applied via
`PagedInputBindings.Reload` (hot-reload is already built). This is deliberate:
the compiled-level `LayeredInputBindings` primitive composes wholesale per
`(slot, source)` and cannot express "override one entry inside a shared page,"
which the per-world overlay requires (the kart world remapping a single lane).
Pre-merge at the document level can; that is the rule.

Binding entries today carry no per-entry id — the `(page, chord, source)` key
needs no shape change. If the Phase 3 brief concludes stable per-entry ids are
needed anyway (e.g. for editor UX), adding an id to
`BindingPageEntryDefinition` is a real `Puck.Commands` edit and is the one
conditional engine seam of Phase 3 — flag it in the brief, don't improvise it.

Rebinding is exposed as console verbs this arc (`player.bind`, `player.bindings`,
`profile.save`); the chord-first authoring UI belongs to the UI arc and will
sit on the same `SetPlayerSection(bindings)` message. One acknowledged
consequence: live rebinding changes the input→command mapping mid-run, which
deliberately breaks replay-stable command streams — Puck.World is not
determinism-gated by owner ruling, so this is accepted, not accidental.

### 2.5 Cloud readiness (blob.byteterrace.com)

This section is the **blueprint** the eventual cloud arc executes; this arc
lands only the parts that make the blueprint drop-in (the split is at the end
of the section). Two mechanisms the design keeps separate on purpose: the
**ordering key** (which copy is newer) and the **clobber guard** (did someone
else write since I read). ETags are opaque — they can guard, they cannot
order.

1. **Ordering lives in the document.** `puck.world.player.v1` carries a
   monotonic `Revision` (long, incremented on every save) plus `UpdatedAtUtc`
   as tiebreak (persistence sits outside the sim-determinism contract; wall
   clock is legal here). Sync state is derived, never a volatile flag:
   `local.Revision > lastSyncedRevision` (both persisted locally) means dirty
   — crash-safe by construction.
2. **Clobber guard** (engine seam, `src/Puck.Storage`):
   `ObjectBlobReadResult<T>` gains an opaque version token; `WriteAsync` gains
   an optional if-match. Azure: the download ETag (currently discarded) and
   `BlobRequestConditions.IfMatch` catching 412. Local: a content hash used as
   an LWW input only — its if-match is best-effort within one process (the
   file backend has an inherent read/write TOCTOU gap); true optimistic
   concurrency is an Azure-backend property. This change ripples through the
   public surface — `JsonObjectBlobStore`, `ProfileDocumentStore<T>`,
   `WorldProfileStore` — trivial under supergreen, but the Phase 4 file
   -ownership map must list them. The Azure backend also becomes
   `IDisposable` (it holds a disposable credential).
3. **Storage grain: one blob per profile.** Under the user's container:
   `world/profiles/<profile-id>.json` per catalog entry plus a small
   `world/player.json` for catalog-level state. Concurrent edits to
   *different* profiles from two devices are then independent. Concurrent
   edits to the *same* profile remain whole-profile LWW — detected (ETag +
   Revision), surfaced in `storage.status`, and accepted as a known window
   this arc; the protocol's section-granular messages do NOT buy cross-device
   section merging, and the plan does not pretend they do.
4. **Identity → container.** An `IPlayerStorageIdentityResolver` maps a
   signed-in principal to the per-user container Guid. **Not** by parsing the
   storage-scoped access token (`DefaultAzureCredential` yields an opaque
   JWT; decoding it is unsupported-brittle, and under CI/managed-identity the
   `oid` would be the *app's*, silently collapsing per-user into per-app).
   The resolver's sources, in order: an explicit override (data-file field /
   `--user-id`, for dev boxes and agents), then a genuine user identity via an
   ID-token/claims flow (the phase brief picks the Azure.Identity mechanism —
   broker/interactive — and its UX is allowed to be minimal this arc). Under a
   non-user credential the resolver **declines**: per-user sync stays off,
   local-only, `storage.status` says why. The Entra `oid` claim is a Guid and
   `Guid.ToString()` is a valid container name, so oid-as-container remains
   the target mapping — obtained from real claims, never from a parsed
   storage token.
5. **Composition + policy.** The storage endpoint is a **data-file field**
   (host section of the world doc) with a `--storage-uri` CLI reflection —
   never an env var (World has no `PUCK_*` surface). The player store becomes
   local-first: boot reads local, kicks off a cloud read; divergence resolves
   by Revision (tiebreak `UpdatedAtUtc`, conflicts logged loudly); writes go
   local-synchronous + cloud-async, retried while dirty. Verbs are the
   control plane: `storage.status` (identity, endpoint, per-profile
   dirty/synced/conflict, last error), `storage.sync` (force a round-trip).
6. **Testability without prod (for the cloud arc):** Azurite
   (connection-string path) exercises the blob mechanics — container create,
   read/write, ETag round-trip, conditional overwrite, per-profile keys. It
   **cannot** exercise the identity resolver (no Entra principal on that
   path): the resolver is proven by its own unit-level tests plus the one
   owner-assisted smoke against blob.byteterrace.com with a real credential.
   Those two proofs stay separate on purpose.

**Lands in THIS arc (readiness — the parts that would cost rework later):**
the document `Revision`/`UpdatedAtUtc` ordering fields and persisted
`lastSyncedRevision` shape (1); the storage version-token/conditional-write
seam, since it is also the honest local story (2); the per-profile blob
layout under the local target — same address model the cloud uses (3); the
`IPlayerStorageIdentityResolver` **contract** with only the explicit-override
and "decline" implementations (4); the endpoint as a reserved data-file field
(5). All of it runs and is proven against the **local backend only**.

**Deferred to the cloud arc (execution — nothing above needs reshaping):**
constructing the `AzureBlobObjectStorageTarget`, the real claims-based
identity implementation, the cloud-async sync policy and retry loop, the
`storage.sync` round-trip against a live endpoint, Azurite proofs, and the
owner-assisted smoke. `storage.status` ships now and reports the truth:
identity absent, cloud unwired, local authoritative.

### 2.6 Many worlds, one substrate (genre-neutrality)

The destination is a game of many games — an RTS world, an FPS world, an RPG
world, an MMO-shaped world, puzzle worlds — reachable from the same client
against the same server. This plan implements none of them; its obligation is
that **nothing it ships would need reshaping for any of them**.

What already points the right way, kept and strengthened:

- **Every entity is intent-driven, and drivers are interchangeable.**
  `WorldPopulation`'s own doctrine: a driver is "a client seat, a network
  peer, AI, a replay" — the B2 intent-source axis. Genre-specific control
  (an RTS's order queue, an FPS's aim) is a different *producer* of the same
  compositional intent/command traffic, or a different action vocabulary in
  the world doc — not a different protocol.
- **The action vocabulary is compositional data** (predicates/effects/kit
  rows), not named avatar moves. Genre verbs are authored rows. The lane
  count stays 2 this arc, but lane widening is explicitly on the genre path
  (Phase 1 notes it) — a data-bounded capacity, not an enum ritual.
- **Engagement is the existing mini-game seam.** A machine screen already IS
  "a different game the same pad drives" — the engage latch reroutes intent
  wholesale. That mechanism generalizes: a genre world is content plus
  possibly an engine/addon behind a surface, never a client fork.
- **The mutation vocabulary is section-keyed, not concept-keyed.** New
  world-doc sections (an RTS's tech tree, a puzzle set) arrive as new
  sections + new mutation kinds + `Extensions` bags; existing messages never
  change meaning.

**The standing audit question — asked of every new contract surface at every
Fable review gate:** *"Would an RTS / FPS / RPG / MMO / puzzle world need a
different message here, or just different data?"* If the answer is "a
different message," the surface is wrong — generalize it or move the
specificity into data. (This is the calcification audit applied to the wire.)

### 2.7 Principals and capability grants (first-class)

**The verified precedent.** The single-owner model already exists in three
ad-hoc forms: `WorldEngagement` (a player's display index exclusively latches
a screen route; the body owns the engagement latch), the demo's machine-input
ownership (while a machine is owned, input flows from that one player only),
and `AddonHost.SlotOwner` (an addon owns a roster slot). The pattern is
right; it is just not one primitive, and addons have no standing in the World
client/server contract at all. This plan names it once:

- **Principal** — anything that acts through the protocol: a seat, the
  console/script surface, an **addon**, and (later) a network peer. Every
  `IServerLink` submission carries its acting principal.
- **Grant** — `(principal, capability, subject id)`: the right to **drive** a
  body, **control** a screen/machine surface, **mutate** a world-doc section,
  or **edit** a player-profile section. Coarse verbs, stable-id subjects, one
  server-side grant table. Grants are Session-scope state, mutable through
  the protocol (`world.grant` / `world.revoke` verbs), with defaults seeded
  from documents. Exclusivity (the engagement latch) is a property of a
  grant, not a bespoke mechanism per surface — `WorldEngagement` becomes a
  view over drive/control grants rather than a parallel table.
- **Local play defaults permissive.** On a single machine every seat holds
  every grant it holds today — behavior is unchanged until someone revokes.
  The editor's permission story, and any future trust UI, changes *grant
  contents*, never protocol shape.
- **Addons are principals, placement-free.** An addon attaches through the
  SAME `IServerLink` as any client — it is granted a body and drives it by
  submitting intents (the Wasmtime pad-command ABI already produces exactly
  that traffic; the demo's `AddonCommand → PlayerIntent` translation is the
  shape). Because the attachment is protocol traffic, *where* the addon runs
  — beside the client or beside the server — is a hosting choice, not a
  contract change. That is the owner's "server-side plugins not ruled out"
  kept open for free, and it is why capability checks live in the server,
  not the client.

### 2.8 What this plan deliberately does NOT do

- **No genre implementations.** No RTS/FPS/RPG/MMO/puzzle mechanics land in
  this arc — §2.6 is a neutrality obligation on the substrate, enforced by
  the audit question, not a feature list.
- **No trust model / permissions UI.** Grants exist and are enforced by the
  server (§2.7); the default policy is permissive local play. Deciding who
  *should* hold which grants — reveal ladders, hostile-addon posture, the
  editor's permission UX — is later work on top of the grant table.
- **No editor, no UI system** — it creates the mutation vocabulary and
  document surfaces they will drive.
- **No addon ABI expansion.** UI addons need host imports/capabilities — an
  `AddonAbi` version event with its own design review (capability granting,
  trust model beyond "trusted path-declared authors", possibly server-side
  execution). This plan's contribution: every surface an addon will touch is
  already data with stable ids and a protocol vocabulary, and documents carry
  `Extensions` so data-only addons work day one. Named first deliverable for
  that future arc, so it starts concrete: a **"gardener"** addon granted
  `mutate` on the scene section — a fuel-metered guest that slowly rearranges
  the boulders through the same `WorldMutation` traffic as the editor, proving
  the grant table gates a plugin's world-editing end-to-end. (It is out of
  reach today only because the pad-command ABI cannot express mutations —
  which is precisely what the ABI event adds.)
- **No socket transport.** The rejecting version handshake and the
  buffered-mutation discipline are the only forward payments.
- **No live cloud.** Owner ruling: readiness, not proof. The shapes and seams
  land and are proven locally; wiring, identity, sync, Azurite, and the real
  -credential smoke belong to the cloud arc (§2.5's deferred list).
- **No cross-device merge engine.** Whole-profile LWW with detection is the
  contract (§2.5.3); anything finer is future work with a real requirement
  behind it.
- **No Post stages for World features.** World's verification story remains
  build + proof scripts + running it. The one defensible engine-tier gate — a
  `Puck.Storage` conditional-write/round-trip stage (local backend + Azurite
  when available) — is **flagged for owner approval**, not assumed.

## 3. Phases

Each phase lands as one commit (or a small stack) on `features/it-starts`,
verified before the next begins. Dependencies: 1 → 2 → 3 → 5 are ordered;
4 (cloud) depends on 3's document shape (not on 2) and can overlap 5.

### Phase 1 — The world document (`puck.world.def.v1`)

- Serialize `WorldDefinition.Default` into a checked-in
  `src/Puck.World/Assets/worlds/default.world.json`; write the loader
  (`--world <path>` arg; missing/invalid → baked default, loud console line).
- **Serializer design (Opus-tier, not mechanical):** annotate
  `WorldScreenSource` and `WorldCamera` with
  `[JsonPolymorphic]`/`[JsonDerivedType]` discriminators mirroring the
  `Puck.Scene` precedent (`ScreenSourceDocument`, `SceneObject`); add a World
  `JsonSerializerContext`; decide the `Vector3` strategy (`IncludeFields` vs
  serializable surrogates — the repo has been burned by STJ silently zeroing
  `Vector3`/`Quaternion`; `WorldProfile` stores color as hex for this exact
  reason).
- Give every row a stable id where one is missing (screens, cameras, kits
  already have names; spawns and scene boulders need them).
- Complete the data-drive the kit-rows commit started: per-kit
  `MotionTuning` actually varied per row; `ActionSpec.Jump/Dash/Surge` factory
  literals become authored fields in the document rows; `KitFor` hash replaced
  by a definition-supplied assignment policy table (the reserved seam), with
  the hash as the explicit `"hash"` policy default; lane count stays 2 this
  arc but `TryParseLane`/`ActionLanes` consolidation notes the widening path
  (a genre-path item per §2.6: RTS/MMO worlds will want more action
  channels; the capacity becomes data-bounded, not an enum ritual).
- World doc gains an `addons` section (name / module path / hash / fuel /
  enabled — World-local descriptor rows mirroring the `AddonDocument`
  precedent), consumed in Phase 2 when addons mount as principals.
- `WorldDefinitionValidator` grows into the one thick gate for the loaded
  doc, and it is explicit about the field taxonomy: **sim-affecting** values
  (kit tuning, actions, spawns, population) quantize to fixed-point once at
  compile — exactly the `FixedWorldKit` pattern — while **presentation-only**
  values (albedos, camera FOV, screen geometry) stay float. The validator
  names which is which so a future field lands on the right side by
  checklist, not by vibe.
- **Canonical serialization — the ouroboros gate.** The writer emits a
  canonical form: stable member order, invariant number formatting, no
  incidental whitespace drift. `world.save` immediately after load reproduces
  the file **byte-for-byte**. This is a §0.1 bitwise property doing product
  work: world files become diffable and git-friendly (the editor arc's
  version-control story starts here), and save-path idempotence is proven
  rather than assumed. Load→save→load byte-identity joins the Phase 1 exit
  bar alongside the baked-default parity proof.
- **Traps:** STJ `IncludeFields` for numerics; source-gen omits collections →
  null → `?? []`; the serializer initializer-skip trap from the run-document
  doctrine.

### Phase 2 — The mutation vocabulary

Lands as two commits: **(2a)** mutations + handshake, **(2b)** principals/
grants + the addon proof — 2b builds on 2a's vocabulary but neither waits on
the other's review to start its brief.

- `WorldMutation` → the kind-tagged record set of §2.2; `SubmitWorldMutation`
  and `SubmitDefinition` implemented end-to-end (buffer → tick-boundary apply
  → derived-state rebuild → snapshot/notification out).
- **The mutation journal — undo for free.** The server appends every applied
  mutation, tick-stamped, to a session journal. `world.undo [n]` restores the
  loaded base definition and deterministically replays the journal minus its
  tail — no per-mutation inverse logic is ever written, because the journal
  IS the edit history and replay IS the undo engine. `world.save` compacts
  the journal into the file (and the journal answers `world.status`'s
  dirty question precisely: dirty = journal non-empty). The editor arc
  inherits undo/redo as replay on day one — which is also the honest framing
  of what an editing session *is* in this engine: a replay of mutations over
  a base document, the owner's "replay system and game at the same time"
  applied to authoring.
- Protocol version handshake on `SessionRequest.Join` **with rejection**
  (`Accepted: false` + reason on mismatch).
- **Principals and grants (§2.7):** every submission carries its acting
  principal; the server-side grant table with drive/control/mutate/edit
  capabilities over stable-id subjects; `world.grant`/`world.revoke` verbs;
  mutation application checks grants; `WorldEngagement` re-expressed as a
  view over drive/control grants (one table, not two — dedup per §0.1).
  Permissive local defaults keep behavior identical until revoked.
- **Addon-as-principal proof:** mount the existing `AddonHost` in World
  (project reference to `Puck.Scripting` — consumed, not modified; addon rows
  come from the world doc's Phase 1 `addons` section), translate
  `AddonCommand → PlayerIntent` (the demo's translation shape, implemented
  World-side), and prove an authored `.wat` autopilot: granted a body, it
  drives through the same `IServerLink` as a human seat; `world.revoke`
  stops it mid-run. This is the §2.6/§2.7 keystone proof — a non-human
  principal, driving through the neutral contract, placement-free.
- Console verbs as the dev reflection of every mutation (`world.kit.set`,
  `world.screen.add`, … — exact verb names settled in the phase brief), so an
  agent can mold a running world over stdin today, and the editor arc reuses
  the identical messages tomorrow.
- **Trap:** the stdin drain barrier — sim-mutating verbs already hold only
  Immediate lines; keep new verbs on the same discipline or proofs serialize.

### Phase 3 — The player document + bindings vertical slice

- Define `puck.world.player.v1` as the **catalog** of §2.3 (profiles with
  identity/motion/bindings/preferences + document `Revision`); absorb + retire
  `puck.world.profiles.v1` with a one-time on-disk migration;
  `WorldPlayerDocumentValidator` (binding sections gate through
  `BindingProfile.Compile`).
- **Adopt the existing `Puck.Commands` stack — no lift.** Author World's
  engine-default `BindingProfileDocument` (the data-file successor of
  `inputBindingTable`, speaking `player.*` commands); replace
  `SharedInputBindings` with one `PagedInputBindings` per seat built from the
  §2.4 pre-merge; reconcile the console `BindingCommandSource` onto the same
  composed documents (per-slot or rebuilt — brief decides).
- Implement the document pre-merge with the `(page id + ordered chord,
  source)` key; per-world overlay section (`bindingOverlays`) in the world
  doc, exercised by one shipped example (the kart kit remapping a lane).
  Conditional engine seam: per-entry ids on `BindingPageEntryDefinition` only
  if the brief demands them.
- `SetPlayerSection`/`GetPlayerDocument` protocol messages (seat→profile
  selection via `SetProfile` stays Session scope). Rebind verbs:
  `player.bind <source> <command>`, `player.bindings`, `profile.save`;
  hot-reload via the existing `Reload` path.

### Phase 4 — Cloud readiness (can overlap Phase 5)

Scoped to §2.5's "lands in THIS arc" list; everything is proven against the
local backend.

- Engine seam (`src/Puck.Storage`): version-token round-trip + conditional
  overwrite per §2.5.2 — touching `ObjectBlobReadResult<T>`,
  `IObjectBlobStore`, `JsonObjectBlobStore`, `ProfileDocumentStore<T>`, both
  backends, and `WorldProfileStore` (listed so the file-ownership map is
  honest); Azure backend `IDisposable` while in the file. The seam is the
  same code the cloud backend will use — that is the point.
- Document `Revision`/`UpdatedAtUtc` + persisted `lastSyncedRevision`;
  per-profile blob layout on the local target; the
  `IPlayerStorageIdentityResolver` contract with explicit-override and
  decline implementations only; endpoint reserved as a data-file field;
  `storage.status` verb reporting the honest state (identity absent, cloud
  unwired, local authoritative).
- **Deferred (cloud arc):** Azure target construction, claims-based identity,
  sync policy + `storage.sync` live round-trip, Azurite proofs, the
  owner-assisted smoke, and the optional `Puck.Storage` Post stage question.

### Phase 5 — Session write-back

- `world.save [path]` serializes the live definition (including mutations
  applied since load, current render settings, population defaults, screen
  inserts) back to the world file — the proto-editor loop closes: mold a
  running world over stdin, save it, boot it back.
- Live-but-unsaved session state is surfaced honestly (`world.status` shows
  dirty-vs-file, backed by the Phase 2 mutation journal).
- **The second world — `expo.world.json`.** Author a second checked-in world
  file deliberately far from the default (different kit rows and tuning, a
  different assignment policy, different screens/scene), built the honest
  way: scripting mutations against a running server and `world.save`-ing the
  result. This is the standing §2.6 proof artifact — booting
  `--world expo.world.json` yields a visibly different game with zero code —
  and it keeps the loader honest forever (a one-document loader rots into a
  deserializer for its only input; a second document keeps it a format).

### Phase 6 — Docs + ledger

- Update `docs/project-map.md`, `docs/capability-catalog.md`, the World
  README(s); record the retired `puck.world.profiles.v1`; write the
  editor/UI-arc handoff note listing the surfaces they inherit (mutation
  vocabulary, player sections, binding layers, `Extensions` bags).

## 4. Orchestration protocol (for the implementing agent)

Run this arc as a phase pipeline with three subagent tiers:

- **Fable 5 (`model: fable`) — thinking.** One design brief per phase before
  any code: exact record shapes, verb names, merge-key semantics, file
  ownership map, and the phase's verification recipe. Also the adversarial
  review gate at the end of each phase (review the diff against this plan +
  repo doctrine; findings fixed before commit). Fable never does mechanical
  labor.
- **Opus 4.8 (`model: opus`) — coding.** The design-heavy implementations:
  the document loaders + validators + polymorphic serializer work, the
  mutation vocabulary and its tick-boundary application, the binding
  pre-merge and dual-consumer reconciliation, the storage version-token +
  sync policy, the identity resolver.
- **Sonnet 5 (`model: sonnet`) — grunt.** Mechanical extraction of
  `WorldDefinition.Default` literals to JSON (after Opus lands the serializer
  shape), verb plumbing, proof-script authoring and runs, doc updates,
  lock-file churn, call-site migrations after renames.

Working rules:

1. **Brief first, then fan out.** Within a phase, Opus and Sonnet tasks run in
   parallel only when the brief's file-ownership map shows no overlap. Shared
   tree: every agent commits with `git commit -- <its paths>` (staged files
   from a co-agent sweep into commits otherwise).
2. **Verify by running.** Each phase's exit is: `dotnet build` clean, the
   World proof scripts green (extend `src/Puck.World/scripts/proof.cs`
   with a proof per phase: doc-load boot parity, a scripted mutation round
   -trip, a rebind-and-persist round-trip, a local-store Revision/version
   -token round-trip), and a live run of Puck.World exercising the phase's
   verbs over stdin. No Post promotion for World features.
   **The quality bar (§0.1) is part of every review gate:** the Fable
   reviewer checks each phase's diff for steady-state allocations, locks on
   the tick path, float leakage into sim-adjacent state, and duplicated
   primitives — findings there block the commit exactly like correctness
   findings do.
3. **Baked-default parity proof (Phase 1 exit bar):** booting with the
   checked-in default world file must produce the same world as the baked
   default (the existing tableaux/proof harness is the comparator), and
   load→save→load reproduces the file byte-for-byte (the ouroboros gate). A
   deliberate content change to the file is expected to diverge — that is the
   feature.
4. **Scope fences:** `src/Puck.World` + the flagged seams
   (`src/Puck.Storage` Phase 4; `Puck.Commands` per-entry ids only if the
   Phase 3 brief demands them; a Phase 2 project *reference* to
   `Puck.Scripting` — consumed, never modified) only. Anything else an agent
   thinks it needs is a finding for the Fable review, not an edit.
   **Every review gate also asks the §2.6 audit question** of each new or
   changed contract surface: would an RTS/FPS/RPG/MMO/puzzle world need a
   different message here, or just different data?
5. **Standing traps** (from the repo's scar tissue): never delete or revert
   unrecognized files (parallel sessions exist); commit `packages.lock.json`
   churn forward, never strand it; STJ `IncludeFields` for numerics; the
   Wasmtime `[44.0.0]` pin is load-bearing — never bump incidentally; stdin
   sim-verb drain discipline (hold only Immediate lines).
6. **Commit hygiene:** phase commits on `features/it-starts`, hand-written
   summaries, no `Co-Authored-By` trailers.
7. **Docs stay current per phase.** The World README sections a phase touches
   are updated in that phase's commit — parallel sessions read the README as
   the vision contract sheet; a stale one mid-arc misleads them. Phase 6 is
   the catalog/project-map sweep, not a license to batch README debt.

## 5. Exit criteria for the arc

1. Booting Puck.World reads `default.world.json`; deleting it falls back to
   the baked default with a loud line; `--world` selects any file.
2. A stdin script can: rebind a button per seat, change a kit's tuning,
   add/remove a screen, save the world file, and see every change survive a
   relaunch — all through protocol messages, no restart, no code edit.
3. A player document (catalog + bindings) round-trips through the storage
   stack under per-profile blob keys on the **local backend**, with
   Revision/version-token semantics observable in `storage.status` — and the
   cloud arc can be started with **zero reshaping**: the ordering fields,
   storage seam, blob layout, identity contract, and endpoint field are all
   in place, with `storage.status` truthfully reporting cloud-unwired.
4. An addon boots from the world doc's `addons` section, is granted a body,
   and drives it through the same protocol as a human seat; `world.revoke`
   stops it mid-run — a non-human principal exercising the neutral contract
   end-to-end.
5. The editor and UI arcs can be briefed entirely in terms of surfaces this
   arc shipped: mutation records, player sections, binding layers, principals
   and grants, stable ids, `Extensions` bags — with zero new "make X data"
   prework. And every surface passed the §2.6 audit: genre would arrive as
   data, engines, and addons — never as a protocol change.

## 6. Settled questions — verified during planning, do not re-litigate

The adversarial reviews checked these against the working tree; re-deriving
or re-flagging them wastes a session. (Pattern per the disposal audit: a
refuted-findings ledger prevents repeat work.)

1. The binding-profile stack (`BindingProfileDocument` v7,
   `BindingProfile.Compile`, `CompiledBindingProfile`, `PagedInputBindings`
   with per-slot chords + `Reload`) **lives in `Puck.Commands`**, public.
   Demo contributes only a default-document factory and a store wrapper.
   There is no lift to do and no Demo copy to migrate.
2. Compiled-level `LayeredInputBindings` is **not** the layering primitive
   for per-entry overlay — it composes wholesale per `(slot, source)`.
   Document pre-merge keyed `(page id + ordered chord, source)` is the rule
   (§2.4). Don't swap back.
3. The player scope is a **catalog** (seat-selectable profiles), not a
   single-person record — flattening it regresses couch co-op (§2.3).
4. ETags are opaque: they **guard** (if-match), they cannot **order**.
   `Revision` orders; the two are separate mechanisms (§2.5).
5. `Guid.ToString()` is a valid Azure container name (verified against
   container-naming rules).
6. Section-granular protocol messages do **not** imply section-granular
   storage; the storage grain is per-profile blobs, and same-profile
   cross-device concurrency is whole-profile LWW with detection (§2.5.3).
7. Azurite's connection-string path carries no Entra principal — it can
   never prove the identity resolver, only blob mechanics (§2.5.6).
8. The ownership precedent is real but ad-hoc in exactly three places
   (`WorldEngagement` latch, demo machine-input ownership,
   `AddonHost.SlotOwner`) — §2.7 unifies them; don't invent a fourth.
9. `WorldServer.Step`'s buffered-drain shape fits mutations as-is; commands
   apply in the pre-Step window by documented deviation (read-after-write in
   one stdin batch) — mutations buffer like intents, don't "fix" the
   command path.
10. `puck.run.v1` is not the player-data home — argued and decided (§2.3).

## 7. Session kickoff prompt

Any implementing session (this agent or another) starts from this, verbatim:

> Read `docs/reviews/2026-07-17-world-moldable-state-plan.md` end to end
> before touching anything — it is the contract, including its §0.1 quality
> bar and §6 settled questions. Execute exactly ONE phase: the first not yet
> landed (check `git log` on `features/it-starts` and the working tree —
> parallel sessions exist, the tree moves; re-verify any §1 claim you depend
> on before building on it, and never delete or revert files you don't
> recognize).
>
> You are the orchestrator (Fable-tier). Per §4: write the phase design
> brief first — exact record shapes, verb names, merge semantics, the
> file-ownership map, the verification recipe. Then fan out: Opus 4.8 for
> the design-heavy pieces, Sonnet 5 for the mechanical ones, parallel only
> where the ownership map shows no overlap. Verify by running: clean build,
> `src/Puck.World/scripts/proof.cs` green including this phase's new proof,
> and a live stdin session exercising the phase's verbs. Then run the review
> gate: §0.1 (steady-state allocations, locks on the tick path, float
> leakage into sim-adjacent state, duplicated primitives) and the §2.6 audit
> question on every new or changed contract surface. Fix findings before
> committing. Commit only your paths (`git commit -- <paths>`) with a
> hand-written summary; update the World README sections you touched in the
> same commit. Stop at the phase boundary and report.
>
> Hard fences: §4.4 scope; §2.5's deferred-cloud list stays deferred; no
> Post stages for World features.
