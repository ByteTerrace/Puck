# Companions across instances

A companion is an autonomous body that belongs to a player and goes where the
player goes: the debug world's faerie is the first. When its player crosses a
portal into another instance, something has to drive the companion there, its
state has to survive the trip, and it has to come home as itself. This decision
settles who drives it, what travels, and what happens when a world does not
take it.

The design weighs author and player concerns, and the owner answered the
product questions.

## The owner's rulings

- Companions are not networked players: an instance's `networkPlayers` cap
  counts remote humans only.
- A companion is the player's. It travels with the player's identity, into
  other people's worlds too.
- It behaves the same by default in every world. A host world may substitute
  its own behaviour only when its author declares that on purpose.
- Separated from its player, it goes home. A world that does not take it sends
  it home and tells the player (the owner's direction, adopted as the engine's
  rule below).
- Federation stays in scope: a companion crosses into a world on another server
  as it does into a colocated one; the design may not cut remote crossings to
  avoid their trust and return problems.

## Code never crosses; the destination decides what runs

A crossing companion carries data only: its role, the mobility identity of the
player it follows, its identity projection with its durable state, its named
action registers, and the identity of its preferred driver (an addon pinned by
hash). It never carries executable code, add-on memory, or a stream of control
from home.

The destination decides from its own document. If it accepts visiting drivers
for the role, or mounts the same pinned driver, that driver runs the companion,
and the companion behaves exactly as at home. If its author declares a different
driver for the role, that one runs instead, by the author's choice. If it
declares nothing for the role, it refuses the companion at reservation.

The alternatives are rejected: migrating the driver's code puts
code from another machine in a world that never chose it and needs a
checkpoint format for add-on memory that ordinary checkpoints refuse; driving
it from home makes it a Live traveller, which counts as a human under the cap
rule, ties its movement to the link and needs the link's deliveries on the
replay tape.

### A world may accept visiting drivers by policy

The driver could travel with the body: otherwise every world that wants
companions duplicates a particular addon's pin, and a companion is inert or
refused wherever an author did not. The design keeps both halves: a world's role
declaration has a fourth choice, `accept`. A world that accepts runs the
visitor's own pinned driver, resolved by its hash from the content store and
verified, mounted in the world's own addon runtime under that world's fuel,
budget and grants (Drive over the arriving companion only). The author still
chooses, so no world runs behaviour it never agreed to; a companion is welcome
in every world that accepts, with nothing duplicated; and because lasting state
lives in registers, not in add-on memory, nothing new has to be checkpointed.
The Puck standard package (`worlds/standard/`) declares `accept` for the
companion role, so every world that imports it takes companions as they are.

## A world that does not take it sends it home

A companion is only ever where a world has said it may be; otherwise it is
home. A destination that declares nothing for its role refuses it at
reservation, before anything departs, and the player is told (a HUD notice on
the player's own crossing). The same rule covers a companion left behind when
its player crosses without it, and a player who leaves the game while their
companion is away: it returns to its home placement.

This is the healthy rule for the engine because it has one answer everywhere:
no world runs behaviour its author never chose, no companion is stranded, and
no generic follow behaviour has to exist for worlds that never asked for it.

## Home takes it back as itself

Home re-adopts a returning companion by its mobility incarnation into its
original placement, with that placement's kit, look and driver, whatever body
slot the escrow lands it at. While the companion is away its placement counts
it as present, so a reload at home does not spawn a replacement. Grants that
drive a companion name its placement or role group, never a body slot.

## A stranded companion waits in custody; only home recalls it

A companion is stranded when it must go home and cannot: home is not running,
its server is unreachable, or the link is down.

An owned traveller has a *title* (that it exists and whose it is, held by its
home), an *image* (its durable identity state and registers) and *custody*
(which authority embodies it now, at which epoch). A crossing moves custody,
never title. Across a partition nobody can tell lost from slow, so elapsed time
is never evidence of loss. The invariant the engine never breaks: at most one
committed embodiment of an incarnation at any point, no silent re-issue of its
title, and no destruction of its committed state without a record from an
authority entitled to make it.

- **The custodian sheds the body, never the custody.** A stranded companion
  stays embodied for an authored grace window, then detaches into a durable
  return record (an in-doubt departure toward home) and frees its slot, so the
  world holding it can reap or stop. The record is retried until home settles
  it; the custodian never drops it on a timer.
- **Home never refuses its own returning companion for capacity.** Its away
  entitlement holds the slot, and home may accept custody before it has room to
  embody it.
- **Only home ends or recalls a title.** Recall restores the companion from its
  last committed image under a new epoch and tells the player what was lost.
  It is safe only because home authorizes every custody hop: no onward crossing
  or return commits without home's record, so a stale copy that later
  reappears is refused and retired at its custodian, never duplicated or
  merged. Home never recalls on silence while the owner is also unreachable,
  since they may be together in a partitioned world.
- **By default it waits.** The engine's default is to wait in durable custody;
  a recall happens by the player's request or an author's declared recall
  window. The standard package declares that window as the margin by which a
  custodian may miss its lease, so waiting forever is never the standard's
  answer ([lease-missed recall](worlds-and-federation.md#recall-after-a-missed-lease)). Refilling with a new identity happens only for a traveller its author
  declares a disposable role (a summon, a hireling, an escort), and it is
  named to the player as a different individual.
- **Every transition is a record** made by the deciding authority (departure,
  detach, settlement, recall, release), every window counts that authority's
  own ticks, and replay re-drives taped outcomes rather than re-deciding them.

The rule is the engine's for any owned traveller (items, mounts, vehicles,
summons), not the companion's alone. The redesign that builds it carries the
records, the fence and the laws. A scarce traveller whose title sits at a
settlement domain takes that domain as its home, with the player's host as a
custodian ([bodies under custody](worlds-and-federation.md#bodies-move-under-custody-and-the-active-seat-is-the-domains-right)).

## State that persists is declared

What a companion must remember across crossings (its name, mood, affinity,
progress) lives in its durable identity state and named registers, which travel
and are checkpointed. Add-on private memory does not travel; a driver that keeps
decision state only in its own globals restarts that state on arrival, so a
driver keeps its lasting state in registers.

## Trust, determinism and cost

The receiving side classifies what it admits from what it can verify (the
cap-trust rule): an arriving companion is autonomous, lands with no grant from
its sender, and receives Drive from the destination's own policy, scoped to the
landed generation. Home mints a companion's incarnation with a title mark that
is part of its identity, so a sender cannot pass the companion off as an
ordinary traveller: a marked incarnation lands only under a hop home confirms,
and an unmarked copy is another incarnation home holds no title of. The destination's replay tape pins its own add-on receipts,
so a replay of the destination needs nothing from home. One driver instance per
role per instance drives every arriving companion of that role, under that
world's fuel and budget; placement capacity bounds how many a world takes, and
a full world refuses at reservation.

## Building it

The faerie's lasting state moves into registers, and its driver grants are
placement- or group-keyed rather than `body:42`. The
[companion protocol plan](../plans/companion-redesign.md#9-build-plan) owns the
remaining custody transitions, lifetime handling, and debug-world trip; the
[Transfers, federation and server packet](../plans/open-items.md#transfers-federation-and-server)
ranks that work.
