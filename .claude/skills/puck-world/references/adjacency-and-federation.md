# Adjacency and federation

Adjacency is authored ownership topology. It makes two world regions continuous
without adding portal furniture, co-locating their authorities, or allowing two
writers for one body. Federation is the authenticated transport used when the
neighbour is not hosted in the same process. Consumers depend on authority
identity and capabilities, never on that hosting distinction.

## Authoring contract

An invisible boundary needs three document rows:

1. `references` names the neighbouring world document.
2. A global, persisted `destinations` row names that reference.
3. An `adjacencies` row names the destination, the neighbour's reciprocal row,
   and a rectangular boundary (`center`, outward yaw/pitch, width, height).

`WorldAdjacencyUnavailable.Closed` is the only failure treatment today, and it
covers every terminal outcome, not just an unresolvable destination row: an
unreachable neighbour, a full border, a refused admission, a refused leave, a
commit abort, and a reservation the destination no longer has all clamp the body
one raw fixed-point unit inside the boundary, clear its pending continuum, press
`onUnavailable` once, and name the refusal on stderr. Clamping is what makes a
refusal terminal — the sweep answers `Crossed` for a body already beyond the
threshold, so a refusal that leaves the body outside is a refusal per tick. Only
a capacity refusal under `full: retry` re-queues, bounded by a retry ceiling
carried on the transfer. `onUnavailable` may name a declared channel for authored
sound, animation, state, or other feedback; safety never depends on the binding.

An adjacency row may author `capacity` — the same border policy portal furniture
carries, echoed with the unavailable treatment by `world.adjacencies`. Portals
remain intentional authored travel and are not used to represent seamless
topology.

One traversal mints one crossing. The adjacency scan skips a seat already named
by a queued or in-flight transfer (announced once per transfer id on stderr),
because the sweep keeps answering `Crossed` while the traveler waits for its own
transfer to drain.

`WorldDefinitionValidator.ValidateAdjacencies` proves the pair before boot. It
requires stable destinations, reciprocal counterpart names and dimensions,
world-up-preserving frame pairs, and a derivable overlap envelope. At a corner,
two direct neighbours may imply one diagonal observation peer only when both
paths name the same document; validation also proves the transform diamond
closes. Authors declare topology and physical envelopes, not safety margins.

`WorldAdjacencyPolicy.TryDeriveOverlap` derives a symmetric overlap from both
worlds' body reach, interaction/target reach, closing-speed ceilings, and two
periods of the slower simulation rate, rounding outward in fixed point. Those
five inputs are `WorldOverlapTerms`, and the overload taking them derives the
same depth from a neighbour that never handed over its document.
`WorldFrameIsometry` is the one point/vector/orientation mapping used by
crossing, contact, transfer, and presentation. `MapArrival` is the one arrival
function portal furniture and invisible borders share: it anchors on the two
frames' own origins, so an off-centre crossing lands at its counterpart point by
the isometry rather than by a seam carried beside the traveler, and it adds one
wrapped yaw delta to the traveler's own unbounded accumulator.

An `IWorldNeighbourResolver` may answer `Resolved` (the whole document),
`Attested` (a `WorldCounterpartAttestation` — the neighbour's seam edges plus
its overlap terms, composed but not necessarily verified: `WorldStorageNeighbourResolver`
produces this arm today from an unsigned fetched copy), `VerifiedAttested` (the
same attestation shape, reached through `WorldCounterpartAttestationProtocol.TryVerify`,
which verifies a signed claim against the reading world's own `admission` keys
and returns both what it attests and the verified chain's own subject — a
resolver must still bind that subject to the neighbour key it was resolving
before trusting the result: `WorldApiCounterpartResolver` (`Puck.World.Server`)
is the production resolver for an owner-named `WorldReference`, and refuses
unless the verified subject parses as the same `Guid` as the reference's own
`Owner`), or `Unavailable`. Both attested arms prove the same four per-fact
refusals the document arm does for an ordinary two-document adjacency: missing
reverse edge, non-reciprocal counterpart, mismatched extents, and a frame pair
that loses world up. A derived corner is proven the same way as an ordinary
adjacency — each of the three documents involved (the two direct neighbours and
the shared corner) proves only its own edges — but the corner walk accepts only
`Resolved` or `VerifiedAttested`: a plain `Attested` neighbour names a claim
about a third authority that nothing has verified, so it never enters a corner
proof. What a verified diamond proves is authenticated consistency of the
signed statements within a signed validity window, not real-world truth and not
equality to any document the reader never saw.

The five quilt documents are the worked stress example: four coloured ground
worlds meet at one corner and `quilt-island` is vertically adjacent to all
four. They are test content, not charter game worlds.

## Runtime ownership and handoff

Every active body has exactly one authoritative writer. Crossing an adjacency
uses the same reserve-then-commit escrow for local-process and federated
destinations. Reservations bind capacity until commit, explicit abort, or an
exact tick-denominated deadline. A stable mobility identity binds the origin
`WorldEntityAddress` incarnation and a monotonically advancing ownership epoch;
reserve atomically leases that incarnation and expected epoch to one transfer,
and commit compare-and-sets it to the next epoch exactly once. Ambiguous status
uses an exact committed outcome, never a scalar inference from later transfer
IDs. Acknowledgement retires that outcome; a later epoch for the same traveler
may supersede a lost acknowledgement. The one current credential per mobility
identity rejects delayed replay. These tables are bounded by active
transactions/travelers, not lifetime crossing count.
A committed route can forward later input and submissions through further
handoffs, so an old credential remains a route to the one current writer
rather than a stale body slot. Generation recycle creates a different mobility
identity and cannot inherit the old credential.

Entity identity is `WorldEntityAddress(authority, index, generation)`.
`WorldAuthorityRoute` carries that complete address plus an epoch, and
`WorldSeatAuthorityRouter` publishes the complete claim with CAS. Rendering,
input, audio, HUD, targeting, and read-backs consume that one route.
`WorldContinuum` and adjacency presentation compare the full address; an
authority string plus a recyclable index is not an identity.

Every reciprocal handoff uses a true ownership deadband rather than transferring
at the authored plane and merely suppressing the return edge afterward. A body
remains with its current writer until its center reaches the far side of that
deadband, so the mapped arrival is already that far inside the destination and
the pair closes. The boundary's geometry selects which deadband
(`WorldAdjacencyPolicy.OwnershipThreshold`):

- A **vertical wall** carries the reciprocal contact hysteresis — two maximum
  body reaches plus contact skin — so a legal cross-authority melee correction
  cannot manufacture an immediate return. Because that threshold moves a
  vertical ownership face outward, the runtime expands its horizontal half-width
  by the same threshold; otherwise two perpendicular faces leave an unowned
  threshold-by-threshold square at their corner and a diagonal traveler can
  escape both writers. The authored vertical aperture remains exact.
- A **floor/ceiling** boundary cannot carry that much: one body radius of delayed
  ownership would consume ascent headroom and can place handoff after solid
  destination terrain. It carries `TryVerticalSettleDeadband` instead — derived
  per document from each kit's downward envelope (gravity over one authority
  step, capped by terminal fall or sink speed) carried over one more step, plus
  the contact skin, plus one raw unit, every quotient rounded outward. It is a
  centimetre-scale distance, not a body radius. The separating invariant: larger
  than any uncommanded descent, smaller than any commanded one — a settling
  arrival never re-crosses its own reciprocal edge under gravity alone, and a
  body driven or already falling downward clears the deadband inside one step
  and transfers. A zero threshold there reads a settle as a departure and
  oscillates the traveler across the seam.

`TryDeriveOverlap` covers whichever threshold is larger, and
`WorldAdjacencyBand` derives its aperture from the same numbers the ownership
sweep does, so no point ownership claims is outside every contact band. The band
bounds the OWNED side by the derived depth and is unbounded outward — a body past
the plane has left this world's terrain, the neighbour's own geometry is what
decides whether there is ground, and a finite outward bound is a hole for as long
as a handoff takes to drain. Its horizontal half-width expands by the ownership
threshold on a yaw-only face for the same reason the sweep's does. An
intermediate hop of a derived corner path is gated on the depth alone
(`WorldAdjacencyBand.Transits`): the junction beyond two perpendicular rectangles
is exactly the region the diagonal peer serves, and the commuting-diamond proof
is what makes transport past the aperture the same point either way round. The arrival-border latch
remains a defense for federated delivery and observes both ends of the first
destination step, so a genuine rapid reversal cannot run outside its owner while
the reciprocal edge is disabled. Other edges remain eligible, including deterministic forwarding
at a multi-world corner. One already-evaluated source step carries its mapped
geometric cursor, exact engine-time interval, consumed-through watermark, and
bounded face count through every onward owner. Each destination sweeps its own
terrain before selecting another ownership face. Before an ordinary authority
step, the composition root resolves pending topology under that authority's
gate; a body cannot evaluate input, actions, timers, gravity, or movement while
the geometric cursor is pending or while the step's start overlaps consumed
continuum time. This makes a 60 Hz source safe when its destination is scheduled
at 120 Hz. Exhausting the eight-face work ceiling clamps one raw fixed-point
unit inside the last confirmed owner and removes only outward normal velocity.

## Input and action continuity

Movement and actions route to the seat's current authority. A transfer carries
action state by declared name rather than document-local ordinal:

- the previous threshold bit, so arrival cannot manufacture a rising edge;
- the last admitted held composition value, bridged until the destination
  receives its first real input publication (neutral included);
- named counter/timer action registers admitted only when the destination
  declares the same name and kind; their authored programs give those values
  cooldown, charge, or other gameplay meaning.

The bridge is why holding jump across a handoff remains one physical hold
rather than release-then-press. `player.press`, `player.channels`,
`player.state`, `player.targets`, `world.contacts`, and `player.where` follow
the same seat route after a crossing. A new seat-facing verb that reads the
boot population directly is a routing defect.

## Contact across the overlap

`WorldAdjacencyFields` owns the delivered neighbour images used by both contact
and presentation. It freezes the complete direct-plus-derived projection graph
once per authority tick; contact must not rebuild that graph per body.
`WorldAdjacencyContactField` walks the same projection set as rendering,
including a diagonal peer whose two overlap bands meet at a four-way corner. It
maps the body through every path stage, resolves neighbour terrain and eligible
generation-addressed dynamic bodies, then maps the answer back through the
inverse path. Querying only authored direct edges leaves a physical hole at the
literal corner even when the rendered diagonal floor is present.

Contact integration calls `IContactField.ResolveSweep`. `WorldSolidField`
subdivides a long step deterministically before applying its ordinary SDF
resolver; the adjacency wrapper sweeps both local and mapped-neighbour
geometry. Do not replace this with endpoint-only sampling: a capsule endpoint
inside a thin slab has an ambiguous nearest gradient and can be extracted
through an edge or the underside.

Remote dynamic poses currently enter `WorldAdjacencyContactField` from
delivered floating-point snapshots. Until Track 3 tapes fixed, tick-aligned
neighbour records and installs the field at delivery time, do not claim replay
determinism for cross-authority dynamic contact.

The boot replay tape also does not reproduce federated arrival/forwarding. Do
not cite `replay.verify` MATCH as federation evidence; the five-authority runner
and focused laws below are the current executable proof.

## Federation transport

`WorldFederationCodec` (`Puck.World.Server`) is the one authority-to-authority
codec. It reuses the framing, bounded reader/writer, and refusal vocabulary in
`Puck.Networking/WireCodec.cs`: `WireFrame` carries
`[u32 following][u8 kind][payload]` little-endian — the same grammar
`WorldFrameCodec` defines for submissions — over `WireReader`,
`WireWriter`, and `WireRefusal`. Every decoder over peer bytes is
Try-shaped and bounded before it allocates; a decoder that throws on hostile
bytes is a defect. Add a message as a leaf there, never as a second dialect.

One connection carries the whole conversation: the federation wire key
(`WorldFederationCodec.WireKey`, distinct from the interactive peer key), then
`Challenge`/`Authenticate`/`Ack`, then framed requests in order,
request-then-response, until `Observe` or `IntentStream` takes the connection
over and streams on it. There is no second hello and no correlation id.

Refusals are named (`WorldFederationRefusal`) and every refusal frame's text
opens with the name, so a peer and `WorldTcpHost.FederationRefusals` count the
same vocabulary.

`WorldRemoteAuthority` holds persistent authenticated lanes per source authority
namespace: a `FederatedIntentPump` for the latest-value intent stream, and a
`Puck.Networking.PersistentRequestLane<WorldFederationRequest, WorldFederationResponse>`
(adapted by `WorldFederationLaneProtocol`) per `WorldFederationLane` concern —
`Transaction` for reserve/commit/abort/acknowledge/status, `Routed` for route
lookups and forwarded submissions. Connect, hello, and challenge are paid once per lane.
A lane is strictly ordered, so everything sharing one queues behind whatever is
in flight; that is why the two concerns are separate, and why adding a request
kind means deciding which lane it belongs on.

Only a failure to CONNECT takes a lane out of service, and only after a retry.
A break on an established connection reconnects and re-sends once without
entering backoff — a live neighbour must not be marked unavailable over one
recycled socket. Slowness never changes lane state: the worker has no read
deadline. A lane inside its backoff window answers every request immediately
with `LaneUnavailable` without touching a socket, which is what keeps a closed
edge from stalling the source's tick.

Every transfer step answers. A step that ran out of time is a named refusal,
never "ask again": a caller told to retry would hold the transfer while the
adjacency scan minted a second crossing for the same seat, landing the traveler
at the destination twice. Abort and acknowledge are posted and never waited on.
A commit answered `WorldTransferStep.Unreachable` is IN DOUBT, never a refusal —
the source keeps its recovery state and reconciles against the destination's
idempotent status.

## Verification

Run the focused laws after changing frames, handoff continuity, hysteresis, or
contact sweeping:

```text
dotnet test tests/Puck.World.Tests/Puck.World.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorldAdjacencyLawTests|FullyQualifiedName~WorldAdjacencyCornerContactLawTests|FullyQualifiedName~FederationTransferLawTests|FullyQualifiedName~MappedArrivalApplicationLawTests|FullyQualifiedName~HighSpeedGroundContactLawTests"
dotnet test tests/Puck.World.Schema.Tests/Puck.World.Schema.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorldFrameIsometryLawTests"
```

Run `puck canary seamless-adjacency` for the automatic crossing and stationary
real-path proof, and `puck canary quilt-nw-gap-corner-strip` for the four-way
corner specifically: quilt-nw-gap drops both of NW's direct adjacencies, so a
body resting past NW's own east and south edges — where the local field is
identically absent in both legs — is grounded only when at least one direct
edge still delivers the corner, isolating that continuity from local geometry.
Run the stronger topology stress proof directly:

```text
pwsh -NoProfile -File docs/verification/four-corners-sharded/run.ps1
```

That runner starts five distinct loopback authorities (four ground worlds and
the floating island) and must prove horizontal and vertical handoffs, one full
four-ground-world human circuit, vertical human probes, retained dual-stick and
held-button input, routed camera and query state, cross-host body contact,
autonomous travellers, derived diagonal peers, and zero wire errors. `play.ps1`
opens the same five-authority topology for owner playtesting; visual feel
remains owner acceptance, not a headless substitute.

For federation transport changes, both sides need the same
`--federation-key-file`; inspect both stdout and stderr. Authentication must
precede observe, reserve, commit, status, intent, and submission operations.
Omitting the federation key disables federation by name without changing the
ordinary admitted-player socket.
