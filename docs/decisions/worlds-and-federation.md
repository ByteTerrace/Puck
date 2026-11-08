# Worlds and federation: decisions

The choices behind trust, custody, settlement and seams between worlds, as the
[MMO foundations round](../plans/mmo-foundations.md) settled them. Each entry
states what was decided, the reasoning that still holds and the evidence it
rests on; [the synthesis memo](../plans/mmo-foundations/synthesis.md) holds the
options weighed, the attacks each closes and the review answers. The current
model, and what the code does today, is owned by
[Worlds and federation](../architecture/worlds.md); this page owns where that
model is going. The work these decisions order lives in
[open items](../plans/open-items.md#settlement-trust-roles-and-revocation) and the plans
named under [building it](#building-it).

Every decision here weighs its question at the scale of
[Puck's purpose](../../AGENTS.md#pucks-purpose): an MMO the size of World of
Warcraft in which every zone, character and server is a world with its
authority placed per world. Twelve questions the first revision of the memo
put to the owner proved settled by rulings already in `AGENTS.md` and the
world model's invariants; each is stated below with the ruling it rests on.
Three stay the owner's, and the owner has deferred them
([deferred calls](#the-owners-deferred-calls)).

## Three answers for anything worth fighting over

**Decided.** Every class of value takes one of three answers, chosen by the
consequence of copying or falsifying it, never by its schema name: the issuer
keeps the record and escrow moves it; a claim travels signed with its
provenance; or an authority neither party controls decides. What nothing
protects stays the player's own unsigned data.

| Class | Answer |
|---|---|
| Currency and stackable goods | Issuer-kept record moved by escrow; units are created only at the settlement door, and an award by any other authority is a unique redemption against a budget the door delegated |
| Unique tradeable items | Issuer-kept record (title at the issuer) moved by escrow and custody |
| Table stakes | A reservation at the settlement door, every input of a hand under one domain, one terminal decision per hand from the vouched table authority the reservation names |
| Reward paid for a claim (a first clear's chest) | Signed evidence plus an issuer-kept redemption id the door consumes once within a named redemption domain |
| Progress a world gates on (clears, unlocks, levels) | Signed claim with provenance from the world that granted it |
| Reputation | Collections merge per issuer by the reader after its own filter; current standing is an issuer-versioned claim the reader replaces by the newest version, so a penalty takes effect |
| Appearance, ungated progress | Unsigned; an issuer-kept entitlement only when scarce or sold |
| Machine save RAM | Unsigned, forkable personal data; a competitive result exists only when the run executes on an authority the player does not host |
| Companions and characters | Bodies under the custody protocol's title and custody ([below](#bodies-move-under-custody-and-the-active-seat-is-the-domains-right)); the character's identity document is the player's and holds references, signed claims and receipts |

**Why.** A signed balance is ruled out by the attestation reference
([ruled out](../reference/attestation.md#ruled-out)), a copied non-exclusive
claim gains nothing, and one body per incarnation says nothing about supply.
The table is a proposal for the standard package to author, never an engine
enum: the engine ships mechanisms and the game supplies names (the world
model's invariant 3).

**Evidence.** [Synthesis, classes of value](../plans/mmo-foundations/synthesis.md#2-classes-of-value);
[economy, question 4](../plans/mmo-foundations/economy.md#question-4-and-attack-question-1-classes-of-value);
[custody, question 4](../plans/mmo-foundations/custody.md#round-question-4-and-attack-question-1-classes-of-value).

## Scarce value settles at one door per domain

**Decided.** Scarce value lives as issuer-kept records behind one
transactional settlement door per settlement domain, at an authority the player
does not host. The door is a world like any other, chosen per trust community,
small, and replaceable, while zones, characters and tables stay where their
authors put them.

- **The invariant.** Within a domain, every scarce right is available, held for
  one unresolved transaction, transferred, consumed or destroyed, and no two
  accepted effects consume the same right and version.
- **The one enforcement point** is the door's commit: it authorises, compares
  the fence and deduplicates in one durable write, and alone creates units.
  Every other authority's award draws a budget the door removed from
  circulation before delegating it, so an award id deduplicates a retry and
  the budget bounds a re-issue.
- **Receipts.** Every terminal decision is a signed receipt (issuer, subject,
  operation id, prior and next version, outcome) delivered to each party, held
  on a row that refuses when full and never evicts, or pulled from the door
  when the holder is offline.
- **Restore.** Economic decisions, consumption and refusal high-water marks sit
  outside gameplay rollback; a restored door reconciles against its
  counterparties' receipts or withdraws its namespace.
- **Where the guarantee stops.** At authorities the domain does not accept, at
  effects that bypass the door, and at the issuer itself: an issuer that
  equivocates or restores its own store is detected by two receipts for one
  right and version, never prevented, because prevention needs a common final
  decision outside the issuer.

Nothing enforces this today: the companion reducer fences bodies, not value,
and no spend door exists.

**Why.** A duplicate of anything scarce is the one MMO failure that cannot be
repaired locally (CCP's node failure duplicated ships despite a reinforced
node), and a settlement world is the one option that closes double spending by
a dishonest home and the crash duplicate for value, once every reward and spend
uses its door. It reintroduces a
centre only per trust community, which "trust is declared per world instead of
assumed" ([the idea in plain terms](../architecture/worlds.md#the-idea-in-plain-terms))
allows; a friends-only world that wants no centre runs forkable personal
worlds and settles nothing.

**Evidence.** [Synthesis, decision 3](../plans/mmo-foundations/synthesis.md#decision-3-custody-of-owned-things)
and [the Settlement abstraction](../plans/mmo-foundations/synthesis.md#3-abstractions-not-providers);
[custody, question 3](../plans/mmo-foundations/custody.md#round-question-3-custody-and-the-hypothesis);
attacks B1, B2 and B5 in [attacks on the synthesis](../plans/mmo-foundations/synthesis-attacks.md).

### Settlement domains are sealed

**Decided.** No scarce right issued in one domain is accepted in another: a
hand's or a trade's inputs are all reserved under one domain before play, and
the door refuses a mixed-domain operation by name. Exchange between economies
is a later round's mechanism, picked from lane B's three (a common escrow
authority, independent ledgers with enforceable conditional exchange, or
limited bilateral credit).

**Why.** A domain is a key, and anyone with a few accounts can name one and
mint honestly by its own rules. The world model's invariant 5 settles who
decides: "Meaning is bilateral. The engine never adjudicates trust", so the
engine never says one economy is the economy, and acceptance stays the
reader's policy.

### Neutrality is a vouched role

**Decided.** A dealer, a referee, a witness, an issuer and the holder of a
delegated budget are roles a settlement domain grants to a namespace, recorded
as data the door checks; the door accepts a hand's terminal decision, a fight's
outcome or a silence attestation only from an authority vouched for that role
and named in the reservation before the hand or fight begins. Who may be
vouched (the studio's authorities only, operators a root vouches for, or any
community that names them) is each domain's declared policy.

**Why.** The only verified fact about an arrival is its namespace, and a seated
player with a second key file can host a "neutral" table. Invariant 5 leaves
the role list to each domain; the wider the list, the more trust rests on
reading who vouched whom, which is the domain's cost.

### A failed or cheating issuer

**Decided.** The engine fails closed: an equivocation proof or a namespace
withdrawal freezes that issuer's rights, and the door refuses them by name.
What follows (honour the rights to the last receipt counterparties hold, void
them, or split the loss) is the domain's authored reconciliation, run by the
door after the freeze.

**Why.** Signatures cannot choose a reconciliation, and invariant 5 keeps the
engine out of adjudicating trust.

### Tradeable or bound

**Decided.** Each item class declares its transfer; the engine default is
bound, so a sale of a class that declared no transfer is refused by name. The
standard package picks its values.

**Why.** "Authors rule" in `AGENTS.md`: a rule of play is an authored
parameter with a neutral engine default, and the fail-closed default is the
neutral one.

## Bodies move under custody, and the active seat is the domain's right

**Decided.** Bodies (companions as built, characters at the hub plan's H14)
move under the custody protocol's title, custody and epochs. Two changes the
attack pass forced:

- A scarce companion whose title sits at a settlement domain has that domain as
  the protocol's home; the player's host is a custodian, and recall is the
  issuer's decision at the owner's request, so one right is never split across
  two authorities.
- Where a settlement domain requires single presence (ranked tables, scarce
  drops, time-gated rewards), the active seat is a domain right: one active
  incarnation per identity per domain, consumed at each landing and released at
  departure. Outside any domain, parallel presence is accepted and earns
  nothing settled.

**Why.** A home that titles its own body is the attacker in the self-owned
character, and it could confirm one incarnation into many worlds at once; only
an authority the player does not host can hold the seat for value.

**Evidence.** [Synthesis, decision 3](../plans/mmo-foundations/synthesis.md#decision-3-custody-of-owned-things);
attacks B4 and B17 in [attacks on the synthesis](../plans/mmo-foundations/synthesis-attacks.md);
[the custody protocol](../plans/companion-redesign.md#1-vocabulary).

### Recall after a missed lease

**Decided.** A title whose custodian has missed its lease by a margin the
standard package authors is recallable by the title authority. The same rule
covers a home restarted without its checkpoint. A twin left at a dead
destination can earn nothing settled and issues claims only in that host's
namespace.

**Why.** Under "wait forever", a host with a `*` admission row traps every
character that visits it, and because the twin earns nothing settled, the
second body costs nothing. The two questions of waiting or recalling are one
mechanism, so it is engineering, not an owner call. It refines the
[companion decision](companions.md#a-stranded-companion-waits-in-custody-only-home-recalls-it):
the engine default still waits, and the standard package authors the recall
window.

### An unreachable title authority, a crash and an inexact image

**Decided.** An unreachable title authority has no automatic substitute: a
committed custodian keeps its rights within their scope, a pending move stays
pending, and a new exclusive grant waits. A crashed place or an inexact image
never asserts custody from its image; it asks the title authority for current
rights before enabling economic actions, and the world starts with the
affected value doors frozen, narrating why. Refusing to start is the stopgap.
The freeze is reviewed as a door of its own when the settlement door is built.

**Why.** Every studied service froze the doors that move the affected value
until it was resolved, and New World's freeze opened a new duplication path,
so the frozen door gets its own review.

### Epochs survive restart

**Decided.** Epochs stay monotonic through one durable allocator per title
(the authority store already models one for activations,
`WorldAuthorityBlobStore.AcquireActivationAsync`), and any desktop that is a
destination keeps a durable crossing log and durable images, as the silo keeps
`RowCrossingLog`. When one host both restores images and spawns resolver
instances, its generation identity is qualified so a restored generation is
never confused with a spawned one.

**Why.** Epochs fence only with a durable allocator and a comparison at every
accepted effect; a desktop without a log loses its consumed epochs at a crash.

## What a visited world may write

**Decided.**

- **One write door.** A visited world writes a visitor only in its own package
  namespace and never a home-scoped class, through one authorisation on the
  doors rule effects and the console share (`WorldOwnedWorlds.TrySetFact`, which
  `WorldRuleHost.PersistIdentityFact` and `identity.fact.set` reach) and on
  `Decide`, keyed by the executing world's document and package.
  `WorldIdentity.TryAdopt` applies the same rule to what a body carries home
  from another host.
- **Write-through as earned.** A progress award goes home as a pending claim
  with a unique award id, behind a durable award commit (award id, credited
  effect and outcome in one write) whose receipt distinguishes applied, durably
  accepted, pending and uncertain. A settled class goes to the settlement door
  as an operation, and home holds the receipt. A displayed award is pending
  until the accepting authority acknowledges it.
- **Remote write-back is signed.** Across machines, adoption is built as signed
  submissions over (authority, package, fact), never as the unsigned adoption
  colocated seats use; a reader's issuer policy maps an authority namespace to
  the packages it may write, refuses a package with no authority mapped, and is
  the identity map in process.
- **Whose level.** A level can be one issuer's record, a per-world claim readers
  filter, or the player's own number; the engine supports all three and the
  standard package authors the answer.

**Why.** A namespace is policy over who may write what, home scope is policy
over what may be written abroad at all, and a signature is provenance a third
reader can check; dropping one reopens an attack the other two do not reach.
The package is the namespace, so one package's worlds share it by design. The
signed form binds the package to a key because a package name is an authored
string and the namespace is the only verified fact about a federated arrival.
"A level is a durable counter someone called a level" (invariant 3), and the
standard is authored, never baked in.

**Evidence.** [Synthesis, decision 4](../plans/mmo-foundations/synthesis.md#decision-4-value-across-trust-boundaries);
[persistence, question 8](../plans/mmo-foundations/persistence.md#attack-question-8-what-a-visited-world-gets-to-write);
[custody, question 2](../plans/mmo-foundations/custody.md#attack-question-2-when-progress-becomes-durable);
attacks A1 to A3 in [the attack catalogue](../plans/mmo-foundations/attacks.md#a1-the-self-owned-character).

## Player state across author edits

**Decided.** The engine carries a character's saved state across an edit that
keeps a row's declaration. The identity holds template ids and per-instance
values, tuning is read from the template where it is used, and an undeclared
row drops by name through the image ladder's row-by-row admission rather than
setting the whole identity aside. A row whose meaning changed takes an
authored `.puck` conversion keyed by the template version, the only path for
it; a conversion is a pure function of the stored document, the template
version and the program, so it replays. A refund of a settled class is a
settlement operation keyed by (identity, conversion id), so it never runs
twice. The only literal reset is an epoch realm: a new world beside the
permanent one, merged back by a conversion. Whether a world resets its
characters at an edit is its author's choice.

How long set-aside data (a dropped row, a refused image, a quarantined
identity) is kept, and who may restore it, is an operator setting with a
neutral engine default and a restore verb.

**Why.** Every studied game keeps tuning in templates; copying the template at
seed fails at the first retune. "Authors rule" makes carrying the neutral
default and the conversion the author's program, and keeps retention, an
operating policy, out of engine design.

**Evidence.** [Synthesis, decision 2](../plans/mmo-foundations/synthesis.md#decision-2-player-state-across-author-edits);
[persistence, question 2](../plans/mmo-foundations/persistence.md#round-question-2-player-state-across-author-edits);
[durable images](../plans/hub-and-saves.md#durable-images).

## Seams

**Decided.**

- Seams are hidden for spatial shards (`WorldAdjacency`) and population
  layers, and visible portals where the fiction is travel or a change of
  scope.
- A population layer moves only under an unobservability predicate the layer
  authority evaluates over its own state (no combat, no channel in progress, in
  a transition volume or by a prompt), with an increasing hop cooldown the
  layer set's authority keeps per identity subject, never read from the
  mobility record the source writes; a party's hop is gated on its slowest
  member. The predicate gates a mechanism the engine still needs: layer
  ordinals under one destination in the resolver and the durable identity, and
  a deferral queue in the transfer door.
- A frameless portal is authored, not built: a `threshold` template in the
  standard package, a box face whose aperture the glass fills with no frame.
- Interest management maps onto the disclosure tiers: `Frames` is the camera,
  `Presentation` is the recipient view's rows plus a body set under radius,
  hysteresis and cadence tiers, and `Replica` is the whole disclosed document
  for trusted shadows. Body disclosure joins the recipient view with a spatial
  grid, hysteresis, a window or camera anchor for an unembodied observer, and
  cadence tiers.
- Reactive spatial splitting is rejected; it contradicts "do not run an
  authority boundary through a place designed to be contested"
  ([scale and authority](../architecture/worlds.md#scale-and-authority)).

**Why.** Every studied game's hidden seams failed by moving under a live player
or by being hopped to farm or escape; Blizzard shipped framed-everywhere's
mid-cast vanish first and built the predicate as the fix. Framed transitions
everywhere remain a world's choice. How the standard's layer move feels is the
owner's deferred call.

**Evidence.** [Synthesis, decision 5](../plans/mmo-foundations/synthesis.md#decision-5-seams);
[seams, options](../plans/mmo-foundations/seams.md#3-options-for-puck).

## The tables

**Decided.**

- **An independent dealer first.** A hidden-hand table's authority is never a
  seated party, and a non-seated authority a seated player's account controls
  is seated. That is the standard casino's trust policy and the precondition of
  its hidden-hand guarantee, never an engine restriction: an open-hand,
  score-only game runs on any host. The engine makes the policy checkable
  through the vouched table role.
- **A seat's view is the seat's own session.** A seat's private view is a
  session the seat's own authority opens with the table, delivered to the
  seat's client by the table, never through the room; a room's screen shows the
  table's public tier. A screen in a player's room observes a nested
  destination as the room's authority (`WorldServer.TryObserveAsSession`), so
  the room's screen cannot be the casino.
- **Stakes** are reservations at the settlement door, every input of a hand
  under one domain, frozen with roster, buy-ins, rules identity, the table
  authority and recovery policy before a hand starts, with one terminal
  decision per hand id. A wager's debits and pot update are one operation or
  none, and recovery replays the same operation id.
- **Public draws** bind per-participant commitments before any is opened, taped
  as inputs, with an abort policy that gives the last revealer no free reroll.
- **Fairness by replay** needs rules and terms pinned, every accepted action in
  order, commitments witnessed before their dependent actions, agreement on one
  history and the settlement receipt; every seat keeps the public transcript
  and its own receipts apart from the host. Replay proves execution from given
  inputs, never that the inputs were honest or complete.
- **Mucked hands** stay secret or are revealed by the table's authored rule; a
  secret muck keeps a redacted transcript, which is not full replay
  verification.

**Why.** Today a draw's seed folds only the document seed, the instance
identity and the site descriptor, and the authority reads its own arena whole,
so the host sees every hand and chooses the dice. Encrypted dealing removes the
dealer's knowledge but not seated players' collusion, and both need the same
escrow and transcript policy, which the independent dealer proves first.

**Evidence.** [Synthesis, decision 6](../plans/mmo-foundations/synthesis.md#decision-6-the-tables);
[economy](../plans/mmo-foundations/economy.md#the-hypothesis-stakes-as-escrowed-custody);
attacks A4 and A5 in [the attack catalogue](../plans/mmo-foundations/attacks.md#a4-the-host-sees-every-hand).

## Contested places and fights between player hosts

**Decided.**

- A contested place runs on an authority neither combatant controls, bound
  before the first swing: a silo or a vouched referee world.
- The seam contact field is clamped as a defect fix: a neighbour's bodies and
  geometry move a local body only through `WorldPopulation.AdmitAdjacencyResponse`,
  within one per-body budget, behind every ownership face the body is still
  owned behind, and the pair's responder is chosen by the two authority
  identities alone (`WorldCrossAuthoritySettlement`). It narrows seam shoving
  rather than closing it: a neighbour can still carry a still body toward the
  plane up to its threshold, and its reported bodies are not proved to exist.
  Taped neighbour records close the replay boundary.
- Mutual deterministic simulation (both peers step a duel over signed per-tick
  inputs and compare hashes) is the Puck-native prototype for duels between
  friends. Its peers agree one history first, by lockstep commitments or a
  vouched witness that signs receipt times and silence; a mismatch is an
  invalid history until a referee's replay names the peer whose signed hash
  disagrees. It removes the host's simulation advantage, not the referee, and
  cannot hold hidden state.
- Whether two player hosts may fight without a witness is per-world policy.
  Nothing settled rides on an unwitnessed fight: a LAN duel is a friends'
  feature where a stall is a draw by prior agreement, and a fight with stakes
  or durable progress runs on a vouched authority.

**Why.** Invariant 1 (only a body's authority writes it) leaves only physical
contact with a cross-authority rule today, and scarce rights move only through
a door that accepts outcomes from vouched roles, so "Authors rule" can leave
the choice of fights to each world.

**Evidence.** [Synthesis, decision 8](../plans/mmo-foundations/synthesis.md#decision-8-where-a-contested-place-runs);
[seams, options](../plans/mmo-foundations/seams.md#3-options-for-puck);
[A10](../plans/mmo-foundations/attacks.md#a10-seam-shoving),
[B12](../plans/mmo-foundations/synthesis-attacks.md#b12-the-c2-witness-has-no-step)
and [B14](../plans/mmo-foundations/synthesis-attacks.md#b14-pinned-at-the-seam).

## Revocation below expiry

**Decided.** Short-lived bindings plus a signed refusal snapshot: a root
publishes a scoped, versioned list of refused keys, issuers and subjects, a
host persists its newest and records the version with each accepted decision,
and the peer host's existing re-verification on every policy change is the door
it plugs into. A revocation entry carries a not-after instant, so a stolen key
revokes what it signed after that instant and keeps the honest history before
it; a claim's acceptance time is the receiver's recorded tick-boundary
decision, never the claim's own date. External status becomes a recorded
decision so replay never refetches it. Key, issuer and subject bans are
separate, so rotating a key evades no account ban and revokes no historical
ownership. A stale snapshot refuses new valuable actions (spends, stakes, title
moves), never all actions. How long a world may act on a stale snapshot is a
per-domain setting with a neutral engine default.

**Why.** Attestation fetches nothing by rule, so a list must arrive as data;
moderation at MMO scale has no lever without revocation, and `Puck.Attestation`
has none. A short window makes bans bite fast and makes snapshot delivery a
single point of denial for the domain; the operator weighs that trade.

**Evidence.** [Synthesis, decision 9](../plans/mmo-foundations/synthesis.md#decision-9-revocation-below-expiry);
[custody, revocation](../plans/mmo-foundations/custody.md#attack-question-6-revocation-choices).

## What a player host may be

**Decided.** A friend's machine may host a world strangers visit. It keeps a
durable crossing log and images, so a crash loses position and not
possessions, and nothing a stranger would fight over is decided there: scarce
value and acknowledged progress settle at issuers not on that desktop, as the
visitor's own receipts. A player host's awards are claims in its own
namespace, counting where a reader's policy trusts that host; player-hosted
content feeds the main game only through a budget and a vouching the main
game's domain grants. A lost local disk reconciles from the surviving
settlement authority, and where no trusted record survives the rights are
unresolved, never minted again from an image.

**Why.** "Players hosting their own authoritative worlds is a goal"
([scale and authority](../architecture/worlds.md#scale-and-authority)), and
admission is each world's choice; the settlement invariant already keeps
anything scarce off a host the player controls. A host that wants its own
economy runs a domain of its own.

**Evidence.** [Synthesis, decision 10](../plans/mmo-foundations/synthesis.md#decision-10-what-a-player-host-may-be);
[custody, question 3](../plans/mmo-foundations/custody.md#attack-question-3-what-a-desktop-must-persist-and-what-it-proves).

## Abstractions, not providers

**Decided.** Each trust question is asked of an abstraction, and today's
provider is one implementation ("Puck is provider-independent" in `AGENTS.md`):

- **Identity** guarantees that an arrival's source is a namespace a chain under
  a trusted root vouched for, bound to this connection's nonces, transport keys
  and direction, and that the namespace is the only verified fact about it. It
  wants a signature over the challenge, which a bearer token echoed beside the
  challenge is not. A role is a grant from a settlement domain, never a
  property a namespace asserts, and a second namespace is never assumed to be a
  second person.
- **Issuance** guarantees a namespace's key was issued under a root the
  verifier pins, with a window the verifier may shorten, and never assumes the
  issuer is reachable at verification.
- **Key lifetime** guarantees every claim and binding carries a window the
  verifier enforces, with ceilings per entry, root binding and subject binding
  and a replay horizon; a null ceiling is never assumed safe.
- **Revocation** is signed versioned data consumed offline at one door
  ([above](#revocation-below-expiry)).
- **Storage** is durable, fenced and conditionally written: a write names the
  version it read, a stale writer is refused by name, an allocation persists
  before it is exposed, and an OS-buffered write is never assumed durable.
- **Hosting** gives an authority a durable crossing log, a checkpoint cadence
  and a fenced activation, and never takes one placement system's
  single-activation promise for an economic record of title.
- **Settlement** is [the door](#scarce-value-settles-at-one-door-per-domain).
- **Deadlines** are declared passed only by the authority that owns the right,
  as a recorded decision at its own tick boundary from a host clock it trusts,
  replayed as data; a party never declares its counterparty's deadline, and a
  paused player host's ticks never serve as a shared economic deadline.

**Evidence.** [Synthesis, abstractions](../plans/mmo-foundations/synthesis.md#3-abstractions-not-providers);
[Puck as it is, attestation](../plans/mmo-foundations/puck-today.md#3-attestation).

## The owner's deferred calls

Three questions stay the owner's, deferred until the systems they depend on
mature. Nothing in the order of work waits on them: the engine builds each
option as a mechanism, and an answer picks the standard package's values or
funds a bounded prototype. [Owner decisions](../plans/open-items.md#owner-decisions)
lists them with the condition that brings each back.

1. **The casino product**: table-local score, earned chips, purchased chips,
   tradeable stakes or real money, for which territories and ages. Table-local
   score takes the settlement door out of the tables' path; the last two rows
   need counsel before any schema.
2. **The scope of hidden dealing**: whether Puck builds dealing that hides
   cards even from the dealer (verifiable encrypted dealing). Which dealer a
   table uses stays its author's choice.
3. **How the standard's layer move feels**: a transition area out of combat,
   or a countdown in place with a rising cooldown. Both are authored data on
   one neutral layer mechanism.

What would reopen a decision here (a settlement latency the owner will not
accept, a working encrypted-dealing prototype, a desktop that fails to recover
crossings through a kill, a reproduction of parallel seating) is listed in
[the synthesis](../plans/mmo-foundations/synthesis.md#8-what-would-change-the-answer).

## Building it

The memo's [order of work](../plans/mmo-foundations/synthesis.md#6-the-order-of-work)
is filed where each step's cause lives, and each names its check in
[the memo's debug-world checks](../plans/mmo-foundations/synthesis.md#5-how-the-debug-world-proves-each-favoured-option):

- the write door, the durable award commit, identity recomposition and the
  desktop crossing log: [the hub plan's build order](../plans/hub-and-saves.md#build-order);
- the lease-missed recall: [the custody protocol's build plan](../plans/companion-redesign.md#9-build-plan);
- the seam clamp's residual rules, the neighbour replay packet, remote seating
  into a spawned place and the layer mechanism:
  [transfers, federation and server](../plans/open-items.md#transfers-federation-and-server);
- the settlement door, the identity abstractions and roles, revocation and the
  duel witness: [settlement, trust roles and revocation](../plans/open-items.md#settlement-trust-roles-and-revocation);
- per-seat table sessions and the tables with stakes:
  [card tables](../plans/open-items.md#card-tables-in-the-board-game-room);
- body disclosure in the recipient view: [disclosure](../plans/disclosure.md#open-items);
- signed remote write-back: [remote home adoption](../plans/records-and-pools.md#remote-home-adoption).

---

[Decisions](README.md) · [Worlds and federation](../architecture/worlds.md) · [Companions](companions.md) · [MMO foundations](../plans/mmo-foundations.md) · [Synthesis](../plans/mmo-foundations/synthesis.md)
