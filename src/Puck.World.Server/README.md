# Puck.World.Server — the authoritative world runtime

This project is the server half of the world game: the entity table, the tick
step, the capability-grant authority model, the P7 TCP network transport,
the addon host seam, player profiles and their storage, and the deterministic
replay codec. It consumes
the document and protocol shapes from
[`Puck.World.Schema`](../Puck.World.Schema/README.md) and
[`Puck.World.Protocol`](../Puck.World.Protocol/README.md) and knows nothing about
rendering or input devices — the same architecture lane profile that fences
those two projects (see `build/Architecture.props`) denies this project every
presentation and backend assembly. The composition root that hosts it is
[`Puck.World`](../Puck.World/README.md).

Project references: `Puck.World.Schema`, `Puck.World.Protocol`, `Puck.Networking`,
`Puck.Storage`, and `Puck.Hosting`. The addon guest runtime itself is
[`Puck.World.Addons`](../Puck.World.Addons/README.md), which references this
project rather than the reverse — see `IWorldAddonHost` below.

## The tick (`WorldServer.cs`)

`WorldServer.Step` advances one exact fixed tick, in a pinned order its own
XML documentation states: tick the mounted addon guests
(`IWorldAddonHost.TickAddons` — decodes and validates, applies nothing) →
drain the buffered live edits (mutations and whole-document swaps) → drain the
buffered intents → apply the guests' contributions
(`IWorldAddonHost.ApplyContributions`) → fold each human-occupied body's
contributions (`FoldChannelContributions`) → settle per-body contention →
advance every body → resolve the guests' reads
(`IWorldAddonHost.ResolveReads`) → deliver the tick's `WorldSnapshot`.

Every non-intent submission arrives as one `SubmissionEnvelope` through
`WorldServer.Submit` — a single ordered domain, drained in submission order.
Enqueue and drain both run under the same authority gate `Step` and every
federation operation hold (`WorldServer.EnqueueOrdered` is the one door), so the
queue and its reentrancy guard are single-threaded state. A drain reached
without that gate can be skipped by another thread's in-flight drain, which
would leave an already-applied population change — an admitted arrival — standing
without the grant rows its own queued event carries.
The same queue also carries the server-authored `PeerAdmitted` and
`PeerDisconnected` entries; clients cannot submit those events. They apply
through the population/grant doors and are exposed to the replay tape only
after their point of effect.
On the in-process loopback that drain runs inline on the tick thread before
the `Submit*` call returns, so commands, grants, session requests, and queries
apply synchronously at submit, while definition swaps and mutations buffer to
the tick boundary. The practical consequence for scripts: within one stdin
batch, a grant submitted before a command is visible to that command, and a
mutation followed by an `Immediate` read is serialized by the console's drain
barrier (see the console section of
[`Puck.World`'s README](../Puck.World/README.md)). Results return through
typed completions (`WorldSubmissionResult`), and deliveries fan out through
`WorldOutputHub.cs`, which supports multiple subscribed sinks.

## Gravity fields

`WorldGravityField` is the one authoritative gravity evaluator. It gathers
bodies in stable entity order, runs the selected global solver once, adds the
uniform acceleration, then folds matching compiled local areas in stable
priority/authored order. Local areas remain fixed-point and placement-relative:
static rows use authored pose, while attached rows refresh through
`WorldPlacementAttachment.TryResolve` before the tick's solve. Per-entity
participation is separate from vector magnitude, so a zero Replace, exact
cancellation, or radial center suppresses kit fallback, while a body outside
every area in an areas-only document retains it. The same participation verdict
gates gravity-derived ambient orientation when a body crosses an area edge and,
under the surface-following body-frame policy, gates contact-normal orientation.
`gravitationalConstant > 0` runs the global body-source solve even with no
static attractors. Composition additions saturate per Q48.16 component instead
of wrapping, and a later Replace remains an ordinary assignment.

**Mutations, the journal, and undo.** A `WorldMutation` applies by composing a
candidate document, revalidating the WHOLE document through
`WorldDefinitionValidator`, and only then swapping, journaling, and rebuilding
the changed section's derived state; a failure rejects loudly and changes
nothing. The journal is the undo engine: `world.undo` restores the loaded base
definition and deterministically replays the journal minus its tail through
the same apply path — no per-mutation inverse exists. Market listings, bids,
buyouts, cancellations, and settlements are economic finality barriers:
`world.undo` may remove later authoring edits, but refuses before crossing one
of those entries. Retention pruning moves no value and remains undoable.
`world.save` writes a canonical session snapshot and compacts the journal (the
saved definition becomes the new base). `world.reset`/`world.load`/`world.reload` are ONE
rebuild-and-swap mechanism (`WorldServer.ApplyRebuild`) over three document
sources — the server's own base, a different file, or a re-read of the
current origin — that also wipes and re-seeds the ENTIRE runtime grant table
(`WorldGrants.Reset`, replaying only the new document's own `Grants` section
plus every admitted peer connection's re-minted admission grant; every other
live `world.grant` acquisition drops). The `dirty` count in `world.status` IS
the journal length.

World-rule failures accumulate in a fixed-size category table. The first
occurrence is narrated; repeated Level-rule failures only increment their
counter. `world.rule.failures` reports the count and latest tick/rule/effect/reason.
Rule/interaction installation is also guarded by a static aggregate work budget,
reported beside evaluation slots in `world.budget`; dynamic body-index keys use
a prebuilt string cache on the evaluation path.

**Lifetime sweeps.** Five per-tick passes run side by side at the end of
`WorldServer.StepCore`, each firing ORDINARY mutations under
`WorldPrincipal.World`'s structural exemption so recovery is journalled rather
than a bespoke erase: `ReclaimExpiredEscrows` (an unaccepted ownership offer),
`SettleExpiredMarketListings`/`PruneExpiredMarketListings` (a
listing past its deadline, a terminal row past `market.retentionSeconds`),
`SweepContributionTenure` (`WorldServer.Contributions.cs` — a presence-tenure
contribution slot whose watched `adjacencies` row has read dropped past the
slot's own `graceSeconds`), and `SweepPlacementResponses`
(`WorldServer.Responses.cs` — right after `StepFields`, so it reads this
tick's own lattice writes: the first `WorldPlacementResponse` entry whose
condition holds at a placement's coupled cell becomes its prototype). The
contribution sweep reads link liveness through `WorldServer.TryLinkLiveness`,
which pairs `WorldEventFeed.LinkStalenessTicks` with the row's compiled
`livenessGraceSeconds`; its retraction defers, rather than proceeding, while
the slot's inhabitant is drive-possessed. Market settlement is journal-final;
market retention pruning and the other recovery mutations remain undoable.

**Steady-state performance contract.** The per-tick pipeline — intent fold,
sim step, snapshot emission, binding resolution — allocates nothing; document
and JSON work is confined to the boundaries (load, save, and mutation
application), and a mutation rebuilds only the changed section's derived
state, never the whole document's.

## The field lattice (`WorldFieldLattice.cs`)

The live cell values of a `state.lattices` topology, and the reactions that
evolve them — simulation state beside the population, values `FixedQ4816`,
every reaction integer arithmetic in a fixed cell order, so one document and
input reproduce the same fields bit for bit. `WorldServer.StepFields` runs
after the rules (so a tag a rule wrote this tick is what an `emit`/`expose`
reaction reads this same step) and before the snapshot (so the step's cell
writes ride this tick's delivery), on the topology's own `stepEveryTicks`
cadence. A reaction scalar (literal or `{"row": "name"}`) resolves through
`ReadScalarSlot`, the SAME `WorldStateReader.TryRead` seam every other state
read uses — a season row a rule writes and a reaction reads can never
disagree about the value. `expose` writes land through the ordinary
`UpsertStateCell` mutation (`WorldPrincipal.World`, journaled, undoable), never
a bypass. Cell values are checkpointed (`WorldFieldCheckpoint`) and delivered
as `FieldCells` deltas on the snapshot (`FieldsFull` on a primer) — never
document rows, so nothing journals them directly.

`WorldFieldLattice` receives the complete `WorldFieldsSection` companion plus
the already-compiled `WorldFieldProgram`: the companion remains authoritative
for topology, cadence, paint, and presentation, while the typed program is the
one executable reaction IR. `StepFields` reads and writes reaction state by
`WorldStateHandle`, not by repeating row-name lookup. A whole-document rebuild
may replace compatible reactions in place without resetting cells, deltas,
revision, or checkpoint shape; adding/removing a lattice or changing topology,
cadence, or a field envelope refuses and asks for a host restart. The
`world.fields` read-back includes installed node order, dependency edges, and
cell/body pass counts.

## Simulation authority

Every entry in the entity table is a simulated player advanced on the server
from a `PlayerIntent` — no entity is pose-driven, and poses are never accepted
from outside the simulation. Drivers (seats, console verbs, addon guests,
authored producers, replay tapes) only produce inputs; poses flow out through
the tick snapshot. Simulation state is `Puck.Maths` fixed point and exact
engine-tick durations throughout — no wall clock, no RNG, no float. That
determinism is a design contract verified by running and by the replay verbs
below; no build gate enforces it for this game (see `CLAUDE.md` rule 3).

A body's pose is always six-degrees-of-freedom (a `Vector3` position and a
quaternion attitude); its motion model (`grounded` or `free`) decides how an
intent integrates. Ways of moving are DATA: a `WorldKit` row in the world
document names a motion program, tuning, producer parameter maps, and action bindings, and
entities distribute across kit rows by the document's assignment policy. A new
way of moving is a new row, not an engine enum.

Each entity carries one `IntentSource` — what fills its intent gaps between
scripted tape segments: `live` (the submitted stream), `idle` (hold still), or
`producer:<name>` (an authored producer program declared by the selected kit). The per-tick merge rule is
tape > submitted > producer > zero.

## The entity table (`WorldPopulation.cs`, `WorldBody.cs`)

Capacities are single-sourced in `WorldPopulationLimits`
(`Puck.World.Schema`): up to 128 authoritative bodies, of which indices 0–3 are
the reserved local seats and the rest host simulated stand-ins and network
peers. `WorldBody` owns one entry's integration, pose, tape, motion model, and
action state. Bodies advance against the one contact-resolution seam
`IContactField.cs`, which has two providers: the analytic `WorldColliderSet`
(document-derived convex colliders) and the SDF-backed `WorldSolidField.cs`.
Both include solid scene rows, screen frames, and the shapes emitted by solid
creation placements. The field compiles those surfaces into one fixed-point
signed-distance program. The analytic provider emits exact isotropically
scaled spheres and world-axis bounds for other finite placement primitives;
rotated, rounded, non-box, smoothed, and boolean-carved geometry is therefore
conservative there. A solid row participates in simulation, which is why
mutating scene, screen, creation, or placement geometry is a real authority
widening.

Body-frame policy is compiled separately from that provider seam. Every body
uses opposed solved gravity (or the contact field's ambient up fallback) as its
ambient frame. Authoring `GradientDerivedUp` additionally selects
surface-following: a measured walkable support normal may orient a grounded
body. Without it, the normal remains a grounding fact, so a rounded lip cannot
silently pitch the body. A live collision rebuild installs the new policy
beside the new provider; the adoption rule is authoritative on the next step,
and a defined new ambient direction reseats the held axis then.

A kit shaping its planar velocity through a `dynamics` row (rather than the
engage/release response table) carries the follower's Q32 state — position
and velocity raws, plus the previous commanded target the `r` term needs —
as ordinary `WorldBody` sim state (`WorldBody.Dynamics.cs`); a medium hold's
vertical lane carries the scalar counterpart. Cross-world motion continuity
round-trips their values through `TransferState`. A same-world authority
checkpoint additionally carries their seeded latches, the arbitrary-up
frame/reseat/turn fractions, and complete hold/grapple state through
`IntegrationResidue`/`WorldAuthorityCheckpointCodec` (`SupportedVersion`,
bumped whenever the fail-closed wire shape changes).

A disconnected seat or peer does not drop its body on the spot — it PARKS
(`Entry.Parked`/`ParkedUntilTick`) for `bodies.reconnectGraceSeconds` (converted to ticks at compile),
retained pose/state and all, before `ReclaimExpiredParks` tears it down; a
matching re-Join resumes the retained body instead of minting a fresh one.
The park defers the BODY only: a disconnecting peer generation's grant rows are
released at the `PeerDisconnected` event itself (and a checkpoint restore
releases a restored park's at `RestoreCheckpoint`); a verified-identity
reconnect that resumes the parked body re-mints its admission templates
through the ordinary `PeerAdmitted` event.
See [references/session-lifecycle.md](../../.claude/skills/puck-world/references/session-lifecycle.md)
for the full contract.

## Network transport (`WorldTcpHost.cs`, `WorldTcpWireFormat.cs`)

`WorldTcpHost` is the P7 socket door: a TCP listener bound from `host.listen`
(a document field the composition root also lets `--listen` reflect for one
run). Per connection, TWO doors run off the tick thread before any body is
admitted — neither touches server state beyond a read-only document snapshot:
door 1 is the raw protocol-version handshake (`WorldProtocol.WireProtocolKey`
via `WorldHelloDoor.TryAccept`, `Puck.World.Protocol`); door 2, once door 1
passes, is the IDENTITY challenge-response
(`Puck.World.Protocol.WorldAdmissionDoor`) — the host mints a fresh
nonce, the peer answers with a signed `Puck.Attestation` claim (and, for a
vouching root, its two-hop chain), and the door verifies it against the world
document's own authored `admission` section, mapping the verified identity to
that entry's own authored grant templates. Each door refuses by its OWN named
spelling (`version-mismatch: …` vs `identity-refused: …`) — the two are never
conflated. Only once BOTH doors pass does population admission run
(`WorldServer.TryAdmitPeerConnection`, refused by name when the 128-body table
is full or the document's `networkPlayers` admission cap is already met); every
subsequent frame
(decoded through the SAME `WorldFrameCodec`/`WorldSubmissionCodec` leaves the
loopback and tape use), and disconnect
(`WorldServer.DisconnectPeerConnection`) are marshaled onto the tick thread —
`WorldServer`/`WorldPopulation`/`WorldGrants` carry no lock, so nothing may
touch them from a connection's background reader directly. The LOOPBACK path
(`WorldServer.ApplySession`'s `SessionRequest.Join` case, driven by
`LoopbackTransport`) crosses door 1 only, by construction — see that method's
own remarks on why the process boundary is the trust boundary there and no
identity check applies.
`WorldTcpHost.DrainPending`, called from `WorldServerStepShell.Step` before
`WorldServer.Step`, is where that hand-off actually applies: one global FIFO
for v1, no per-connection quotas or bounded-queue backpressure. A decoded
payload's own embedded principal (Command/Session/Mutation each carry one,
read directly by their handlers) is re-stamped with the connection's admitted
`Peer` identity before it becomes an envelope — a handler reads the identity
the door resolved, never the one the client's bytes claimed.

v1 is strictly request-then-response per connection, so no correlation id
travels on the wire; the downstream reply is a small NEW grammar
(`WorldTcpWireFormat`) carrying exactly the Completion lane
(`WorldSubmissionResult`, i.e. Ack/Session/Query) — never a streamed
snapshot/definition/composition/lever (`WorldOutputHub`'s encoded lane stays
a scaffold beyond this one lane). `--connect` does not speak this door as a
client at all: `Puck.World.Program` enqueues a federation transfer
(`WorldInstanceHost.EnqueueTransfer` with `TransferDestination.Remote`),
which authenticates the resulting `WorldRemoteAuthority` purely over
`Puck.Networking.IAuthenticator` (`WorldAttestedAuthenticator`, a signed claim
over the challenge — never a shared secret) — the interactive attestation
identity door above is server-side only today; no production client crosses it.
`Puck.World.Console`'s `WorldNetworkCommandModule`'s `world.peers` echoes the
connection table this class owns — each connection's verified admission
identity (domain/subject) — plus an `arrivals:` group naming every body
admitted by transfer and the authority its verdict was decided against;
`Puck.World`'s `WorldMutationCommandModule`'s
`world.admission` echoes the document's own authored `admission` entries —
the runtime and document halves of the admission decision, respectively.
`world.links`, in the same module, is the seam-liveness read-back: one line per
authored `adjacencies` row naming its destination, neighbour authority, the
tick-derived staleness/grace the `$link:` rule channel and the
`linkEstablished`/`linkDropped` event family both read, and — clearly marked
presentation-only, never a simulation input — the transport lane's wall-clock
backoff state.

Each connection's whole lifetime runs under `WorldNarrationScope.Current` set
to this row's `AuthorityIdentity` (an `AsyncLocal<string?>`, flows across every
await): a host running several rows uses it to tag the narration a connection
writes to `Console.Out`/`Console.Error` by which row wrote it, without
threading a row identity through every write site. Unset (and unread) on the
desktop.

### One admission entry, every ingress

`WorldServer.TryAdmitVerifiedParticipant` is the only path from an ingress to a
population body plus grant rows. It takes a `WorldAdmissionVerdict` and nothing
else — no arm accepts raw `WorldGrant` rows — and only
`WorldAdmissionDoor` produces one: from a verified attestation claim
(`TryAdmit`), from an already-verified identity re-matched against a candidate
document (`TryMatchEntry`, the whole-document rebuild's re-authorization), or
from an authenticated federation authority's namespace (`TryAdmitArrival`).
A caller with no verdict is refused by name rather than admitted on a default
seed. `WorldServer.BuildAdmissionGrants` fills in the two fields a template
cannot carry — the `Peer` principal, and a `body:<n>` subject for a template
that authored none (`WorldAdmissionGrant.SubjectFor`) — and passes every other
field through, so an authored template states exactly what the peer holds.

A federated or colocated transfer crosses the same door. `WorldTransferEscrow`
runs `TryAdmitArrival` once at reserve against `request.SourceAuthority` (the
namespace `Puck.Networking.IAuthenticator`'s signed-claim handshake derived from the
verified proof — never a label the connection merely claimed — or the
in-process host's own for a colocated authority), carries the verdict on
the lease, and commits it through `WorldServer.AdmitTransferredPeer`. Reserve
and commit therefore cannot disagree: the reservation's per-slot authorization
asks the verdict's templates whether they confer `Drive` over the body it is
about to bind, which is the question the mint answers again. An arrival's
identity columns name the authenticated authority, never the traveller's
carried profile — `world.peers`'s `arrivals:` group echoes them.

An `admission` row in `federatedAuthority` mode carries no key: its `domain` is
the authenticated authority namespace, or `*` for any authority that completes
the handshake. That namespace is `WorldAttestedAuthenticator`'s own verified
claim subject — `host.authority` when the document authors one, else the
boot instance identity (`Puck.World.WorldDefinitionLoader.BootInstanceName`)
— never a label the connecting peer merely asserted, so `*` is what a
document authors when it cannot know its neighbours' identities in advance.
Such a row is skipped
when the door builds its attestation trust list — it can never verify a claim —
and a document authoring arrivals alone still admits no connecting peer.
## Federation transport (`WorldFederationCodec.cs`)

The same listener routes a second dialect off the first eight bytes:
`WorldFederationCodec.WireKey` opens an authority-to-authority connection
instead of a player connection. That connection is a persistent authenticated
lane — challenge/proof once (`Puck.Networking.IAuthenticator`), then framed requests
in order, request-then-response, until `Observe` or `IntentStream` takes it
over and streams on it. The frame grammar, the bounded reader/writer, and the
refusal vocabulary are the shared ones in
`Puck.Networking/WireCodec.cs`, so this codec is not a second
wire dialect: every leaf is Try-shaped and bounded before it allocates, and
every refusal frame's text opens with a `WorldFederationRefusal` name.
`WorldTcpHost.FederationRefusals` counts those names, so a refusal is read back
by name rather than by sentence.

Two ingress disciplines meet in this class, and which one applies is decided by
what the frame is:

- An ordinary admitted peer's admission, submissions, and disconnect marshal
  onto the tick thread (`RunOnTickThreadAsync` → `DrainPending`).
- An authenticated AUTHORITY operation — reserve, commit, abort, acknowledge,
  status, route, forwarded submission, published intent — runs on its socket
  worker inside `WorldServer.ExecuteAuthorityOperation`, which serializes it
  against `Step` under the server's authority gate. It must NOT wait for this
  host's next tick: two hosts crossing into one another at the same time would
  deadlock on each other's tick.

Whatever that gate protects is acquired and released under it.
`WorldOutputHub`'s subscriber list carries no lock of its own, so
`StreamProjectionAsync` disposes its projection lease inside
`ExecuteAuthorityOperation` exactly as it attached. Any check-then-act over
population state — is this transferred principal still live, then submit or
describe on its behalf — is ONE gated operation, never two.

The client half is `WorldRemoteAuthority` (`WorldRemoteAuthority.cs`), hosted in
this project though its type still carries the `Puck.World` namespace pending a
one-time normalization pass: an intent pump plus one
request lane per (source authority namespace, `WorldFederationLane` concern), so
connect, hello, and challenge are paid once per lane rather than once per
operation. A lane is strictly ordered, so transfer transactions and routed
traffic are kept on separate lanes rather than queueing behind each other. Only
a failure to connect takes a lane out of service; a break on an established
connection reconnects without entering backoff and re-sends only when
`ILaneProtocol.MayResend` says the kind is safe to send twice (`Submission`
never; the transfer-id-keyed kinds are idempotent at the host), otherwise the
request is answered `ConnectionClosed` and left in doubt. Each attempt runs
under a per-request deadline (`LaneRequestTimeout`): a peer that goes silent
after the request was written is answered `RequestTimedOut` with no re-send and
no backoff, and an unexpected exception from the dialect answers that one
request `LaneUnavailable` without killing the worker. A lane inside its backoff
window answers `LaneUnavailable` without touching a socket, which is what keeps
a closed edge from stalling the source's tick. A run that holds no federation
signing identity (no `--federation-key-file`) never opens a lane, an observer
session, or an intent stream at all: every request is answered
`LaneUnavailable` naming that, with one stderr line per authority, since no
connect could ever authenticate. An authenticator that verifies but cannot
prove (admission trust entries, no signing oracle) passes `IsConfigured`, so
the first proof it refuses is what reveals it; from then on the same gate
closes on it with the same answer.

Every document this codec writes goes out at the connection's disclosure tier.
`DisclosureFor` resolves it once per federation connection, through the same
`WorldAdmissionDoor.TryAdmitArrival` arm that decides what an arriving traveler
is minted; a namespace no `admission` row names gets `presentation`.
`EncodeDocument` writes `[tier byte][document bytes]` — a projection below
replica, the definition verbatim at replica — and `TryDecodeDocument` hydrates
the projection back into a `WorldDefinition` so the route answer, the
reservation reply, and the observation lane's `Definition` frame all keep their
existing shapes. Both arms hand back a document whose `state.<row>[.<key>]`
values are resolved, so a delivered definition is indistinguishable from a
file-loaded one and an arriving seat's binding recompose cannot fault on an
unresolved identifier; a projection leaf that still names a state cell is
refused as `PayloadMalformed`. The reservation leaf carries a
`WorldIdentityProjection` instead of the traveler's owned document.

`StreamProjectionAsync` attaches its sink with the world's authored
`bodies.disclosure` and no observer body index, so a narrowed policy
delivers a remote observer nothing until one of its travelers lands here.

A remote-admitted body is tagged `WorldPopulation.Entry.IsRemoteHuman`
(`IsAdmittedPeer` reads it) so `world.population`'s census lever can never
silently reassign or deactivate a connected human's body — see "The entity
table" above.

## Principals and grants (`WorldGrants.cs`)

Every write submission carries its acting `WorldPrincipal` — a seat, the
console, a named addon guest, or a generation-bearing `Peer(index,
generation)` — and one server-side table,
`WorldGrants`, is the single place a write is authorized. A grant row is
`(principal, capability, subject)` plus optional exclusivity, an untrusted
principal's per-tick dispatch budget, and the co-driving reach/consent pair.
Capabilities are `Drive`, `Observe`, `Control`, `Mutate`, and `Edit`
(`Present` was deleted 2026-08-02 — "contribute to what is drawn" is
`Mutate` over presentation-shaped sections); subjects are the `all`
wildcard, `body:<n>`, `screen:<n>`, `section:<name>`,
`state:<name>`, `composition` (the shared window-composition authority),
`creation:<id>`/`placement:<id>` (one creations/placements row apiece,
`Mutate`-only), or the two world-events-feed subjects
`region:<name>`/`seat:<n>` (legitimate
only for `Observe`), with a positive per-capability legitimacy rule
(`WorldGrants.IsLegitimateSubject`) so a new subject shape is refused by
default. `state:<name>` is the one subject that
narrows BOTH mutation kind pairs over one named row — the whole-row
`UpsertStateRow`/`RemoveStateRow` AND the per-cell `UpsertStateCell`/
`RemoveStateCell` (a slot is a table with one key, so there is one row and
one subject, never a separate `table:<name>`) — beneath its
own section-level `Mutate` hold — `Edit` over the concrete row, checked a
SECOND time at apply — rather than replacing it.

**Two mask payloads, two types, never one lane with two readings.** A grant
row may carry a `MutationKindMask` (`WorldGrant.KindMask`, ordinals from
`WorldMutationKindCatalog`) on a `Mutate` row over `section:<name>`,
`creation:<id>`, or `placement:<id>` — the dispatch door — or on an
`Edit`/`state:<name>` row, where it
separates the per-cell writes from the whole-row re-authoring beneath one
subject (`verbs:UpsertStateCell,RemoveStateCell` grants "bump the score"
without "redefine the score"). It may instead carry a `DocumentWriteMask`
(`WorldGrant.WriteMask`, `WorldDocumentWriteKind`'s `Set`/`Add`) on a
`Mutate`/`state:<name>` row — the cross-document durable-state write-back
channel `WorldOwnedWorlds.Decide` gates. `WorldGrants.CarriesKindMask` /
`CarriesWriteMask` state which row shape carries which, positively and in
one place; a mask offered on any other shape is refused by name. The two are
distinct C# types because they were one `ulong` once, read under whichever
vocabulary the row's subject kind implied — bit 0 meaning `UpsertKit` on a
section row and `Set` on a state row. An ABSENT kind mask means FULL reach
(opt-in narrowing beneath an already deny-by-default capability); an ABSENT
write mask admits nothing (that channel's mask is what admits a foreign
write at all). Both echo BY NAME through `world.grants` and `world.why`, in
the same `verbs:`/`writes:` spelling that authors them, and a mask denial
names the verb it denied.

Local play seeds permissively at boot (seats and the console hold wide
grants; addon guests hold nothing until granted), and a world document can
additionally ship grant rows in its `grants` section, applied at boot through
the same path the live `world.grant` verb uses. Every enforcement point asks
the table before acting — the intent drain, command application, mutation
application, whole-document swaps and undo, engagement, profile edits, and
addon dispatch — and a denial is loud and data-shaped (a named
`[world.grant denied: …]` line; the write drops). The read-back verbs are
`world.grants`, `world.why`, and `body.channels`.

Peer authority is never pre-seeded by index. Each admission or census
reactivation bumps the slot's generation, scrubs stale-generation grants and
engagement routes through the revoke door, then mints the new generation's
default Control grant through the grant door. Admission and disconnect are
tape-covered server events, so offline replay uses those same doors.

For untrusted principals, authority travels as handles rather than names:
`WorldHandleTable.cs` projects a principal's grant rows into per-instance
slots (never a whole-domain designation), stamped with the minting principal
and capability, and generation-checked so a revoked or re-sorted handle
refuses on its next use with a distinct verdict. The campaign that designed
this model was retired on 2026-08-10, its rulings moved into the code they
govern; what survives as WORK is carried in
[`docs/campaign.md`](../../docs/campaign.md). This README is the reader-facing
summary; the CODE outranks it on any point of disagreement.

Two settled rulings worth restating here because their absence is invisible:
ownership latching is unified through this table (control applications'
occupancy included — do not invent a parallel ownership mechanism), and the
authority decision is deliberately not modeled as a lattice or quotient
(see the state document's "What is NOT algebra" entry).

## Screen machines (`WorldMachineHost.cs`)

Owner ruling, 2026-08-03: a booted `IScreenMachine` (a diegetic screen's
cartridge/cabinet — `Puck.Abstractions.Machines`) is CORE state, not
presentation-fed. `WorldMachineHost` — a peer DI singleton `WorldServer`
takes as a constructor parameter, never a private field it builds, so the
container disposes the machines it holds — owns boot, per-tick stepping,
cable-linking, live reconfiguration, and memory-peek for every declared
screen's machine, in EVERY boot shape including headless. Stepping runs
inside `WorldServer.Step`, immediately after `WorldEngagement.FoldTick`, fed
that tick's per-screen pads directly (`WorldEngagement.BuildPadSnapshot()`,
in-process — no client/wire round-trip). `screen.insert`/`.eject`/`.select`/
`.options`/`.link`/`.unlink` (`Puck.World.ScreenCommandModule`) submit a
`WorldScreenOp` (`Puck.World.Protocol`) through the ordered submission domain
(`IServerLink.SubmitScreenOp`), applied SYNCHRONOUSLY like `Command`/`Grant`/
`Revoke` and checked against the ordinary grant table (`Control` over
`screen:<n>`) before `WorldMachineHost` is touched; `Insert` and a
Machine-magazine `Select` share one boot path (`TryBootMachine`) and are BOTH
CAS-pinned (`sha256-64` of the exact bytes read, or the `"absent"` sentinel
when the file could not be read at all) — a failed boot is reported as a
failure, never a disguised success, and the pinned signature rides the tape
REGARDLESS of whether the op succeeded (INCLUDING an unresolved engine —
content is read/signed before engine resolution is even attempted, never
left unpinned on that path), so a replay re-drive refuses by name if the
file's on-disk state no longer matches what was recorded. Declared cable
links (`WorldMachineHost.ReconcileLinks`) are established/torn down at
construction (for a link declared in the boot document itself) AND on every
`WorldServer.Install` (every live mutation and every whole-document
rebuild) — never only once; the reconcile itself is two-phase and atomic
per call (every stale-or-changed declared link tears down FIRST, complete,
before anything (re-)establishes), so a re-shape that moves a screen from
one declared link to another within the SAME reconcile always succeeds
rather than silently failing while the old link still owns the screen.
Every op rides the replay tape (`WorldReplayEntry.ScreenOp`), and
`replay.record`'s arm gate refuses on THREE latches, none sufficient alone:
`WorldServer.AnyAddonEverPumped`, `AnyMachineEverPumped` (once any machine
has stepped), and `AnyScreenOpEverApplied` (once any screen op has applied
AT ALL, independent of stepping — screen ops apply synchronously, between
fixed steps, so an insert/eject/select/options/link/unlink can change live
host state before a single tick has run, which the other two latches would
miss) — offline replay reconstructs a FRESH `WorldMachineHost` from the
tape's embedded definition, so a machine's accumulated core state (or a
screen op's effect) from before recording began can never be re-established,
and the pose hash covers no machine state to catch the divergence.
`Puck.World.WorldScreenBinder` is a
pure reader of this type's outputs for presentation (framebuffer
handle/light, `PublishFrame`) and still owns the genuinely presentation
screen sources (test pattern, authored QR, webcam, compositor capture,
jumbotron view) that are not this type's concern. The list above is the
current set.

## The addon host seam (`IWorldAddonHost.cs`, `WorldAddonReceipt.cs`)

`IWorldAddonHost` is every member this project calls on the mounted addon
guest host — the three tick-boundary pump points above, the
`TryPrepare`/`Commit`/`Finish` prepare/commit/publish transaction
`TryApplyMutation` (the `UpsertAddon`/`RemoveAddon` mutation's own last
fallible gate, refusing by name first when no host is attached at all),
`ApplyRebuild` (unconditional, for `world.reset`/`.load`/`.reload`),
`WorldAddonRuntime.TryCreate` (boot), and `ApplyUndo` each call, mutation
completion, and the undeclared-granted-channel disclosure. `Commit` is pure
reference adoption; `Finish` — narration and superseded-guest disposal —
runs only after the caller's own document/journal publication is durable,
so neither can unwind it. The opaque plan
crossing `TryPrepare`/`Commit` implements `IWorldAddonPreparedPlan`
(`IWorldAddonPreparedPlan.cs`), a bare `IDisposable` marker (plus a
`MountedCount` this project pre-sizes its per-tick addon contention
tracking against) this project declares so it never names the concrete
plan shape either.
`WorldServer` holds the host as `m_addons` and never names the concrete host
type; `WorldReplaySnapshot.Drive` takes an `addonHostFactory` delegate so an
offline re-drive can mount its own fresh guest set. `WorldAddonReceipt`
(one mounted guest's recorded-at-mount name/hash/fuel) stays here rather
than in `Puck.World.Addons` because this project owns the replay tape that
persists it. The concrete host — `WorldAddonRuntime`, the mount sequence,
the WASM guest ABI decode, the addon.mutate refusal catalog — is
[`Puck.World.Addons`](../Puck.World.Addons/README.md).

## Owned worlds and storage

`WorldOwnedWorlds` loads one `puck.world.def.v1` file per identity from
`owned-worlds` beneath the state root, plus any hand-placed basis chain link
under its `owned-worlds/basis/` subdirectory (outside the catalog's own
directory glob, so a link never enumerates as a second owned world). A document
whose BYTES are not a `puck.world.def.v1` document is DISCARDED, not tolerated:
the file moves once into `owned-worlds/unloadable/` (also outside the glob, so
it never enumerates again). Nothing distinguishes a retired document shape from
a corrupt file here, so neither is silently eaten and neither is migrated. A
refusal that can answer differently on the next boot — unreadable file, absent
file, unresolved `basis` link, or a validation claim resting on an adjacency
neighbour — is NOT discarded: those files stay where they are and are only
named, because the neighbour resolver reads the same directory a sweep would
empty. Each half reports as one stderr line grouping file names by their shared
reason, with the path stripped out of the reason. A quarantine destination that
is already taken takes an ordinal suffix rather than overwriting the earlier
copy, and the seeding pass that fills an emptied catalog from
`seatDefaults.identities` skips any id whose catalog path is occupied by a
file or directory, so a document left behind keeps its bytes and a stray
directory cannot crash startup, and `identity.create` refuses an id whose catalog
path is occupied for the same reason. `WorldOwnedWorlds.Discarded` and
`identity.list`'s `discarded=` column are the read-back for the disposals;
`WorldOwnedWorlds.Refused` and `identity.list`'s `refused=` column are the
read-back for the documents left in place. The
machine-local installation id stays separate in `machine.id`; controller
recognition is stored through named text state rows in the owned world.
`--user-id` and `--state-dir` still resolve who is playing and where those
worlds live. `WorldOwnedWorldSync` pushes and pulls those documents against the
per-user cloud container — one blob per world tip under `puck/worlds/`, ETag-guarded,
refuse-and-surface — when the composition root wires an endpoint and a resolved
identity. A world naming a basis pushes and pulls its WHOLE chain, not just its
flattened tip: each chain link lives under its own `puck/worlds/basis/{name}`
key, and a pull composing a chain-derived document writes each link to the
local `basis/` subdirectory (never a flattened file) so the next save keeps
writing a delta. Cloud version tokens persist in `owned-worlds/sync-state.json`
(tips and basis links tracked separately), and the `storage.push`/
`storage.pull`/`storage.status`/`storage.credential` verbs in `Puck.World`
drive and echo it.
`IObjectBlobStore` also exposes `ListAsync(target, objectId, keyPrefix)` (the
object-relative keys beneath a key path, matched by whole path segment — the same
key space a read or write address carries, whichever route served the list); a
whole-catalog `storage.pull` uses it to list the cloud `puck/worlds/` namespace
and DISCOVER worlds the catalog has never seen.

The platform edge (`AzureBlobObjectStorageTarget.EdgeNamespace`) cannot serve a
container list AT ALL — its path rewrite has no segment for a query-string-only
List Blobs request to occupy, so it 404s unconditionally before reaching blob
storage (verified live 2026-08-05). An edge-shaped endpoint therefore never
sends `ListAsync` through the edge: it routes to
`AzureBlobObjectStorageTarget.DirectEndpoint` — the world doc's
`storage.discoveryEndpoint` / its `--storage-discovery-uri` CLI reflection —
or `WorldOwnedWorldSync.DiscoverCloudIds` refuses whole-catalog discovery BY
NAME, before any network call, when no discovery endpoint is authored. A
genuine 404 through the direct connection (the edge-shaped container is
platform-managed and never legitimately absent) propagates as a named refusal
too, rather than reading as an empty prefix — only the raw/dev-emulator shape
(`EdgeNamespace` null, self-managed containers) swallows a 404 as "nothing
written yet."

Going direct means addressing a DIFFERENT layout of the same blob, and that is
the part easy to get wrong: the edge rewrite maps `/{namespace}/{container}/{rest}`
onto container `{container}`, blob `{namespace}/{rest}`, so what the edge route
addresses as container `{namespace}`, blob `{objectId}/{key}` is *stored* as
container `{objectId}`, blob `{namespace}/{key}`. The direct list therefore
enumerates the object's own container beneath a `{namespace}/` prefix — which is
also the only shape the per-user access policy grants — and strips that prefix
back off, so both routes hand the caller the same object-relative keys.
Enumerating the edge's view instead (a container named for the namespace) asks
for something no account layout has, and an emulator that has been laid out to
match the edge's view will pass while production 404s.

`WorldOwnedWorldFileName` (in `Puck.World.Schema`, because the earliest door that
has to enforce it is document validation) is the id↔file/blob-name mapping. It
escapes nothing: it takes a `WorldSafeName`, whose fixed reserved-character set
(rather than `Path.GetInvalidFileNameChars()`) is what makes two machines on
different operating systems agree on the name an id maps to. That makes the
mapping injective into file-name STRINGS, which is not the same as into storage
LOCATIONS — the local catalog directory resolves names case-insensitively, while
the cloud object namespace is case-sensitive — so one id names one location only
under a **case-insensitive** uniqueness rule, held at every door: the document's
authored `seatDefaults.identities` seeds (refused by
`WorldDefinitionValidator`, so a case-variant pair never reaches disk),
`identity.create`, and adoption from a pull. The directory load holds the same
rule from the other side: a file whose name is not the one its declared id maps
to — ignoring case, because the filesystem's own resolution ignores it — is
refused and left where it is, so a case-only rename of a catalog file is
admitted rather than wedging the catalog. A pull additionally refuses a cloud document whose own
`identity.id` is not the id whose key was read, since adopting it would file the
document under one name and its version token under another; a listed cloud name
the mapping could never emit belongs to no reachable id and refuses by name in
the pull's outcome list rather than being silently dropped.

`storage.status`'s `lastWrite` reports the last push's actual outcome — `ok`,
`precondition-failed`, or `failed` — not the precondition bit alone.

The identity half is `Puck.World`'s `IPlayerStorageIdentityResolver`
(`WorldStorageIdentity.cs`) — an authored `storage.userId` / `--user-id`
override, or the local-only decline. There is no app registration and no
interactive sign-in: game clients ARE users, so a player's machine authenticates
ambiently and a hosted server runs as a user-assigned managed identity, both
through the one `DefaultAzureCredential` the blob backend already uses.
`storage.credential` probes whether that ambient credential can issue a storage
token from this machine and records the verdict for `storage.status`. Parsing a
STORAGE access token for identity remains ruled out — it says what a credential
is scoped to, never who is playing.

## Hosted worlds and the authority store

A hosted world's blobs live in a namespace sibling to, and never overlapping
with, the owned-worlds catalog above: `puck/hosted/{world}/…` for its
checkpoint/journal (never published), `private/puck/hosted/{world}/definition.json`
and `.../projection.json` for the pair the platform's public content edge
serves anonymously. One key writer, `WorldOwnedWorldSync.HostedAddressFor`,
computes both roots so a reader can never drift from it.

`IWorldAuthorityStore` (`WorldAuthorityBlobStore` over `IObjectBlobStore`) is
programmed against opaque encoded bytes throughout — `LoadLatestAsync` returns
the checkpoint blob's raw, hash-verified bytes plus its ordinal and tick, never
a decoded record; `WorldAuthorityCheckpointCodec` decodes what this store
hands back. A checkpoint write is content-addressed and
create-only (an identical retry is idempotent, verified by byte comparison on
a create-only loss), then the `checkpoints/latest` pointer moves under its own
if-match compare-and-swap; a journal page is a read-modify-write append under
the same discipline, relative to whichever checkpoint ordinal `checkpoints/latest`
currently names. `WorldAuthorityCheckpointCadenceCounter` counts master-step
engine ticks toward `WorldAuthorityCheckpointCadence.EngineTicks` and arms a
capture request a caller honours at its own next boundary; it never decides
whether a capture may proceed and never takes a row's own gate itself.

`WorldHostedOrigin` (a `WorldDocumentOrigin` arm beside `WorldFileOrigin`)
loads a hosted definition through `WorldDefinitionLoader`'s bytes entry — a
hosted definition is always stored already composed, so this load never
resolves a basis chain — and resolves its own `references[]` through
`WorldStorageNeighbourResolver`'s hosted-namespace arm
(`WorldStorageNamespace.Hosted`), the same resolver the owned-worlds catalog
uses with its default namespace.

## Deterministic replay (`WorldReplayTape.cs`, `WorldReplayTape.Drive.cs`, `WorldReplaySnapshot.cs`)

`replay.drive <name> [to <tick>]` re-drives a saved tape into the running
session: a forced `world.load` of the embedded definition plus the boot
population image (`WorldPopulation.Restore` from a shadow server the recorded
seats joined) reset the live world, `WorldServerStepShell` feeds one recorded
tick through `WorldReplaySnapshot.ApplyRecordedTick` ahead of each live step
(the same apply the offline drive uses), `LoopbackTransport.InputMasked`
drops local seat intents and commands for the drive's span, and the first
live-vs-recorded hash divergence is narrated on stderr without stopping.
`replay.fork <name> <tick> <new>` fast-forwards the same drive to `<tick>`
(a burst of recorded ticks per shell call) and hands over to a recording
whose leading tick groups are the parent's, with `ForkedFrom` in the header;
the child is standalone. `replay.record <name>` captures the running session's record-start definition,
active seats, mounted-guest receipts, and the per-tick server-input stream,
while sampling both the LIVE population's pose hash and complete authoritative
state hash; `replay.stop`
persists `<name>.puckreplay` and re-drives it once; `replay.verify <name>`
rehydrates a fresh boot-image world, re-drives the stream offline, and
reports MATCH or MISMATCH naming the first divergent tick (tick 0 indicts the
starting state; any later tick is a real trajectory divergence). A receipt
disagreement — the live tree moved past the recording — refuses loudly with
no verdict; a recorded mutation's accept/refuse outcome disagreeing with what
the replay's own apply pipeline produces refuses loudly by name too
(`MutationOutcomeMismatch` — see [addons.md](../../.claude/skills/puck-world/references/addons.md)'s prepare/commit
transaction); a codec defect (`WorldReplayCodecException.cs`) reports as a
host bug, never folded into either refusal. `replay.inspect <name>
[<from>-<to>] [--all] [--poses]` (`WorldReplayInspector.cs`,
`WorldReplayEntryDescriber.cs`) is the tape's read-back: the header facts,
then one line per tick carrying the recorded hash beside what changed that
tick (authority entries, intent channel edges); `--poses` re-drives through
the same `Drive` and prints each active body's pose per line, naming the
first pose-divergent tick. The MATCH/MISMATCH verdict uses the authoritative
trace; the pose trace remains the human-readable trajectory diagnostic.
Presentation (screen pixels,
cameras, overlays, audio) is excluded by design: a match proves the
authoritative simulation state, not the HUD. Known scope limit — the tape
captures every one of the twelve envelope payload kinds except `Lever`
(command, grant, revoke, session, designation, rebuild, mutation, undo,
composition, query, and screen-op) plus intents and the two
peer-lifecycle server events; a mid-session capture honestly reports
MISMATCH at tick 0 — carried in
[`docs/campaign.md`](../../docs/campaign.md).

## Verifying a change here

No build gate covers this project's behavior; verify by RUNNING `Puck.World`
over stdin. The apply pipeline's all-or-nothing contract (a mutation that
fails whole-document validation leaves the live definition byte-identical) —
the same gate `WorldServer.ApplyUndo`'s journal-replay loop passes each kept
entry through — is proven in-process by
`tests/Puck.World.Tests/MutationAllOrNothingLawTests.cs`; that suite does not
construct a genuine mid-replay validation failure, so the replay loop's own
early-return is unproven beyond code inspection.

No committed battery covers the ordered-domain envelope's ordering
contract. Verify it live instead: one stdin batch interleaving a grant and
the command that needs it, plus the reversed order as the discriminating
control.

Principal/grant enforcement (denial/control pairs per player-facing verb) is
proved by `AuthorityAdministrationLawTests`, `EngageAuthorityLawTests`, and
`ControlApplicationLawTests` in
`tests/Puck.World.Tests`.

A change that moves simulation math is expected to change replay hashes;
re-record any persisted tape it invalidates in the same change (`CLAUDE.md`
rule 4).

Adjacency/federation changes additionally run
`puck canary four-corners-sharded`. It starts five distinct authorities
(four ground worlds plus the floating island) and exercises generation-
addressed forwarding through a full four-ground-authority human circuit.
The automatic smaller proof is `puck canary seamless-adjacency`.

Verify a network-transport change by running two `Puck.World` processes: a
headless host (`--headless --listen <ip:port> --state-dir <tmp>`) and a
`--connect <ip:port>` client, both scripted over stdin — `world.peers`/
`world.grants peer:<index>:<generation>` on the host prove admission and the
disconnect-driven revoke; the client's own query replies prove the Completion
lane round-trips. No persisted battery exists for this yet (a live owner
conversation about runner disposition); do not add one without asking.
