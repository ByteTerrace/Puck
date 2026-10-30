# Companions across instances

A companion is an autonomous body that belongs to a player and goes where the
player goes: the debug world's faerie is the first. When its player crosses a
portal into another instance, something has to drive the companion there, its
state has to survive the trip, and it has to come home as itself. This decision
settles who drives it, what travels, and what happens when a world does not
take it.

The owner asked for a design reached by consensus across the agent families,
weighing author and player concerns. Claude, Codex and Antigravity each drafted
a position independently; this record is the reconciliation, and the owner
answered the product questions.

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

All three drafts rejected the alternatives: migrating the driver's code puts
code from another machine in a world that never chose it and needs a
checkpoint format for add-on memory that ordinary checkpoints refuse; driving
it from home makes it a Live traveller, which counts as a human under the cap
rule, ties its movement to the link and needs the link's deliveries on the
replay tape.

### A world may accept visiting drivers by policy

Two of the three families, reviewing the open items after the first draft,
argued that the driver should travel with the body: otherwise every world that
wants companions duplicates a particular addon's pin, and a companion is inert
or refused wherever an author did not. The lead settled the dissent (the owner
deferred the design to the team) by keeping both halves: a world's role
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
landed generation. The destination's replay tape pins its own add-on receipts,
so a replay of the destination needs nothing from home. One driver instance per
role per instance drives every arriving companion of that role, under that
world's fuel and budget; placement capacity bounds how many a world takes, and
a full world refuses at reservation.

## How it is built

A world declares its roles in a `roles` section: a row names the role, either
`driver` (this world's own addon, which is the same pin when it matches the
visitor's or a substitute when it does not) or `accept`, and the kit and look an
arriving companion is dressed in. A world with no row for a role takes no
companion of it. The standard package (`worlds/standard/`) declares `accept` for
`companion`; the debug world, the faerie's home, names the faerie's own addon
instead, and the night field and the garden refine the standard's row with the
faerie's kit and look.

A companion is an inhabit facet holding a `role` and the local seat it
`follows`. It crosses in a transfer of its own right after its player's, through
the same door, carrying its role, its player's incarnation, its home and its
preferred driver's pin on the reservation member. Its home publishes the
driver's module bytes into the process content store as it leaves; an `accept`
destination verifies them against the pin at reservation and mounts them as an
addon row named by pin alone. The role, never a body slot, decides the driver's
grants: Drive and Observe over each companion of the role and Observe over the
player it follows, re-derived whenever the population moves.

A companion that arrives behind its player is set down just through the door
within its role's follow reach, so it never lands behind the destination's
door. Home re-adopts its own companion by incarnation into the slot and
placement it left, and counts it present while it is away.

Two parts remain open in the transfers packet of the
[open items](../plans/open-items.md#transfers-federation-and-server): a remote
`accept` world can resolve a visiting driver only from its own content store, and
the faerie keeps its decision state in guest memory because a guest has no way
to write its own body's registers.
