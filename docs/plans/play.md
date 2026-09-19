# Play

A world is only proved by being played. The engine can boot a document, hold a
hash steady, and draw a frame, and still say nothing about whether the place is
worth standing in: whether its creatures behave like creatures, whether four
friends and a stranger can reach the same dungeon from five machines, whether
one person can author a district while another walks through it. This
programme owns the reference game's remaining engineering and the two surfaces
that let other people and other programs reach it: the finder that admits a
party across authorities, and the MCP adapter that hands an agent a body or a
console. The authored experience is [the reference game design](../game/design.md);
the checks earlier game work recorded are in
[game development milestones](../development/game-milestones.md); the
reasoning behind every decision is in
[the decisions register](../decisions/play.md).

## Implementation status

Checked against `state/rebuild` at `6d0a4cbb2`.

- **Landed:** seamless crossing, the four ground corner authorities and the
  island, and the `four-corners-sharded`, `seamless-adjacency`,
  `seamless-four-corners-circuit`, and `quilt-nw-gap-edge-carry` canaries;
  per-body scale, decision commitment and rising-edge interruption, flock
  profiles and affinities, per-cell provenance, the `puck canary` manifest
  runner, `WorldEntityAddress`, kit speed envelopes; the local group, role-aware
  grant, transfer, and external-operation foundation; the MCP host extension,
  local stdio, tool and result contracts, and remote HTTP with OAuth (owned by
  [`Puck.Mcp`](../../src/Puck.Mcp/README.md)); every tabletop primitive and
  the shipped `games/` fragments.
- **Not started:** the One World re-authoring, the content and federation
  waves, `Puck.Audio`, namespace normalization, the frames document, the
  neighbour tape, the seat's view state, the finder's units A to C and its
  slices, MCP milestones 2 to 5b, composed play, and the few-thousand-creature
  acceptance workload. One security claim is open rather than unverified:
  nothing yet witnesses that a binding cannot escalate its destination beyond
  what its grant allows, so the claim is treated as false until a canary shows
  it.

## The forcing artifact

The quilt, [the forcing world](state-and-language.md#the-forcing-world)
shipped as a product by [runtime and delivery](runtime-and-delivery.md), is
played: by a group the finder assembled from several source authorities, in a
world dense with creatures, with an agent authoring one district through MCP
while the humans walk through it. The party crosses corner boundaries and the
island's seams, plays the arcade cabinets and the parlour's boards, and leaves
with a replay tape; a few thousand creatures are densely packed on the ground
and in the dive district's water at 60 FPS on desktop and Steam Deck, with
per-body scale consistent across every seam; two friends and compatible
strangers accept their roles and activity, enter from different source
authorities, lose a member, reconnect, backfill, and finish together; a
Participant-profile agent plays with nothing but perception and actuation, and
an Operator-profile session edits a district live and captures the composed
frame that proves the edit arrived.

**Check:** `Puck.World` boots the one world with no bracketed stderr line;
`world.imports` names every district under its alias and `body.pose
spawn:<district>` stands a seat in each; a `puck canary` proof runs group
authority, recipient consent, and qualified membership across two
authenticated authorities with both streams drained and a red leg for every
claim; the acceptance playthrough runs end to end through real authenticated
processes and reports rendered whole-frame cost at the creature workload; the
Participant agent completes the run without reaching exec, files, the
framebuffer, or the tape by any tool name or profile argument; the Operator's
captured PNG returns from `FrameCaptureRequest.Completion`.

## Packages

### The playthrough substrate remainder

**Owns:** the state section's row ownership, the handheld attach pair, the
`chance` level's hidden operands, the four-corners canaries' ring.

**Delivers:** the catalog walk per operand read goes and the idle tick lands
under four milliseconds; the handheld's attach pair ships once the rule-work
sheet has room for it (a region-scoped pair interaction, or a placement-effect
cost derived from the population it rebuilds); the `chance` level's hidden
operands; the multi-authority four-corners canaries re-recorded at the shards'
2.75× ring.

**Check:** `puck landing`; the idle-tick measurement recorded in the milestone
record.

### The population

**Owns:** the frames document shape and its envelopes, `puck canary`'s
manifest tree and the `four-corners-sharded` scripts, neighbour-field
derivation and the per-tick neighbour tape, ghost records.

**Delivers, in this order:**

1. **The canary widening** (first, because the frames proof is a canary):
   `four-corners-sharded` exercises vertical and island handoffs, cross-host
   body contact, autonomous travellers, retained dual-stick control, and derived
   diagonal peers; and the binding-destination escalation witness, one
   real-path refusal-with-control canary in which a non-privileged principal
   authoring a binding whose destination is an administrative verb refuses
   while the same mutation naming an ordinary verb applies.
2. **Frames, as the envelope ratification:** one document shape (root frame,
   sibling frames, body-parented frames on demand) whose envelope takes an
   angular-speed bound, a minimum feature size or aspect-ratio bound, and a
   mass-ratio ceiling beside the size and speed band, sized analytically and
   never by sweeping the sample worlds; an interval proof names the failed
   quantity, kernel, frame, and envelope corner; every kit's speed is bound by
   an authored envelope and adjacencies derive one symmetric overlap from body
   reach, targeting reach, and two slower-side delivery periods of closing
   speed with outward rounding; the read-back shows declared values and derived
   placements with proof margins.
3. **The neighbour tape, then ghosts:** neighbour-field derivation hoisted to
   delivery, per-tick records taped separately from definition revisions, the
   delivered revision a consumer tick sees pinned at tick start (never "latest
   when accessed"), ghosts read-only and never authoritative, and
   `WorldSnapshot` carrying per-entity scale so the remote side of the
   adjacency sweep is scale-consistent.

**Check:** each proof's positive and discriminating legs under `puck canary`;
a real world run at the acceptance workload with rendered whole-frame cost;
the falsifiers do not fire (density-dependent unbounded perception work, slot
reuse inheriting another creature's memories, duplicate hearsay increasing
corroboration, checkpoint divergence, incompatible followers borrowing a
narrow route, split and replan bursts breaking the frame budget).

### The seat

**Owns:** the seat-lifetime view state, input preference, the feel sitting,
the touch-triggered win slice, navigation and equip facets, the `body:n`
lowering, entity-addressable rules and elemental interactions.

**Delivers:** one seat-lifetime view state (world-owned camera structure,
profile-owned input preference, standard dual-stick movement and look, one
logical basis shared by intent composition, local rendering, traveler
rendering, cursor capture, and read-back; no slot-global orbit, binding-side
feel cache, renderer-local orbit cache, or mixed schema survives), then the
owner feel sitting, then the touch-triggered win slice, then navigation and
equip; authored local `body:n` lowers to `WorldEntityAddress` at compile or
install time; entity-addressable rules and elemental interactions land with
the reference-game content that proves them, after re-verifying that the
property vocabulary, rules, interactions, and the arena district's combat
rules survive the schema drift that once refused the combat document.

**Check:** the feel sitting recorded in the milestone record; `puck canary`
for the view-state and win-slice proofs; the content proofs from the game
itself.

### The content wave

**Owns:** the studio prologue's acts, the arena crawl's spawners and bosses,
the arcade hearth's seam content, the retail basis deltas that pin a boot seat
and district behind a fact, and the One World re-authoring: the island on the
one-description rule (rendered and collided from one description), the plaza
with the granary court, the arcade, and the market hall; the proving ground
and the garden as districts; the pool as the dive district, a track as the
kart district, a course as the jump district, the studio canvas behind the
fourth arch; two local seats in a split layout; a spawn point and a navigation
domain per district; `captures` rows for parity; districts as modules under
an alias exporting only their control rows. The primitives the island refuses
without: a placement whose instances are dealt from a keyed row (one instance
per cell, keyed by the cell, laid out by the row's `distribution` region, a
variant chosen by a second row, following the row live), an observation field
landing on a row of any cell kind, a placement's `respond` reading an ordinary
cell, an inhabit facet whose count is a state cell, identity-carried facts, and
a `machine` screen booting a `puck.cartridge.v1` document.

**Delivers:** the island authored on those primitives; `granaries.world.json`
moved under the modules rather than deleted; the frozen documents, the
scenarios, their canaries, and `experimental/Puck.Demo` retired with a ledger
naming each successor. After [S6](state-and-language.md#s6--modules-and-the-forcing-world)
lands, the island and its districts move onto the same modules.

**Check:** the forcing artifact's boot check; every retired canary re-recorded
against the one world or deleted with a named successor.

### Wave 4 — `Puck.Audio`

**Owns:** adaptive music, event voice, a rhythm judge, diegetic synthesizer
machines; the tick clock, director, judge, and instrument machines in the
simulation; the mixer and synthesis in presentation.

**Delivers:** authored music as tracker-style data (patterns, sequences,
instrument patches) under a structural layer (segments with transition
markers, conditional layers, director embellishments), synthesized end to end
with no sample assets; `Puck.Audio` parses no document; voice gains a producer
that estimates syllable counts from dialogue text and babble correlated with
a live body position.

**Check:** the audio guide's and reference's recipes run against the real
executable; the mux determinism check and audio-device failure paths become
law tests or `puck` verbs, so `experimental/scripts` holds no sole coverage.

### Housekeeping, last

**Owns:** the `Puck.World.Client` seam (`PlayerRoster` reading the live server
through a link query, remote-default), the self-update signing chain
(`puck publish --sign` and upload; the launcher installs a refusing placeholder
verifier today), namespace normalization, and the human-at-a-window procedures
under `docs/verification/manual`.

**Delivers:** each once, over the settled tree, after the splits.

**Check:** `puck landing`; a signed release manifest verified by the launcher.

### The federation wave, and the remainder

**Owns:** provenance signing for carried state, the bilateral attestation rows
a duel or wager is, a profile world attached at a shard's seam, the silo-hosted
hub under Orleans (a grain is a world instance, the silo hosts the door,
hosted persistence goes non-private through the silo's managed identity,
clustering rides Storage; the second container app and managed identity are
bicep in the sibling Azure.Resources repository), and the hub's ownership by
the platform's public-content identity.

**Delivers:** the wave above. The remainder is one package that waits for the
forcing world to be played, because every row is a question a played world
asks: per-viewport user- and group-scoped destination images; a
destination-clock interpolation ease; multi-authority replay (a destination
arrival is not taped today, and `replay.verify` reports a remote or
unavailable target as not verified, never as passing); bounded queues,
backpressure, and query redaction on the observation feed; derived-band
read-back with a long-run remainder-drift demonstration; destination and
session resolution on the wire, an unembodied session authority, then optional
body reservation and allocation; issuer-qualified group and document claims,
entry reservations and idempotent handoff tokens fenced by epochs, leases, and
durable commit records, hydrate, suspend, and migrate for persisted worlds,
and durable recovery when an authority dies mid-transaction; retry-safe
cross-document write-back (an operation id, a precondition, atomic persistence,
an observable receipt); cloud-catalog discovery through
`storage.discoveryEndpoint`; latency equalisation from a real round-trip
source; and local `Join`'s pre-allocation as enforceable admission semantics.

**The gated ladder**, each row waiting on the one before: the extension
registry as the selection mechanism; extensions validating their own
configuration, cartridges as pinned content, renderers as extensions; sinks as
first-class (viewport, quadrants, recordings, streams) with render extent on
the sink and one view-and-sink compositor; the screen row collapsing into a
placement facet with string ids and links addressing by name; the world as a
screen source at a target-selected tier, a specified client wire, and
replication; proximity co-location on the interaction flag, occlusion-aware
candidacy, transfer stability, co-location acceptance, junction headroom,
contention facts with authored responses, adjacency as scheduling affinity,
tick health as a fact; contact-counterpart and region-occupant targets after
a body-to-body contact seam; threat tables after a keyed-table primitive;
spatial partitioning for proximity once a measurement names the scan as the
cost; Native AOT after reflection-based JSON and COM interop are replaced.
Unmeasured, deliberately: contact sampling budgets, the compound-collider
volume ceiling, mirrored stamps doubling instance-grid contribution, per-tick
input-hold bookkeeping, N simulations per host.

**Check:** `four-corners-sharded` and its successors green under real
separate authorities; the playthrough's replay tape reproduces the run
elsewhere.

### The finder's foundation

**Owns:** durable operation identity through ingress, application, the
serialized persistence queue, receipt lookup, and typed completion; a
bodyless session principal and session table at the authenticated doors;
row-scoped group authority and the invite, revoke, accept, decline, set-role,
transfer-leadership, and administrative-removal transitions; one effective
roster surviving policy reload; the Group wire tag and mutation ordinals,
checked against the live catalog and never cast from enum values;
`IObjectBlobStore`'s deletion operation.

**Delivers:** the three units and their integration as one landing, because
they share one identity contract. A lost reply followed by the same
authenticated request returns the retained decision without applying twice, a
changed actor or payload refuses, and crash before or after root publication,
refused operations, and uncertain writes all produce honest results. Native
and MCP ingress can observe and act without allocating a body; wrong issuer or
tenant, stale session generation, forged claim, and ambiguous same-tag
memberships refuse; a session actor cannot drive or transfer a body.
Recipient-only acceptance, the last-place race, group-A-versus-group-B
authorization, role changes, founder departure, deadline expiry, and reload,
reset, load, and redeploy each have a discriminating control. Consent stays
outside editable state and authoring undo; no save or undo resurrects
acceptance, revoked membership, or a spent invitation. Writer fencing keeps
the activation epoch at the single authority-root publication and carries it
into external dispatch; append bytes, checkpoint cost, receipt-index growth,
and the external journal are measured before segmentation or retention is
chosen.

**Check:** the existing membership, role, mutation-outcome, authority-store,
retirement, silo-lifecycle, and checkpoint-codec laws as regressions with the
consent, session, durable-result, and roster-survival cases added; the
real-process canary for group authority, recipient consent, reload survival,
and qualified membership across two authenticated authorities, with dropped
replies and a takeover while the old writer keeps running; every denial with a
successful control and an actor distinct from the target.

### Parties and matching

**Owns:** `WorldGroupCommandModule`, the in-world finder panel, the authored
activity schema (pinned destination and policy revision, size, composition
slots, verified eligibility predicates, permitted preference expansion,
progress and backfill and completion policy), the queue request, proposal, and
participation-claim records, the pure matcher in `Puck.World.Server`, and the
scheduling extension through existing hosting.

**Delivers:** manual parties (one create operation inserts the creator with
the initial role; an invitation never inserts its recipient; accept checks
identity, expiry, revision, and capacity in one transition; repeated identical
requests return their recorded outcome) with succession by explicit transfer
then the next eligible member after disconnect grace; matched groups through
Draft, Queued, Proposed, All accepted, Admission pending, Active, and
Reconciling, where the finder never creates tickets for unconsenting members
and final acceptance rides one `WorldMutation.Batch` under an expected
fingerprint; a matcher that is a pure bounded function over an immutable
revisioned snapshot (older requests first with a stable tie-break and a
rotating cursor, premades indivisible, hard constraints against every member
including asymmetric preferences and blocks, bounded backtracking for role
assignment, stable integer scores, budget exhaustion meaning more work rather
than no match, measured latency or an explicit unknown); listings that expose
only opted-in fields; waiting credit that survives compatible edits and is
recalculated from the remaining participants on roster change; rate limits per
verified participant; an original party and a matched run group as separate
flat rows.

**Check:** several authenticated clients form a party without coordinator
bodies; last-place races, disconnect grace, voluntary leave, and leadership
transfer run through normal commands and UI; an independent exhaustive solver
checks the matcher on small candidate sets; adversarial role layouts, blocked
and asymmetric combinations, stale snapshots, and competing proposals; bounded
progress under load.

### Admission and release

**Owns:** target-issued assignment generation and roster admission across
source authorities through the resolver, federation codec, transfer escrow and
recovery, silo activation, and external-operation provider; the complete
finder experience (browsing, queue explanations, ready checks, backfill,
completion, play-again) through the in-world UI, console, and MCP; release
qualification.

**Delivers:** the destination resolves the group-scoped world identity once
and returns the assignment-to-generation binding for all members and retries;
transfer reservations bind a whole roster from several sources, each source
proving authority over its own travelers, capacity reserved before anyone
detaches, gameplay admitted only when the roster's startup condition holds,
partial progress held safely or compensated explicitly; backfill uses a
target-authored vacancy and progress revision with reconnect and replacement
racing at one reservation boundary; a membership and admission proof through
`Puck.Attestation` carrying issuer-qualified identities, group revision,
audience, assignment id, expiry, and replay binding, checked live with its
issuer and never a parallel trust list; OAuth OBO request-confined with no
user bearer token retained in any group, queue, journal, or checkpoint; one
cooperative activity authored from a shipped district with declared start,
progress, completion, and backfill boundaries.

**Check:** a real multi-process test with separate sources, lost prepare and
commit replies, target and coordinator restarts, and delayed stale proofs,
with no duplicate bodies, assignments, or leaked capacity; the full acceptance
journey with controller and authenticated remote clients, the reconnect versus
backfill race, and identical authorization decisions through UI, console, and
MCP; the provisional load envelope (1,000 queued participants with 250 active
groups; 25 accepted operations per second for 30 minutes with 100-per-second
bursts; durable results p95 within 1 s and p99 within 3 s; a newly feasible
request considered within 2 s; admission reopened within 60 s of a replacement
host; no duplicate incompatible assignments, bounded memory and retained
history under sustained churn) measured on a named SKU, with p95 and p99
mutation-to-durable-receipt latency, proposal evaluation time, successful-entry
time, reconnect recovery time, queue age, tick impact, and storage operations
per group recorded.

### MCP — the local surface

**Owns:** the Participant composition (`puck_affordances`, `puck_observe`,
`puck_act` through `WorldAgentBridge`), binding lifecycle, bounded admission,
receipts and retry semantics; `puck_doc` (read, validate, cost, mutate, save,
with live mutation and offline save distinct and replacement preconditions),
`puck_capture` (recording and replay controls, `puck_capture_frame`), and
`world.cost`.

**Delivers:** the three participant tools with closed argument objects
(observe requires exactly one of pose, channels, state, targets, contacts,
properties and returns the typed observation including `Refused`; act is a
closed move, press, stop union with positive durations, exact channel names,
and a binding-scoped request key), mutations serialized per binding in
admission order with overflow reported rather than dropped, identical retries
returning the same receipt and changed input refusing, no automatic retry after
an uncertain disconnect, and disconnect cleanup that never issues an
unannounced privileged Stop; the Operator surface using Console identity and
the full command registry through a dedicated session with its own barriers;
frame capture after exec entering the same session ordering before arming;
one awaitable still returning the completed PNG through
`FrameCaptureRequest.Completion`, busy when the pending slot is occupied, and
failing clearly without a renderer; `puck_doc`'s whole-document replacement
carrying an expected revision enforced at application; `world.cost` returning
`WorldCostReport.Generate`'s facts intact with `Admitted=false` distinct from
validator rejection; MCP and IPC serialization, network waits, and still waits
kept off the pump.

**Check:** a Participant cannot reach exec, files, frame, or tape by guessing
tool names or changing profile arguments; grant and revoke, channel reorder,
body reuse, observe denial, cancellation, and saturation against the real
host; concurrent document edits and malformed candidates, disk failures, codec
declines, drops, stale recording handles, timeout after dispatch, and replay
arming refusal, with the console available throughout.

### MCP — the remote surface

**Owns:** the parity runner as an isolated job and approved device handles
with revocation; live Entra consent, DNS and ACME, first-party and external
sign-in, deployment; Participant authority bindings and durable delegated
mutations through `WorldExtensionClient`.

**Delivers:** a parity job that boots the approved fixture in isolated
directories and returns the content, state, and pixel verdicts from the real
runner; ingress that exposes only host-approved source handles with consent
and revocation; remote hosting on the official ASP.NET Core SDK with JWT
bearer authentication and MCP 2026-07-28 on both transports; a
tenant-qualified subject resolved through admission and bound to a world
principal, index, generation, and approved body, never cast into a seat;
OBO only for a valid user token intended for this API, obtaining a distinct
downstream ARM token, with token passthrough and credential fallback
forbidden; discover, invoke, and status over granted bindings by stable
request key with claim-before-send, no blind resends, unknown outcomes, and
replay suppression; one authentication mode.

**Check:** each item established against the real tenant and the deployed
host; owner routing, live consent, reauthentication, durable-job restart, a
lost ARM response, binding revocation, and replay suppression.

### Composed play

**Owns:** the chess module's `search` rows and board `enforcement`; the
composed-game acceptance check.

**Delivers:** checkmate, stalemate, draws, and a CPU opponent authored in the
shipped chess module; two chess instances in one world from one rules
fragment, one moved by hand on the table and one driven by text, producing
equivalent accepted histories while their motion and presentation differ; then
two solitaire tables at once; then poker with two participant identities in
the same roles. A capacity failure here needs accurate work pricing or a
general correction, never a game-specific bypass.

**Check:** the shipped `games/` fragments boot and their law suites pass; the
three compositions run in one world.

## Sequencing

| Step | Packages, in parallel | Why here |
|---|---|---|
| 1 — today | The finder's foundation; MCP's local surface; the population's canary widening | None reads the rebuilt state vocabulary. |
| 2 — after the rebuild lands | The playthrough substrate remainder; the population's frames, tape, and ghosts; composed play | These rewrite or author the rows the rebuild is rewriting. |
| 3 | The content wave and the One World re-authoring; the seat; parties and matching | The content wave needs districts; the seat opens with the view state so feel stays the gate. |
| 4 | The federation wave; admission and release; MCP's remote surface; the gated ladder from its first row | Destination admission across sources needs the federation wave's attestation. |
| 5 | The federation remainder; `Puck.Audio`; the forcing artifact end to end | The remainder's rows are what a played world asks for. |
| Last | Housekeeping | Once, over the settled tree. |

Deferred until the forcing world plays, and re-asked at step 5: how another
identity's world attaches to the hub; the pre-allocation embodiment subject;
multi-world replay tape ownership; ephemeral terminal policy; federated group
proof through issuer-qualified group ids; the admission-policy
representation; the `OwnershipPolicy` and `SharedStateScope` contract; the
reauthentication policy for an OBO credential expiring during a durable job;
distributed placement and portable world handles; the finder's load envelope
beyond the provisional targets.

## Verification summary

```bash
dotnet run --project src/Puck.World -c Release -- --exit-after-seconds 2
```

```bash
puck canary
```

```bash
puck landing --against origin/main --base <the commit the branch was authored from>
```

```bash
puck parity
```

```bash
dotnet test tests/Puck.World.Tests -c Release
```

```bash
dotnet test tests/Puck.World.Schema.Tests -c Release
```

```bash
puck doc-links
```

---

[Plans](README.md) · [Decisions](../decisions/play.md) ·
[The reference game design](../game/design.md)
