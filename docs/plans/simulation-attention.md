# Simulation follows attention

A world's cost should follow who attends it. A host that runs many worlds
pays for every one of them on every tick unless something lets an unattended
world cost less, and at the scale Puck aims at most worlds are unattended at
any moment. This plan holds what is built toward that, the invariant it rests
on, and the steps that remain.
[The decision](../decisions/worlds-and-federation.md#simulation-follows-attention)
holds the options weighed and the reasoning.

## What is built

**A world lapses below the attention its document steps at.** A document
states the least attention it steps at in `simulation.stepsAt`
(`WorldAttention`). The host reads each running world's attention once a host
step (`WorldInstanceHost`'s `ReadAttention`), and a wave row below its bar
takes no step: `WorldTick.Lapse` moves its tick ordinal and engine tick over
the whole ticks it banked. A world that authors no bar steps on every tick.

**A tape records a lapse.** The host writes each lapse on the row's tape as it
makes it (`WorldReplayTape.NoteLapse`, a `WorldReplayEntry.Lapse` among the
tick's authority entries), and a replay, a reproduction or a drive applies the
entry through `WorldTick.Lapse`. Whether a row is recorded is no part of the
decision to lapse it, so a recorded run and an unrecorded run reach the same
state. A lapsing row applies no buffered document operation between steps: a
mutation, a rebuild or an undo waits for a step and owes the row one, which is
where a replay applies it too.

**An observing surface states how far its attention reaches.** A session
source's `attention` (`WorldSessionAttention`) gives two distances from the
surface to the nearest occupied body of the world that declares it: within the
first the destination is watched, within the second glimpsed. A surface that
authors none watches at any distance. An infinity layer never gives more than
a glimpse.

**A body far from everyone takes a coarser cadence.** A kit's `autonomy.far`
(`WorldAutonomyFar`) names the cadences a body takes while every occupied body
stands farther from it than a distance. The tier a body is bound to stands
until one of its cadences comes due, so a far body reads the distance once a
refresh. The distance is one query over one image of where humans stand
(`WorldPopulation.NearestOccupiedSquared` over `ReadOccupied`, through
`FixedSweepGrid`), which a surface's attention reads too.

**Each instance's cost is counted.** `world.rate` reads back an instance's
attention, its bar, the ticks it lapsed, and the body advances and producer
runs its population has run. A producer run is counted where a producer ran: a
body whose producer stands down, or that names none, stages the inert intent
and counts no run.

**A body whose producer is at rest sleeps.** A citizen standing at the point
its errand names costs a tick nothing once it has stood for the idle floor
(`bodies.sleepAfterSeconds`), and wakes when its register is designated again
([the server's account](../../src/Puck.World.Server/README.md)).

The debug world authors these: its paintings and arrival frames carry
`windowAttention` (`worlds/debug/painting-kit.puck`), the city, the garden and
the night field step from `Glimpsed`, the hearth from `Watched`, and the
standard citizen and vehicle kits carry a far tier.

## The invariant

A world's state is a function of its document and its input. A lapse is part of
that input, only a world whose document authors a bar is ever lapsed, and a
lapse is recorded where it is made.

Three places enforce it, and a new caller cannot get round them.

- **One reading.** `ReadAttention` is the only place attention is derived. It
  reads who stands in each world, the sessions the host's worlds hold on each
  other with each surface's fixed-point reading, and whether an observer in
  another process is attached (`WorldOutputHub.Watchers`). No presentation,
  camera or clock enters it, and nothing about whether a row is recorded.
- **One decision.** `Lapses` is the only place a row is told not to step for
  want of attention. It answers false for a world with no bar, since nothing is
  below `Unobserved`.
- **One clock move, written down where it is made.** `WorldTick.Lapse` is the
  only way an authority's time passes without a step. It takes a tick count
  and derives the engine ticks from the world's own rate, so a caller cannot
  move the two clocks out of the ratio a step keeps. The host's `SettleLapsed`
  is its one caller outside a replay and notes the lapse on the row's tape in
  the same place; a replay reaches it only by applying that entry.

The guarantee stops at two places, the first a step below: a world with an
adjacency and a world holding a transfer always step. A row whose tape is
driving takes its lapses from the tape.

## Steps

Each step names the check that shows it worked.

1. **Worlds joined by an adjacency lapse together.** The moon and the debug
   world share the `sky` seam, so neither lapses as an instance. A neighbour
   reads this world's ticks one by one; lapse the pair as one unit when both
   stand below their bars. Check: `world.rate` on the moon reads lapsed ticks
   while nobody stands near either side of the seam, and
   `worlds/debug/moon-seam-tests.puck` still reproduces.
2. **A lapsed body coasts by its producer's law.** A lapsed body stands where
   it stood. A curve follower's station has an exact closed form already
   (`WorldPopulation`'s `AdvanceCurveArc`, which carries its remainder); a
   navigated body needs its route's arc advanced by speed and time. The law is
   an authored member of the producer program, with standing still as the
   neutral default. Check: a scenario in which a curve follower's station
   after a lapse equals its station when stepped throughout.
3. **A remote observer's attention arrives with its session.** An observer in
   another process always counts as watching, because its distance cannot be
   measured here. Carry the observing surface's reading on the federation
   observation. Check: a remote observer far from its surface leaves the
   destination lapsing.
4. **Rule evaluation skips what it can prove.** A lapse is exact for values
   that are a law of time and holds everything else. Each subsystem that can
   name its next event narrows what an author has to accept: the deadline
   tables can today, and a rule's gate can once it is bounded over an interval
   of ticks. Check: a world whose only activity is deadlines takes steps only
   at those ticks and matches one stepped throughout.

## Readings owed

Timing is taken only in the lead's quiet window; none is reported here.

- **Shed ticks on the debug route**, on the RTX 4070 and the RTX 2060, against
  the clean window's figures
  ([the 4070's report](measurements/2026-11-25-window.md)). What decides it:
  the route's shed count per chapter and the per-world step time in the run
  report's `frameThread.worlds`.
- **The standalone city's tick**, with a player in it, against the same
  report's city section.
