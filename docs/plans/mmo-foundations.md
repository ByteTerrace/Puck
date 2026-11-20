# MMO foundations: a closed design round

The round that asked what each established MMO answer becomes when every zone,
character and server is a world with its authority placed per world. It is
closed: its decisions live in
[the decision register](../decisions/worlds-and-federation.md), its order of
work in the plans and work packets the register's
[building it](../decisions/worlds-and-federation.md#building-it) names, and
three calls stay with the owner, deferred. The records below are the evidence
the register cites.

They are records of a round, not plans: each is a snapshot that carries no
queue and no open owner call, and where one disagrees with the register or the
code, the register and the code win. In particular the reports written before
the synthesis (`attacks.md`, `custody.md`, `economy.md`, `persistence.md`,
`seams.md`, `puck-today.md`) list open questions and owner calls as they stood
then; the register settles or defers every one, and the three still deferred
are the only ones open. The records stay while the plans they feed (the hub,
the custody protocol, disclosure, remote home adoption) have open steps.

## What the owner said

- Puck exists to make it possible, eventually, to build an MMO at the scale of
  World of Warcraft that redefines the genre the way WoW did. The emulators and
  the screen-within-a-screen work were envisioned as seamless parts of that
  MMO. [AGENTS.md](../../AGENTS.md#pucks-purpose) records the purpose and how
  every design round weighs it.
- Portals are meant to solve many of an MMO's hard problems.
- Local games reveal the MMO ([the ruling](../decisions/play.md#the-game)). A
  player starts in local games on their own machine, and finishing them
  reveals that the MMO exists; the same worlds then run against a hosted
  server. So every world runs well both locally and from the server, and the
  hosted deployment's scale (interest, deltas, per-row stepping, durable
  storage) is ordinary work now, not a later phase. The ruling came after the
  round closed and changes none of its decisions; it puts the move from a
  local authority to a hosted one into a player's first hours, so the custody,
  seam and storage decisions are exercised from the start.
- The card and board game systems exist to satisfy the "casino fantasy" that
  many RPGs implement poorly.
- Puck's world model (every zone, character and server is the same kind of
  world, with its authority placed per world) changes how nearly everything is
  implemented. Remote servers can and will be authoritative; escrow and the
  related concepts exist because of the tension between worlds of different
  authority.
- Visible portals are the wrong default for shards and layers; hidden seams may
  fit them.
- The line between client and server is blurred in Puck because of federation.
  Direct player-to-player interaction, if implemented, is in effect raw PvP.
- Azure and Entra are one provider, not the design: identity, issuance, key
  lifetimes, revocation, storage and hosting sit behind Puck's own
  abstractions, so Puck could move off Azure. `AGENTS.md` records this as
  "Puck is provider-independent".
- Fable takes a full pass to critique the design and find ways to attack it.
- The design is done properly before anything is committed: a full session that
  reaches consensus, with the homework done.
- Asked whether all fifteen calls the memo first put to the owner were truly
  the owner's, the round found twelve settled by rulings already in
  `AGENTS.md` and the world model; the owner deferred the other three.

## What the round asked

The records answer these questions and test these hypotheses, so they stay
here as the premises the records cite.

1. The engine's purpose, as the owner wants it recorded, and how every design
   round weighs it.
2. Player state across author edits: what survives an edit to an identity
   template, a zone or a shared world, where tuning lives, and how an author
   deliberately breaks continuity.
3. Custody of owned things: what generalizes from companions to characters,
   items, table stakes and machine saves; who is custodian when a title
   authority is unreachable; how a crashed place or an inexact image resolves
   custody; how epochs stay monotonic across restarts.
4. Value across trust boundaries: which classes of value an authoritative world
   keeps records of, which travel as claims, and where escrow and attestation
   must hold.
5. Seams: which boundaries are visible portals and which are hidden, how a
   portal runs without a frame, and how interest management maps onto
   disclosure tiers.
6. The tables: escrow for stakes, provable fairness, hidden information, atomic
   wagers, and the regulatory line if stakes ever touch real money (the owner's
   call when the economy is designed).
7. The order of work around the answers.

The hypotheses raised in conversation, as the lanes tested them:

- Player state carries across an edit by default; content templates hold tuning
  and state holds references; an authored reset epoch is the deliberate break;
  dropped player data is set aside where it can be recovered; per-row restore is
  the common path, since every engine build invalidates an exact continuation.
- Custody: title at home, a lease or escrow wherever the thing is, monotonic
  epochs as fencing, a crossing as the custody transaction, a place never
  asserting custody from an image, and recovery by presumed abort and recall.
- Seams: hidden for shards and layers, visible where the fiction is travel.
- Tables: stakes as escrowed custody; fairness by replay.

## Where the decisions live

- [Worlds and federation: decisions](../decisions/worlds-and-federation.md):
  the classes of value, the settlement door, custody and the active seat, what
  a visited world may write, state across edits, seams, the tables, contested
  places, revocation, player hosts and the abstractions.
- [Worlds and federation](../architecture/worlds.md): the model and what the
  code does today, with the intended design linked from it.
- [The companion decision](../decisions/companions.md): the custody rule the
  register refines with the lease-missed recall.

## The deferred calls

Each comes back on its condition, listed in
[owner decisions](open-items.md#owner-decisions); nothing in the order of work
waits on them.

1. The casino product, when the economy's design exists.
2. The scope of hidden dealing, when per-seat table sessions and the settlement
   door have landed.
3. How the standard's layer move feels, when the layer mechanism exists.

## What later readings of the code showed

A read of what limits one remote authority at scale, made after the round
closed, contradicts none of the register's decisions. It extends several: the
register decided the shape, and the code does not have it yet. Each follow-up
is filed in open items'
[transfers, federation and server](open-items.md#transfers-federation-and-server)
packet (its scale items) or in the plan named, and the register's
[building it](../decisions/worlds-and-federation.md#building-it) list does not
yet route them.

- **Interest management and deltas.** The register maps interest onto the
  disclosure tiers: `Presentation` is the recipient view's rows plus a body set
  under radius, hysteresis and cadence tiers, joined with a spatial grid
  ([seams](../decisions/worlds-and-federation.md#seams)). The code sends every
  active body to every session each tick (`WorldDocument.BuildSnapshotCore`,
  `WorldOutputHub`), redacts per sink by a linear scan
  (`WorldOutputHub.Redact`), and has no deltas, so its cost is sessions times
  bodies every tick. This is the decision unbuilt, not a different answer. Two
  plans hold parts of it: the disclosure plan's I1 item folds the grid,
  hysteresis and the unembodied observer into the recipient view
  ([disclosure, open items](disclosure.md#open-items)), and open items' scale
  item adds the per-sink interest set, deltas against an acknowledged
  baseline, and one encoding per audience.
- **Whole-document delivery.** The register gives only `Replica` the whole
  disclosed document. The code delivers the whole definition to every sink on
  any shape change (`WorldDocument.DeliverPending`), serializing and hashing it
  per sink below the replica tier (`WorldDeliveryCursor.DeliverDefinition`)
  and embedding audio inline. Row-level delivery is open items' scale item and
  the [authoring loop's step 4](iteration/authoring-loop.md#build-order).
- **One thread per silo.** The register's hosting abstraction promises a
  place to run with a durable crossing log, a checkpoint cadence and a fenced
  activation, and says nothing about concurrency. The silo steps every row in
  turn on one thread, each under its authority gate for its whole step
  (`WorldSiloSimulation.Step`, `WorldServerStepShell.Step`), though rows never
  observe one another. Per-row stepping is the job system's
  [T4](engine-job-system.md#6-build-order).
- **Checkpoint stalls.** The cadence arms every row on one master step, and
  each captures and encodes its whole image under its gate
  (`WorldSiloHost.CaptureRowsArmedByCadence`, `TryCaptureRow`); only the upload
  is asynchronous. The cadence the register asks for holds, but its cost grows
  with the silo's total state. Staggered rows and encoding off the step are in
  open items' scale item and T4; incremental checkpoints have no design yet.
- **Credential checks.** The synthesis records that the deployed silo
  re-validates a bearer token each second. That fact changed: a connection's
  verdict is cached (`WorldPeerHost`'s `Credential`), a timer sweep each second
  refreshes issuer trust off the tick, and a proof is verified again only when
  the admission policy or the issuer's trust changes or its window ends. No
  decision rested on the old behaviour; the identity abstraction still wants a
  signature over the challenge, not a bearer token.
- **Hard caps.** A world holds at most 4096 bodies
  (`WorldBodiesLimits.CapacityCeiling`, sized with the client's per-entity
  view arrays), a host at most 256 authenticated federation connections
  (`WorldPeerHost.MaximumFederationConnections`), and a server's mailbox is one
  unbounded channel with no quota per connection. The register settles no
  population ceiling; its layers and spatial shards are how a zone grows past
  one world, so the caps fit it. What decides each cap, and a per-connection
  ingress bound that refuses by name, is open items' scale item.
- **Authority storage.** The register's storage abstraction asks for fenced,
  conditional writes and an append that answers only once the store knows the
  outcome. The silo's journal now publishes immutable segments through the
  fenced root's compare-and-swap at tick and crossing boundaries, retires
  superseded payloads, bounds its backlog by entries and bytes and refuses a
  mutation by name when it is full (`WorldSiloHost.PrepareJournalMutation`),
  and keeps a deferred checkpoint armed. One gap against the abstraction
  stands: an ordinary edit is answered as applied when the tick drains it
  (`WorldTick`'s pending-operation completion), before its segment lands, and
  a failed segment only blocks later publication (`PersistenceBlocked`). The window is bounded but not zero. Scarce value is
  meant to settle at the settlement door instead, which is not built, so
  whether an edit's acknowledgement must wait for durability is a question for
  the [settlement packet](open-items.md#settlement-trust-roles-and-revocation),
  and no plan owns it yet.
- **Transfers.** A federated transfer no longer blocks the shared tick thread:
  the source cohort is held while reservation, companion authorization, commit
  and route discovery await the peer, and the reply applies on the row thread
  at a tick boundary (`WorldRemoteAuthority`). That is the crossing as one
  custody transaction the register decided, without the stall a hung peer used
  to put on every row.

## Records

- [Synthesis](mmo-foundations/synthesis.md): the decisions with their options,
  evidence and attacks, the classes of value, the abstractions, the debug-world
  checks, the order of work, what would change the answer, and every review
  finding answered.
- [Attacks on the synthesis](mmo-foundations/synthesis-attacks.md): the second
  attack pass, on the favoured composition as if it were built.
- [The attack catalogue](mmo-foundations/attacks.md): the first attack pass, on
  the world model as the code builds it.
- [Persistence and live ops](mmo-foundations/persistence.md) (lane A).
- [Distributed custody and trust](mmo-foundations/custody.md) (lane B).
- [World seams](mmo-foundations/seams.md) (lane C).
- [Economy and table integrity](mmo-foundations/economy.md) (lane D).
- [Puck as it is](mmo-foundations/puck-today.md) (lane E).

---

[Plans](README.md) · [Decision register](../decisions/worlds-and-federation.md) · [Open items](open-items.md) · [The world model](../architecture/worlds.md)
