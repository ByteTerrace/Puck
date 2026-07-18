# Puck.World moldable-state handoff — 2026-07-18

The moldable-state arc (`docs/reviews/2026-07-17-world-moldable-state-plan.md`)
landed as six commits on `claude/moldable-state-plan-a86832`: `74f26b4`
(Phase 1, world document), `cea24c6` (Phase 2a, mutation vocabulary + journal),
`579fbc4` (Phase 2b, principals + grants), `41f3c54` (Phase 3, player document +
bindings), `93d8159` (Phase 4, cloud readiness), `df07af3` (Phase 5, session
write-back + expo world). All exit criteria in the plan's §5 are met. This note
is the condensed reference for the editor and UI arcs — the plan's §6 exit
criterion 5 requires they can be briefed entirely from shipped surfaces, with
zero new "make X data" prework. Full detail lives in
`src/Puck.World/README.md`; this note only condenses and points.

## (a) The three scopes, as built

| Scope | Document | Authority | Persistence | Built shape |
|---|---|---|---|---|
| **World** | `puck.world.def.v1` (`WorldDefinition`) | Server | `Assets/worlds/*.json`; `--world <path>` loads, `world.save [path]` writes back, `world.load <path>` swaps live | `WorldDefinitionValidator` (the one thick gate); `WorldJsonContext` source-gen with `$type`-discriminated screens/cameras/predicates/effects; three checked-in worlds (`default`, `kart-remap`, `expo`) |
| **Player** | `puck.world.player.v1` (`WorldPlayerDocument`, `src/Puck.World/WorldPlayerDocument.cs`) | Server per-session; user-owned durably | `WorldProfileStore` (`Server/WorldProfileStore.cs`) — per-profile blob split, local-always / cloud-per-user-when-identity-present routing | catalog of stable-id profiles, monotonic `Revision`, `WorldPlayerDocumentValidator` |
| **Session** | none (live state) | Server | Not durable by itself; folds into `world.save` or `profile.save` | `WorldSessionCapture` composes the fold; render levers, live census + peer-source default, runtime `screen.insert` binds |

`puck.world.player.v1` absorbs and retires `puck.world.profiles.v1` (one-time
on-disk migration, both retired shapes covered, old file deleted, loud line).

## (b) The mutation vocabulary

Every world-document section is molded through one kind-tagged
`WorldMutation` record set (`Protocol/WorldMutation.cs`), submitted over
`IServerLink.SubmitWorldMutation`:

`UpsertKit` / `RemoveKit` / `SetDefaultSeatKit` / `SetKitAssignment` /
`UpsertScreen` / `RemoveScreen` / `UpsertCamera` / `RemoveCamera` / `SetScene` /
`SetSpawns` / `SetMotion` / `SetWander` / `SetPopulationDefaults` /
`SetRenderDefaults` / `UpsertAddon` / `RemoveAddon` / `UpsertBindingOverlay` /
`RemoveBindingOverlay` — 18 records over the 11 `WorldSection` values (kits,
screens, cameras, scene, spawns, motion, wander, population, render, addons,
bindings). Each is whole-row, keyed by stable id (or index for screens).

**Buffering discipline.** Mutations buffer like intents and apply at the tick
boundary: compose a candidate → revalidate the WHOLE document through
`WorldDefinitionValidator` → reject loudly on failure (definition unchanged) →
on success swap, journal, rebuild only the changed section's derived state,
deliver. `SubmitDefinition` (whole-document swap) buffers the same way.
`WorldMutationCommandModule` verbs route `CommandRouting.Simulation`, so the
stdin drain barrier holds a following `Immediate` read (`world.status`) until
the buffered edit applies — no polling needed for a scripted
mutate-then-read pair.

**Capacity honesty.** An over-envelope scene/screen edit rejects loudly
against the probed render envelope (`WorldRenderEnvelope`) — never a crash,
never a silent clamp.

**The journal is the undo engine, day one.** The server appends every applied
mutation, tick-stamped, to a session journal; `dirty` in `world.status` IS the
journal length. `world.undo [n]` restores the loaded base and replays the
tail-trimmed journal through the SAME apply path — no per-mutation inverse
logic exists anywhere, because replay IS undo. `world.save` compacts the
journal (the saved definition becomes the new base, journal clears). This is
what the editor arc inherits for undo/redo on day one: an editing session is
already, mechanically, a replay of mutations over a base document.

## (c) Principals + grants

`WorldPrincipal` (`Protocol/WorldPrincipal.cs`) kinds: `Seat` (0-based slot
0..3), `Console` (the one non-seat local authority), `Addon` (named), `Peer`
(0-based entity index 4..127). Every `IServerLink` write submission carries
one.

`WorldGrants` (`Server/WorldGrants.cs`) is the ONE table: a grant is
`(principal, capability, subject, exclusive)`.

- **Capabilities** (`WorldCapability`): `Drive` (submit a body's intents/
  commands), `Control` (engage a screen/machine route), `Mutate` (apply a
  mutation targeting a `WorldSection`), `Edit` (a player-profile section —
  now wired: `profile.save` gates on it, per the Phase 3 commit).
- **Subjects** (`GrantSubject`/`GrantSubjectKind`): `All`, `Body:<n>`,
  `Screen:<n>`, `Section:<WorldSection>`.
- **Exclusivity** is a property of a grant, not a bespoke mechanism: a second
  exclusive acquisition of the same `(capability, subject)` by a different
  live principal is rejected loudly; re-granting a subject a principal
  already holds is idempotent.
- **Permissive local defaults**: every seat holds `Drive` over its own body
  and `Control`/`Mutate` (every section)/`Edit` over its whole domain; the
  console holds `Drive` over every body and the same domain grants; every
  population peer holds `Control` over every screen; addons hold NOTHING
  until granted.

**Addon-as-principal — the reference proof.** A `WorldAddonRow` in the world
document's `addons` section is data-only; mounting happens once, at BOOT
(`Client/WorldAddonDriver.cs`, consuming `Puck.Scripting`'s `AddonHost`, never
modifying it). A mounted addon holds no body until
`world.grant addon:<name> drive body:<n> [exclusive]`; the driver discovers
its body from the grant table, translates the guest's pad commands to
`PlayerIntent`, and submits over the SAME `IServerLink` a human seat uses,
principal `addon:<name>`. `world.revoke` stops it mid-run (edge-latched denial
line; the body idles rather than silently resuming wander). `proof.cs grants`
is the reference: mount → save → relaunch (asserts the boot mount line) →
grant (asserts driven motion) → revoke (asserts frozen) → a section-mutate
denial/re-grant round-trip.

`WorldEngagement` is now a VIEW over `Control` grants, not a parallel table —
`proof.cs screens` is its regression coverage; `proof.cs grants` does not
duplicate it.

**Known code-comment staleness:** `WorldCapability.Edit`'s XML doc in
`Protocol/WorldGrant.cs` still reads "gates nothing until the Phase 3
player-document arc wires it" — Phase 3 landed and wired it
(`profile.save` is Edit-gated); the comment was not updated in that commit.

## (d) The player document + binding layers

`puck.world.player.v1` is a **catalog**: `Profiles[]`, each a stable `Id` and
four sections — `identity` (display name + `#RRGGBB` color), `motion`
(speeds + look-invert), `bindings` (`BindingProfileDocument?`, null = inherit
engine default), `preferences` (open bag) — plus a document-level `Revision`
and `Extensions`.

**Section messages.** `SetPlayerSection` targets one profile's section
(bindings | identity | motion | prefs); the server owns the durable document,
bumps `Revision`, persists, and acks. `PlayerDocument` (a `WorldQuery`)
echoes the whole document as JSON via `profile.doc`.

**The §2.4 merge key.** Bindings resolve through a document PRE-MERGE, not a
compiled-level composition:

```
effective document = engine default BindingProfileDocument
                   ⊕ world overlay(s)            (puck.world.def.v1 bindingOverlays)
                   ⊕ player profile bindings      (puck.world.player.v1)
                   ⊕ live rebinds                 (session layer; folds into the profile on profile.save)
```

The merge key is **`(page id + ordered chord, source)`** — a later layer's
entry at that key REPLACES the earlier layer's; new sources/pages append.
This is deliberately below `PagedInputBindings`/`LayeredInputBindings`
(compiled-level composition is wholesale per `(slot, source)` and cannot
express "override one entry inside a shared page," which a per-world overlay
needs).

**Per-seat Reload.** The merged document compiles once per seat through the
existing `BindingProfile.Compile`, then hot-swaps in via
`PagedInputBindings.Reload` — recompose-and-reload only on change (profile
switch, rebind, overlay mutation), never per frame. `WorldSeatBindings`
replaces the former single shared `inputBindingTable`/`SharedInputBindings`.
The console dispatch path (`BindingCommandSource`, dormant in World) derives
from the SAME composed base (`WorldSeatBindings.ConsoleBaseTable`) — no second
authoring grammar.

**Rebind verbs the chord-first UI arc sits on**: `player.bind <seat> <source>
<command>` (live session-layer remap, Simulation-routed), `player.bindings
[seat]` (Immediate echo of the composed active mapping), `profile.save
[seat]` (folds the session layer into the profile's durable `bindings`
section via `SetPlayerSection`, Edit-gated, then empties the session layer),
`profile.doc` (Immediate whole-document echo), `world.bindings.set
<overlay-json>` / `world.bindings.remove <id>` (per-world overlay upsert/
remove, recomposes every seat on apply).

## (e) Storage readiness

**Two ordering mechanisms, kept separate.** `Revision` (monotonic, bumped on
every save) + `UpdatedAtUtc` (tiebreak) is the ORDERING key, living in the
document. The storage `VersionToken` (`ObjectBlobReadResult<T>`/
`ObjectBlobWriteResult` — a SHA-256 content hash locally, the Azure download
ETag once wired) is the CLOBBER GUARD via an optional if-match on
`WriteAsync` (`ObjectBlobWriteMode`, a `PreconditionFailed` result flag).
ETags guard; they never order.

**Layout.** `WorldProfileStore` splits the catalog under the user's container
(today `%LOCALAPPDATA%/Puck/World/<b1d5c0de-0002…>/`) into `world/player.json`
(catalog: schema, `Revision`, `UpdatedAtUtc`, ordered profile-id list,
`Extensions`), `world/profiles/<id>.json` (one profile body per entry), and
`world/local.json` (machine-local sidecar: boot-seat id +
`LastSyncedRevision` — never roams). This is the SAME address model the
future cloud container uses.

**Derived dirty.** `WorldProfiles.Dirty` = `Revision > LastSyncedRevision` —
crash-safe by construction (both numbers persisted). With no cloud wired,
`LastSyncedRevision` stays 0 forever, so `Dirty` is always true — honest, not
a bug.

**Identity contract.** `IPlayerStorageIdentityResolver`
(`WorldStorageIdentity.cs`) resolves a principal to a per-user container id,
or DECLINES. Exactly two implementations ship this arc:
`ExplicitOverridePlayerStorageIdentityResolver` (`--user-id`/data-file
override, an Entra-`oid`-shaped Guid; a non-Guid value declines loudly) and
`DecliningPlayerStorageIdentityResolver` (the local-only default). No token
parsing, no claims flow.

**The reserved endpoint.** The world doc's `storage` host-section
(`WorldStorageDefaults`: `endpoint`, `userId`) is RESERVED — nothing
constructs an Azure target from it this arc. `--storage-uri`/`--user-id`
overlay the document defaults at boot; `storage.status` echoes both.

**Honest deferred list for the cloud arc (nothing above needs reshaping):**
constructing an `AzureBlobObjectStorageTarget`, the real claims-based
identity implementation, the cloud-async sync policy + retry loop,
`storage.sync`'s live round-trip, Azurite proofs, the owner-assisted smoke
against `blob.byteterrace.com`, and the optional `Puck.Storage` Post-stage
question. `storage.status` reports this truth today rather than pretending
otherwise: `tier local (authoritative); cloud unwired`.

## (f) Stable-id + `Extensions` conventions

Every row a document carries is addressed by a string id (or index for
screens, which are position-addressed): kits, screens, cameras, spawn
points, boulders, addon rows, binding-overlay rows, profiles. Mutations
target ids; a future editor/plugin references ids; ids never carry meaning
beyond identity.

Both document families carry `[JsonExtensionData] Extensions` bags — `puck.
world.def.v1` at the document level (`WorldDefinition.Extensions`,
`IDictionary<string, JsonElement>?`), `puck.world.player.v1` at both the
document and per-profile level — the same extensibility posture as
`PuckRunDocument`. Unknown sections/fields survive a round-trip untouched;
this is the data-side plugin story until the addon-ABI arc lands host
imports.

## (g) The standing §2.6 audit question

Every new or changed contract surface future arcs propose — an editor
message, a UI binding surface, a genre-specific verb — must answer: **"Would
an RTS / FPS / RPG / MMO / puzzle world need a different message here, or
just different data?"** If the answer is "a different message," the surface
is wrong — generalize it or move the specificity into data. This is the
calcification audit applied to the wire, and it is not retired with the arc;
it is a standing review-gate question for whatever builds on this substrate
next.

## (h) Known seams and accepted asymmetries

- **Cameras are document-only.** `world.camera.set`/`.remove` upserts the
  document row; a camera change applies at next boot, not live.
- **Addon mounting is boot-time only.** `world.addon.set`/`.remove` edits the
  document row (data-only); `AddonHost` composition from enabled rows happens
  once, at boot. There is no live remount verb.
- **Screen-source live-apply is index-scoped.** Per the Phase 2a commit,
  screen sources apply live for EXISTING screen indices with geometry fully
  live; population/render/camera/addon defaults stay document-only echoes
  this arc (population/render session state does fold back into the document
  on `world.save`, per Phase 5, but the mutation verbs themselves are
  document-only edits, not live re-applies).
- **Session drift persists past a save.** The Phase 5 fold is
  saved-bytes-only — it composes the serialized snapshot and never mutates
  the in-memory definition or journal — so `world.status`'s `session-drift`
  hint keeps naming which live dimensions (render/population/screens) differ
  from the in-memory document even right after a save reproduces the file;
  it is a live-vs-document comparison, not a "since last save" flag.
- **`BindingCommandSource` is dormant in World.** The console dispatch path
  still exists and derives from the same composed base layers, but
  `InputRouter` owns all physical input in World, so nothing drives dispatch
  through it today.
- **Per-profile `Edit` subjects are not yet granular.** The `Edit` capability
  scopes to a section kind (identity/motion/bindings/prefs via
  `SetPlayerSection`'s target), not to which catalog profile is being
  edited — a seat's permissive default grants `Edit` over its "whole domain,"
  not a specific profile id. Finer-grained per-profile trust is unbuilt.

## Where to look next

- `src/Puck.World/README.md` — sections "Moldable state," "Principals and
  grants," "The player document + bindings," and "Storage" carry full
  prose detail behind each condensed section above.
- `docs/reviews/2026-07-17-world-moldable-state-plan.md` — the executed plan,
  including the §0.1 quality bar and §6 settled questions that still apply to
  new work on this substrate.
- `src/Puck.World/scripts/proof.cs` — `worlddoc`, `mutate`, `grants`,
  `bindings`, `storage`, `expo-author`, `expodoc` are the live, runnable
  reference behavior for everything in this note.
