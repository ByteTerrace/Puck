# MMO foundations: a closed design round

The round that asked what each established MMO answer becomes when every zone,
character and server is a world with its authority placed per world. It is
closed: its decisions live in
[the decision register](../decisions/worlds-and-federation.md), its order of
work in the plans and work packets the register's
[building it](../decisions/worlds-and-federation.md#building-it) names, and
three calls stay with the owner, deferred. The records below are the evidence
the register cites.

## What the owner said

- Puck exists to make it possible, eventually, to build an MMO at the scale of
  World of Warcraft that redefines the genre the way WoW did. The emulators and
  the screen-within-a-screen work were envisioned as seamless parts of that
  MMO. [AGENTS.md](../../AGENTS.md#pucks-purpose) records the purpose and how
  every design round weighs it.
- Portals are meant to solve many of an MMO's hard problems.
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
