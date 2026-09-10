# Groups and cooperative matchmaking

This is the implementation design for Puck's group finder, requested on 2026-09-09.
It proposes work; it does not claim that the experience exists. The
[campaign](campaign.md) owns sequencing and verification status, and the
[vision](vision.md) owns the world model. The source observations below were made
against `31eab09cbd0b540539a038a5025b86a3f01a23bc`.

## The experience we are building

Friends can form a party, invite people, find compatible strangers, enter one
dungeon, recover from disconnects, replace a departing member and play again
together. A player can choose automatic matching or browse a group's listing.
Both routes use the same membership, consent and admission operations.

Cooperative activities ship first. Activity size, composition, eligibility,
progress and completion are authored data, so racing, tabletop and competitive
activities can later use the same foundation. Competitive ratings and team
balancing are not prerequisites for the cooperative release.

The finder is reachable inside the running island through its ordinary UI and
console. Its operations are also available through the existing authenticated
MCP control surface. A queue does not require leaving the current world. There
is no separate launcher mode, account system, onboarding service or game server
stack for this feature.

The first complete acceptance journey is two friends and compatible strangers
forming a run group, accepting their actual roles and activity, entering the
same destination from different source authorities, reconnecting, backfilling,
finishing and choosing to stay together. Repeat it with a coordinator restart
and with a lost destination commit response. A local roster demo alone does not
satisfy this plan.

## What the code gives us, and what it does not

These are source observations that explain the decisions below, not a capability
register. Recheck the cited code when implementing each slice.

| Existing home | Consequence for this work |
| --- | --- |
| [WorldGroups.cs](../src/Puck.World.Schema/WorldGroups.cs) | Group kinds, membership, ownership, eviction and lifetime already have a home. Members are local principals; roles are declared on kinds but not assigned to members. `Ephemeral` dissolves an emptied runtime group. It does not specify restart recovery. |
| [WorldServer.MutationCompose.cs](../src/Puck.World.Server/WorldServer.MutationCompose.cs) and [WorldMutation.cs](../src/Puck.World.Protocol/Protocol/WorldMutation.cs) | `FormGroup` creates an empty group and `JoinGroup` adds one member. `WorldMutation.Batch` already composes several mutations against one candidate, validates once, journals once, and refuses the whole batch if any member would be refused or if `ExpectedDefinition` no longer matches. A fully accepted run roster is a `Batch` of `FormGroup` plus its `JoinGroup`s under an expected revision, not a new operation. |
| [WorldGrants.cs](../src/Puck.World.Server/WorldGrants.cs) | Group expansion derives membership from local principals. Kind reach combines capabilities across roles. Per-member leadership cannot be implemented by merely adding a `leader` string to the document. |
| [WorldSessionResolver.cs](../src/Puck.World.Schema/WorldSessionResolver.cs) | Local group-scoped resolution exists. Its local group ids and process-local generation cache are not a federated membership proof or a shared destination directory. |
| [WorldTransferEscrow.cs](../src/Puck.World.Server/WorldTransferEscrow.cs), [WorldInstanceHost.Transfers.cs](../src/Puck.World.Server/WorldInstanceHost.Transfers.cs) and [transfer recovery](../src/Puck.World.Server/WorldInstanceHost.TransferRecovery.cs) | Reserve, commit, status and recovery already exist. A transfer request names one source authority. Coordinating a complete roster arriving from several sources is additional work. |
| [WorldSiloHost.cs](../src/Puck.World.Silo/WorldSiloHost.cs) and [WorldAuthorityBlobStore.cs](../src/Puck.World.Server/WorldAuthorityBlobStore.cs) | Checkpoints and a serialized journal append chain exist. Persistence runs after mutation application; a drained request is not a durable success. The current journal append reads and rewrites its page. |
| [WorldSubmissionResult.cs](../src/Puck.World.Protocol/Protocol/WorldSubmissionResult.cs) | `Ack` reports submission drainage. Callers need a typed applied/refused result and a separately identified durable result for this work. |
| [WorldExternalOperationJournal.cs](../src/Puck.World.Server/WorldExternalOperationJournal.cs) and [extension hosting](../src/Puck.World.Server/ExtensionHosting.md) | Durable external requests, request-key deduplication, causal recovery and reconciliation already exist. Reuse them for external coordination; do not build another outbox. |
| [IObjectBlobStore.cs](../src/Puck.Storage/IObjectBlobStore.cs) | Reads, conditional writes and listing exist. This interface has no deletion operation: physical expiry cleanup needs a bounded storage operation or the established provider lifecycle path, not an imaginary existing API. |

The semantic `WorldGroup` reference walk through `Puck.World.Console` also reaches
serialization, the resolver, the mutation composer and the grant indexes. That
project closure is useful evidence of coupled changes; it is not a complete
solution-wide deletion or rename proof.

## Authority, identity and leadership

### A group is a relationship, with one authoritative home

An official coordination world holds the live group and request rows. It is an
ordinary service-owned authority hosted by the existing silo, without a rendered
scene or embodied population requirement. This is an operational world, not a
new game district. The finder is an installed service extension; the document
selects its allowed configuration through the existing trusted registry.

One authority owns a group's membership and decisions. Its hosting process can
change without changing the group's identity. Do not allocate a world, timer,
Orleans grain or database per group. The existing
[WorldGrain](../src/Puck.World.Silo/WorldGrain.cs) remains a thin activation adapter.

The dungeon's authority independently owns capacity, progress and admission.
The group's leader does not host the group and does not become the dungeon's
authority. A player-hosted game may use the same mechanisms under its own trust
policy; the first official experience uses the official coordination authority.

Start with one logical coordination authority per explicit matchmaking trust
domain. Every activity preference and manual application in that domain routes
through the same participation check. Activity indexes are not separate owners.
Domain isolation is an authorization boundary; region is normally a matching
constraint, not a reason to permit a second concurrent assignment.

### Durable members and temporary connections

A member identifies a verified player-owned world. A live connection maps that
identity to its current session principal. Reconnecting or moving to another
authority changes that binding, not membership.

Evolve the existing identity vocabulary into an issuer-qualified reference using
the verified issuer/subject and owned-world relationship. Do not treat
`WorldAuthorityIdentity(Owner, World)` as proof: it is currently a storage key.
Do not substitute a body incarnation, display name, unqualified group string or
OAuth client-specific subject for the stable participant identity.

Keep local seats/addons usable for authored local groups. Represent local members
and verified world members as an explicit union; a local member cannot silently
become a remotely admissible identity. Official online matchmaking requires a
verified identity for every participant, including distinct local co-op players.

A portable group reference includes its issuing coordination-world identity and
a never-reused local id. Mutable membership has a revision; authority takeover
has a fencing epoch. Neither is interchangeable with a dungeon generation or a
peer connection generation.

### Leadership is policy and grants

Give a manually created party's founder the leader role. For an automatically
formed run group, select the first willing member under a stable, authored
ordering. Default succession is explicit transfer, then the next eligible member
after disconnect grace. The UI can offer volunteering when nobody is willing.
A group kind may use automatic decisions or voting without a player leader.

Separate **management roles** such as leader/member from **activity roles** such
as healer. The former constrains operations; the latter fills a composition
slot. Neither ownership nor leadership means acting as another participant.

Extend group grant expansion to evaluate the member's actual role against the
current roster revision. Add a row-scoped group subject following the existing
creation/placement subject pattern where the present section-wide hold is too
broad. Keep the existing capability vocabulary. Mutating group A must not grant
access to group B or permission to edit the kind catalog.

Invitation acceptance and readiness always require the named participant's own
authenticated action. Administrative removal is a distinct, auditable action;
an administrator cannot manufacture another person's consent. Removal, role
change and reconnect revoke or rematerialize the corresponding session grants
at the authoritative boundary, including cached handles and observations.

## Data and state transitions

Extend the existing group model rather than introducing a competing party store.
Exact declaration names follow the implementation's semantic inventory; the
following records describe responsibilities, not a new package contract.

| Record | Authoritative contents |
| --- | --- |
| Activity definition | Pinned destination reference and policy revision; size; composition slots; verified eligibility predicates; permitted preference expansion; progress/backfill and completion policy. |
| Group | Qualified id, kind, revision, flat members, management roles, stable join ordering and lifecycle phase. |
| Invitation/listing | Group revision, permitted audience, inviter, intended recipient or disclosed listing, expiry and disposition. A listing derives vacancies from the live roster. |
| Queue request | Stable request key; authenticated participants; original party id and roster revision; each member's accepted preferences; eligible activities; waiting credit; revision and phase. |
| Proposal | Exact request revisions, complete roster, assigned activity roles, pinned activity policy, each member's acceptance and a deadline generation. |
| Participation claim | Participant identity, request/proposal/run owner, claim generation and phase. At most one incompatible pending or active assignment within the domain. |
| Run assignment | Run group, target-issued destination identity/generation, admission operation id, roster revision, per-source transfer references and reconciliation phase. |

An original friend party and a matched run group are separate rows with flat
members. Keep the original-party reference on the request/assignment; do not
nest groups. Players may belong to several social groups, but that does not
permit conflicting activity assignments. Changing a member's management role
need not cancel an unrelated activity; changing the participating roster or
accepted activity terms does invalidate its pending proposal.

### Form a manual group

One authenticated create operation creates the group, inserts its creator and
assigns the initial role. An invitation does not insert its recipient. Accept
checks recipient identity, expiry, group revision and capacity, then adds the
member in one transition. Concurrent accepts for the last place have one winner.
Repeated identical requests return their recorded outcome; reusing a request key
with different input refuses.

### Form a matched group

```text
Draft request → Queued → Proposed → All accepted → Admission pending → Active
                    ↑       │                         │
                    └───────┘                         └→ Reconciling
                  decline/expiry                          │
                                                     settled outcome
```

`Draft` waits for every member to consent to queuing under the proposed terms.
The finder never creates tickets for unconsenting party members. The group's
leader can propose settings; each person approves their participation.

Proposal admission atomically checks all request revisions and reserves every
participant. Final acceptance atomically consumes those requests, creates the
complete run roster and records pending admission. These changes ride one
`WorldMutation.Batch` under an expected definition fingerprint, not several
independent `FormGroup`/`JoinGroup` calls or a client-side sequence that can
partially succeed.

Capacity failure before any possible destination commit releases the proposal
and restores eligible requests. After an ambiguous external outcome, claims stay
held until reconciliation establishes what happened. A client timeout or expired
ready-check timer is never proof that an external commit failed.

Declining before entry carries no automatic penalty. Other participants retain
their waiting credit. A request keeps credit across compatible preference edits;
roster changes recalculate credit from the remaining participants so a newly
added party cannot inherit an unrelated veteran request's entire priority.

## How the matcher runs

Keep matching a pure, bounded function over an immutable, revisioned snapshot.
It returns proposals and diagnostic reasons. It does no storage, authentication,
networking, mutation or destination allocation.

1. Index queued requests by compatible activity/content requirements and useful
   composition attributes. Maintain these indexes from committed changes;
   reconstruct them after recovery. Do not scan Blob Storage to find candidates.
2. Visit older requests first with a stable tie-break and a rotating work cursor.
   An impossible older request must not starve everyone behind it.
3. Build candidate combinations while treating a premade party as indivisible.
   Check hard constraints against every affected member, including asymmetric
   preferences and blocked pairs. Reject internally impossible premades at entry.
4. Solve activity-role assignment across the whole combination. For the small
   authored roster, bounded backtracking with role-slot feasibility pruning is
   sufficient; it can move a flexible candidate between slots rather than making
   an irreversible greedy assignment.
5. Rank valid combinations by authored priorities: waiting credit, compatible
   intent, acceptable measured latency and preferences. Use stable integer/fixed
   scores and explicit tie-breaking. Skill ratings are not required for co-op.
6. Submit a proposal with expected revisions. The authority rechecks every
   participant claim and rule against current state before reserving anyone.

Limit candidate visits and search nodes per pass. Budget exhaustion means more
work remains; it is not evidence that no match exists. Keep activity preferences
in one request rather than cloning a request into independently claimable queues.
Only broaden requirements within each participant's preaccepted envelope. Hard
eligibility, trust, blocks and content compatibility never relax with time.

Use measured destination latency when available. Do not claim that the existing
input-latency hold measures RTT. Unknown latency is represented as unknown, with
an authored fallback or explicit refusal for a hard latency requirement.

Manual applications use the same claim, eligibility and consent transition as
automatic proposals. Listings expose only permitted fields and paginate by
revision; a stale vacancy is checked again at acceptance. A participant seeing
two offers cannot accept both into competing assignments.

The host schedules work outside the simulation pump and posts results through
the existing mailbox. If a channel is needed at this boundary, use a bounded
`Channel<T>` with explicit backpressure. Replaceable search wakeups can coalesce;
accepted participant operations cannot be dropped. Do not introduce one task or
timer per player, block the tick thread on I/O, or create a second mutable roster
inside a worker.

## Persistence, recovery and expiry

### Temporary lifetime, durable decisions

Use `IWorldAuthorityStore` and `IObjectBlobStore`: existing Azure blob storage in
production, the existing directory provider locally. Use a private service-owned
namespace with the existing identity-based access patterns. A player-writable
world save cannot be authoritative evidence of official group membership.
No new Redis, Cosmos DB, Table-backed party mirror or database per group is
planned. Reassess storage only against measured load and cost.

Memory holds search indexes, scoring scratch space and disposable subscriptions.
Persist accepted membership, roles, queued requests and waiting credit, consent,
participation claims, admission decisions and operation outcomes. Dungeon progress
and rewards remain owned by their existing authorities, not copied into parties.

Separate authored group policy/initial seeds from the live roster's lifecycle.
Expose one effective group view to grants, resolver, commands and clients. Runtime
state belongs in checkpoint/recovery data; redeployment reloads policy without
reseeding or discarding active parties. Revalidate a policy update against live
groups. If it makes them invalid, refuse it or require an explicit transition;
do not silently eject members.

Ordinary authoring undo/reset must not resurrect consent, release a committed
assignment, reissue invitations or repeat destination effects. Define operational
transitions as non-rewindable live decisions with recorded replay observations.
A destructive operational reset closes ingress and reconciles outstanding work
before starting a new lineage. Replay can reproduce decisions without live
credentials, notifications or external calls.

### Acknowledgement and the write boundary

Retain `Ack`'s current meaning. Add typed results for the new operations identifying
queued, applied, refused and durably committed outcomes, with operation id,
revision and a stable refusal code. Do not parse console narration to learn them.
A connection correlation id alone is insufficient for a retry after reconnect.

Expose a commit watermark/receipt from the existing serialized persistence path.
No success notification, portable membership proof or external admission request
may rely on a transition beyond that watermark. A lost response is resolved by
reading the stable operation id. The same id and payload return the same result;
a changed payload under that id refuses.

Group, request and participation-claim changes in one transition must share the
same committed record. On storage failure, stop publishing decisions dependent
on the uncommitted tail and report pending/recovery. Do not announce success and
then forget it, and do not block all worlds' simulation while awaiting a write.

Reuse `WorldExternalOperationJournal` for effects leaving the authority, including
its request-key binding, causal recovery, replay suppression and unknown-outcome
reconciliation. The group ledger owns membership; that journal owns dispatch of
the corresponding external action. A group checkpoint restored before an already
dispatched effect must be reconciled against retained external history before
releasing any claim or reopening readiness.

### Writer fencing and write cost

An ETag compare-and-swap detects a changed blob. It does not, by itself, stop an
old authority from reading a new ETag and writing again. Establish one live
authority epoch at activation and require it at commit publication and external
dispatch. An old host loses write/admission authority after takeover. Extend the
existing store/host path to enforce this; an Orleans activation assumption is
not a substitute for a two-writer failure test.

Use immutable journal segments and a conditionally advanced committed head if
the current page rewrite cannot meet the release load. The head binds epoch,
ordinal and checkpoint/tail references. A stale writer cannot refresh and adopt
a newer epoch; only the activation protocol can do that. Immutable writes not
reachable from the committed head are not accepted state. Checkpoint publication
must preserve later committed journal entries.

This is a bounded improvement to existing persistence, not a second transaction
engine. Measure the current append path first, including its bytes rewritten per
operation. Apply the same scrutiny to the external journal, which currently
rewrites a bounded document and refuses when full. Add safe retention/rotation
before normal group churn can exhaust it. Never overwrite a hot blob containing
all groups for every heartbeat.

Azure provides conditional writes and leases, but their scope must be respected:
a lease on one blob does not protect unrelated state, and a container lease does
not lock writes to its contents. The storage implementation must match its
documented fencing protocol. See [Azure concurrency semantics](https://learn.microsoft.com/en-us/azure/storage/blobs/concurrency-manage).

### Clocks and deletion

Simulation decisions stay ordered and replayable. Use the existing host clock
pattern and `TimeProvider` for operational scheduling. Host-side expiry metadata
records absolute service deadlines; the scheduler submits a revision-checked
expiry observation to the world, which records the outcome. The reducer never
reads `DateTimeOffset.UtcNow`, and replay never reevaluates a deadline against the
current clock. After downtime, process overdue observations before new admissions.
World gameplay time and service deadlines are explicitly different domains.

Disconnect grace, invitation expiry, proposal expiry and final group retention
are separate policies. Presence heartbeats are coalesced and do not cause one
blob write each. A disconnected member is not immediately removed or backfilled.

Closing a group rejects new actions immediately. Keep a bounded tombstone and
deduplication history through the supported retry/proof lifetime and until all
external outcomes settle. Never reuse group or operation ids. Physical deletion
is a later, idempotent operation restricted to the service namespace. Old
checkpoints and journal segments participate in the same retention policy; deleting
only the current row does not remove historical personal data. Garbage collection
must not remove the sole evidence needed to reconcile an outstanding transfer.

## Cross-world admission and security

First add a verified, unembodied session to the existing ingress model. A player
must not need a body in the coordinator to browse, accept an invitation or queue.
Reuse the owner routing and authenticated session work behind MCP, and close the
native transport's corresponding gap. Do not invent a dummy body for every user.

Normalize OAuth/OIDC and attested peer identity at their existing doors. OAuth
OBO remains request-confined for delegated downstream access using the established
Entra setup and `user_impersonation`; never retain user bearer tokens in a group,
queue, journal or checkpoint. Durable consent records describe the accepted
operation, not renewable authority to impersonate the user. Service-owned
persistence uses the service's own managed identity. A later action requiring
fresh user authorization must obtain it rather than replaying an expired token.

Extend `Puck.Attestation` integration with a purpose-specific membership/admission
proof carrying issuer-qualified identities, group revision, authorized audience,
assignment id, expiry and replay/channel binding. A signature over a member list
does not make it current forever. Target admission checks the live assignment
revision with its issuer; if it cannot establish validity it defers/refuses new
admission. Revocation invalidates subsequent uses and notifies active consumers.
Do not create a parallel trust list beside target-authored admission policy.

The destination resolves the group-scoped world identity once, persists the
assignment-to-generation binding and returns it for all members and retries.
The coordinator does not independently mint a destination per source host.

Extend existing transfer reservations to bind a whole accepted roster arriving
from several source authorities. Each source proves authority over its own
travelers; the coordinator cannot use the leader's `Drive` grant for everyone.
Reserve the roster's destination capacity before detaching anyone. Track each
source's prepare/commit state under the one assignment. Admit gameplay only when
the roster's startup condition is satisfied; network arrival is not instantaneous
or a distributed database transaction. On partial progress, hold the arrivals
safely while reconciling or performing explicit compensating returns.

Reuse transfer status and recovery for lost responses. A committed destination
cannot be rolled back merely because the coordinator timed out. Preserve claims
and reservations until the actual outcome permits release. Signed proof, target
reservation, source-body ownership and group consent are distinct facts.

Backfill uses a target-authored vacancy and progress revision. A parked member's
reserved place is not a vacancy. Reconnect and replacement race at the target's
same reservation boundary, so exactly one can obtain a place. Candidates accept
the disclosed progress; an encounter beginning or the run completing revalidates
that offer before admission. Final rewards remain the dungeon/identity authorities'
responsibility and use their existing provenance and idempotent write-back seams.

## Player surfaces and safeguards

Extend `WorldGroupCommandModule` with invite/accept/decline, role/leader transfer
and leave operations. Add activity request, proposal acceptance/cancellation,
listing/application and status operations through the same command registration.
The precise grammar is chosen once there; UI and MCP do not implement their own
membership rules or call internal provider methods directly.

Add the in-world finder panel through the existing HUD/view/interaction system:
party roster, activity selection, queue explanation, listings, ready check,
admission progress, reconnect/backfill and play-again. It must work with controller
and keyboard and respect split-screen ownership. Queue state remains visible while
playing. Completed groups can keep the run roster or return to original parties.

Return an initial authorized snapshot followed by ordered revisions. Bound slow
subscriber queues and provide resynchronization; do not disclose the full
coordination document to every member. Public listings expose only opted-in
fields. Private membership, invitation targets, block relationships and consent
history require appropriate access at query and subscription boundaries.

Use existing chat/identity authorization where it expresses the needed policy;
do not assume `chat.block` already means a matchmaking-wide block. Define and
implement that relationship explicitly. Rate-limit invitations, applications and
proposal churn by verified participant. Do not let a premade majority remove
strangers to take rewards: define removal eligibility, safe replacement boundaries
and preserved earned progress in activity policy. Reports are evidence for an
authorized review path, not an automatic reputation verdict.

Wait estimates come from observed compatible cohorts and include uncertainty;
insufficient samples produce an explanation, not a fabricated number. Track time
to successful entry, not merely time to a proposed match. Hard constraints may
make a request unmatchable; say which user-actionable condition can change without
revealing another person's private block or eligibility facts.

## Implementation sequence and ownership

Each slice includes its schema/codec changes, relevant tests and documentation.
No new top-level `Puck.Matchmaking`, `Puck.Groups` or control package is planned.
The engine owns generic relationships and transitions; the shipped activity
documents own dungeon terminology and policy.

| Slice | Code homes and deliverable | Evidence required before proceeding |
| --- | --- | --- |
| 1. Portable membership and sessions | `Puck.World.Schema`, `Puck.World.Protocol`, `Puck.World.Server`, existing MCP/hosting ingress. Qualified identities, local/portable member distinction, unembodied access, scoped group grants and actual management roles. | Same verified member across reconnect and authority change; wrong issuer, wrong tenant, stale epoch and forged identity refuse; leader cannot consent for another member. |
| 2. Recoverable group operations | Existing mutation/admission/results/checkpoint code, `WorldSiloHost`, authority/blob stores and external-operation integration. Atomic roster operations, revisions, request ids, durable receipts, reload preservation, writer fencing and safe expiry. | Crash before/after durable publication, lost acknowledgement, duplicate request, storage failure, two competing writers and reload/undo/replay behave according to the contracts above. |
| 3. Parties and invitations | `Puck.World.Console/WorldGroupCommandModule.cs`, group runtime state, HUD/client bindings. Creation, invitations, membership, succession, party visibility and retained original parties. | Multiple authenticated clients form a party without allocating coordinator bodies; last-place races, disconnect grace, voluntary leave and leadership transfer run through normal commands/UI. |
| 4. Activity requests and matching | Authored activity schema and data; pure evaluator in `Puck.World.Server`; installed scheduling/observation extension through existing hosting. Queues, indivisible premades, role assignment, atomic proposal claims and consent. | Independent exhaustive oracle for small candidate sets; adversarial role layouts, blocked/asymmetric combinations, stale snapshots and competing proposals; bounded progress under load. |
| 5. Destination admission | Existing resolver, federation codec, transfer escrow/recovery, silo activation and external-operation provider integration. Target-issued assignment generation and roster admission across source authorities. | Real multi-process test with separate sources; lost prepare/commit replies, target/coordinator restarts and delayed stale proofs; no duplicate bodies, assignments or leaked capacity. |
| 6. Complete finder experience | Existing in-world UI/console/MCP, listing projections and activity lifecycle data. Browsing, queue explanations, ready checks, backfill, completion and play-again. | Full acceptance journey using controller and authenticated remote clients; reconnect versus backfill race; identical authorization decisions through UI, console and MCP. |
| 7. Release qualification | Existing tests/canaries, telemetry, storage cleanup and unified Azure/Bicep/workflows. Load, recovery, managed-identity access, deployment/restart and retention. | Agreed load envelope, graceful and abrupt restarts, deployment during active runs, private-data checks and bounded long-running churn. |

Slices 1 and 2 are engine prerequisites, not optional post-release hardening.
Slices 3 through 6 should each expose a usable portion in the running game;
do not defer all UI until a backend-only implementation is called complete.
MCP remains an adapter to those same operations. Author one actual cooperative
activity from a shipped island district with declared start, progress, completion
and backfill boundaries. Do not assume that a destination row by itself supplies
those gameplay events, and do not invent a complete combat/loot game as a hidden
dependency of the finder. Its activity module lives with the existing world
content; the silo's existing configuration pins the coordination authority and
binds its private storage and installed extension.

## Verification and release limits

Extend the existing `Puck.World.Tests`, schema/protocol tests and relevant MCP
tests. Existing resolver, federation-transfer, checkpoint and extension-host laws
are regression anchors, not proof of the new behavior. Use the existing canary
runner for real process boundaries and run `Puck.World` for the user experience.
Do not revive the quarantined Post gate or create a separate permanent runner.

The matcher needs an independent small-input exhaustive solver to check valid
combinations and role feasibility. Under a deliberately bounded production
search, compare safety and any optimality claim only over fully explored cases;
do not claim globally optimal matching from a heuristic. Replay reuses recorded
proposal/clock/network observations rather than repeating live external work.

Fault tests cover every durable phase boundary, stale leader actions, revoked
memberships, wrong-audience proofs, all members disconnecting, journal exhaustion,
garbage collection during recovery, slow observers, restart during notification,
and an old host continuing after takeover. Replay or restore must never resend an
already committed external action.

Run schema/registry generation and checks, architecture and length checks, the
affected builds and tests, and documentation/citation checks for each slice.
Update XML/source-generated contexts, codecs, relevant READMEs and the owning
world skill references in the same change. Keep campaign status tied to actual
evidence. Use the existing build-once deployment workflow for final Azure proof;
this plan does not authorize a deployment or a merge.

Until the owner selects a different load envelope, use the following provisional
first-release targets. These are acceptance targets, not measured performance:

| Workload or result | Planning target |
| --- | --- |
| Population | 1,000 queued participants alongside 250 active group rows, including original parties and run groups. |
| Churn | 25 accepted state-changing operations/second for 30 minutes, plus 100/second bursts for 60 seconds; include invite, ready, cancel, leave and backfill traffic. |
| Durable operation result | p95 within 1 second and p99 within 3 seconds on the declared same-region deployment, with healthy storage. |
| Matching responsiveness | A newly feasible request is considered within 2 seconds under the workload; measure queue fairness separately from whether a valid combination exists. |
| Recovery | Reopen coordinator admission within 60 seconds of a replacement host becoming available with healthy storage and dependencies. Report time waiting for infrastructure separately. |
| Correctness and boundedness | No duplicate incompatible assignments or loss of acknowledged decisions; memory, retained history and pending work reach a bounded steady state during sustained churn. |

Report overload explicitly and bound admission queues rather than acknowledging
work that cannot be retained. These targets are measured on a named Azure SKU
and local reference host; they are not a promise about arbitrary hardware or a
regional outage. Broader scale changes the qualification envelope before release.

The present ceilings are 128 groups per world and 64 members per group. They
cannot silently become the online service's capacity contract. Establish a load
envelope before freezing runtime layout: queued participants, active groups,
operation rate, maximum premade size, churn and recovery time. Measure whole-world
candidate validation, `SyncGroups`, snapshot construction, journal amplification,
external-history retention and private projection fan-out at that envelope.
Raise declared ceilings with corresponding memory/wire budgets and evidence;
do not simply remove bounds or fragment queues to hide a bottleneck.

If a single logical owner cannot meet the agreed envelope after focused changes,
the next design step is stable participation ownership and a measured partition
reservation protocol. It is not independent per-activity match owners that can
assign the same participant twice. Do not promise that adding silo replicas will
parallelize one authoritative state machine.

Record the release's measured p95/p99 mutation-to-durable-receipt latency,
proposal evaluation time, successful-entry time, reconnect recovery time, queue
age distribution, tick impact, memory and storage operations/bytes per group.
Name the hardware and workload. No latency, scale or cost result is claimed by
this document.

## External references and their limits

[PlayFab's match formation description](https://learn.microsoft.com/en-us/xbox/playfab/multiplayer/matchmaking/how-matchmaking-works)
separates filtering, candidate ranking, final validation and bounded backtracking.
Those are useful algorithm boundaries; they do not establish Puck's consent,
authority or recovery semantics.

[Open Match 2](https://openmatch.dev/site/v2/overview/) leaves overlapping-ticket
resolution to the application. Adopting a matching service would therefore not
remove the participation-claim problem. Start with Puck's existing host and
storage; reconsider an external service only for a measured requirement.
