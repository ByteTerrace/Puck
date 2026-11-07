# MMO foundations: an open design round

Nothing in this page is decided. It holds the questions a full design session
must answer before any of them becomes a ruling, the round that answers them,
and the candidate positions raised so far, each marked as a hypothesis to test.
Only what the owner rules after the round is written into
[AGENTS.md](../../AGENTS.md), a decision page and the owning plans.

## What the owner said

- Puck exists to make it possible, eventually, to build an MMO at the scale of
  World of Warcraft that redefines the genre the way WoW did. The emulators and
  the screen-within-a-screen work were envisioned as seamless parts of that
  MMO.
- Portals are meant to solve many of an MMO's hard problems.
- The card and board game systems exist to satisfy the "casino fantasy" that
  many RPGs implement poorly.
- Puck's world model (worlds as worlds: one model for every zone, character and
  server, with authority placed per world) changes how nearly everything is
  implemented. Remote servers can and will be authoritative; escrow and the
  related concepts exist because of the tension between worlds of different
  authority.
- Visible portals are the wrong default for shards and layers; hidden seams may
  fit them.
- The design is done properly before anything is committed: a full session that
  reaches consensus, with the homework done.

## The frame: worlds as worlds

[The world model](../architecture/worlds.md#world-relationships) represents
zones, identities, alternate characters, hubs, games and what would elsewhere be
called servers with one document model. Where a world's authority runs (a remote
server, a silo, a player's machine) is a separate choice for each world, and
remote servers can and will be authoritative. An identity is a world; a zone
writes progress back to it by submission; a world vouches for a claim with an
issuer-signed slot another world carries; trust is bilateral, so the engine
carries proof and enforces capabilities and the receiving world's policy decides
whether a claim counts ([invariants](../architecture/worlds.md#invariants)).

Escrow, title and custody, and attestation exist because owned things move
between worlds whose authorities differ and trust each other differently. The
round studies how well those mechanisms resolve that tension at MMO scale, and
what each established MMO answer becomes when every zone, character and server is
a world rather than a row in one operator's database.

## Questions the round must answer

1. **The engine's purpose, as the owner wants it recorded**, and how every design
   round weighs it.
2. **Player state across author edits.** What survives when an author edits an
   identity template, a zone or a shared world, given that a character's state
   lives in its own world and zones write back to it. Where tuning lives (content
   templates the state references) and how an author deliberately breaks
   continuity. Relates to [durable images](hub-and-saves.md#durable-images) and
   its two owner calls.
3. **Custody of owned things.** Title, custody and epochs already exist for
   companions ([the custody protocol](companion-redesign.md#1-vocabulary)). What
   generalizes to characters, items, table stakes and machine saves; who is
   custodian when a title authority is unreachable; how a
   crashed place or an inexact image resolves custody; how epochs stay monotonic
   across restarts (the resolver's generation counter is not persisted:
   [hazards](../development/hazards.md)).
4. **Value across trust boundaries.** Which classes of value (currency, items,
   stakes, appearance, progress) an authoritative world keeps records of, which
   travel as claims, and where escrow and attestation must hold so nothing is
   duplicated or lost when a thing crosses between worlds that trust each other
   differently; the known limits at that edge (a copied claim, a dishonest host,
   a host that sees hidden state, an owner restoring an old copy) and how the
   existing mechanisms answer each.
5. **Seams.** Which boundaries are visible portals (instances, hubs, cross-realm
   travel, nested screens) and which are hidden (spatial shards through
   adjacency, population layers moved only when the change cannot be observed,
   groups kept together); how the portal mechanism runs without a frame; how
   interest management maps onto disclosure tiers.
6. **The tables.** Escrow for stakes, provable fairness from seeded streams and
   replay, hidden information under [disclosure](disclosure.md), atomic wagers
   under [rule faults](rule-faults.md), and the regulatory line if stakes ever
   touch real money (the owner's call when the economy is designed).
7. **Order of work.** How the hub steps (H1, H2, H7, H14), the custody protocol,
   disclosure and federation reorder around the answers.

## The round

| Lane | Family | Focus |
|---|---|---|
| A. MMO persistence and live ops | Claude (opens pages) | How WoW, FFXIV, EVE, GW2 and RuneScape store characters, migrate them across patches, separate templates from state, roll back after crashes and dupes. |
| B. Distributed custody and trust | Codex | Leases and fencing, two-phase commit and presumed abort, sagas, virtual actors, EVE's node failure, cross-realm transfer; escrow and claims that cross between authorities of different trust. |
| C. World seams | Antigravity (search; pages copied in) | WoW sharding, layering and phasing, the GW2 megaserver, ESO, Star Citizen's server meshing, SpatialOS's failures, interest management. |
| D. Economy and table integrity | Claude or Codex | Dupe history, escrow, provable fairness, hidden information, loot-box and gambling regulation. |
| E. Puck as it is | Claude specialist, code only | Identity worlds, write-back, attestation, adjacency and crossing, federation, the custody protocol, disclosure, durable images: what exists, cited by symbol and `file:line`. |

Each lane separates what it opened and quoted from search summaries and from its
own inference. A synthesis memo (a principal) states each decision with options
and evidence, weighs the combinations Puck is placed to make that other engines
are not (players as worlds, portals and hidden seams, derived products as
caches, behaviour as authored programs, deterministic replay), and names how the
debug world would prove each choice. Two other families review it adversarially;
every finding is answered, and what a bounded prototype or measurement settles is
settled. The owner then rules on the open calls.

## Hypotheses raised in conversation

These are starting points for the lanes to confirm or refute, not positions.

- Player state carries across an edit by default; content templates hold tuning
  and state holds references; an authored reset epoch is the deliberate break;
  dropped player data is set aside where it can be recovered; per-row restore is
  the common path, since every engine build invalidates an exact continuation.
- Custody: title at home, a lease or escrow wherever the thing is, monotonic
  epochs as fencing, a crossing as the custody transaction, a place never
  asserting custody from an image, and recovery by presumed abort and recall.
- Seams: hidden for shards and layers, visible where the fiction is travel.
- Tables: stakes as escrowed custody; fairness by replay.
