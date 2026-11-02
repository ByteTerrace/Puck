# Readable interaction

Puck's animations can be simulation rather than decoration: a swing, a pounce
or a thrown object is an authored, deterministic motion program, so its future
is computable. This plan turns that into a general player-interaction system
in which what a player sees is what is about to happen, and skill is reading
it and answering in space and time. A sword duel is the first forcing case;
the mechanisms serve any interaction (dodging a falling boulder, catching a
thrown object, meeting a creature's pounce).

## The owner's rulings

- Local and remote, player against player and player against environment,
  co-operative and free-for-all are all intended; authors always rule which.
- The proof of concept is playable and fun first, closer to Zelda and Sekiro
  in spirit but Puck's own; authors make their own choices.
- Telegraphs are diegetic first (the blade's motion, its glint, its shadow);
  authors may add clearer indicators at their discretion.
- This is a forcing function for general player interaction, not a sword
  feature.

## The idea

**The future is readable.** A motion program is a pure function of time and
state, so the engine can compute the volume a moving part will sweep over the
next ticks. That volume is the truth the telegraph shows, not an animator's
approximation. The same computation is the visibility plan's V2 (swept bounds
for authored motion, [visibility](visibility.md)); one mechanism serves culling
and play.

**The commitment frontier.** A program may branch on input until a tick after
which its motion is committed. What lies past the frontier is certain; what
lies before it can still change. Feints are authored by moving the frontier:
the reader reacts to what is locked in, and the actor chooses when to commit.

**Answers are spatial.** Evading is being outside the swept volume. Parrying
is placing one's own part where the other's will be. Certified field contact
resolves first contact between two fields at an exact time, so an author grades
an answer by when and at what angle the parts meet; the geometry is the
window, not a hidden timer. A press commits a stance or opens a branch (a
riposte); the quick-time event dissolves into continuous play.

**Projectiles and hitscan.** Rays and sphere casts against the same fields the
frame draws, so a hit is exactly what is seen. A projectile is a swept sphere
with sub-tick contact; a blade's field can deflect it, the angle off the face
deciding where it goes. Aim is a program too, so a shot telegraphs as a swing
does.

**Fair over a network.** Determinism and per-tick command snapshots make
prediction and rollback possible, and windows measured in ticks are identical
on every machine. A press stamped within its tick lets an author judge the
press, not the tick boundary.

## What the engine provides and what authors write

The engine's mechanisms (Puck code over engine code; nothing below is a
sword):

- the predicted swept volume of a motion program over a horizon, and its
  commitment frontier;
- a field-against-field first-contact query giving the time, point, normals and
  relative velocity;
- the sub-tick time of a press within its command snapshot;
- a material input that renders a predicted volume or a frontier, for authors
  who want an overt indicator;
- prediction and rollback for remote sessions (a dependency; Puck has no
  rollback plan yet, and this plan asks for one).

Authored, in the standard package and in worlds: weapons, swings, feints and
their frontiers, how answers are graded, window sizes, riposte branches, how
loud a telegraph is, difficulty and accessibility settings, and which modes a
world offers. The standard ships one duel kit as a worked example; the debug
world develops it.

## Where it proves itself

The debug world gains a fencing salle, a debug tool disguised as architecture:
a place that forces contact precision, motion prediction, input latency from
press to screen and, once it exists, rollback. It is lane C12 of the
[composition plan](debug-world-composition.md).

## Build order

| Step | Lands | Check on `worlds/debug` |
|---|---|---|
| 0. Research round | The round across the families: readable combat and telegraphs in shipped games (Sekiro's deflect, Zelda's flurry rush, For Honor's guard, fighting-game frame data), prediction and rollback netcode (GGPO and successors), sub-tick input, and what Puck's determinism and fields make possible that others cannot | A consensus and this plan revised from it |
| 1. Predicted sweep and frontier | The motion program's swept volume over a horizon and its frontier, shared with the visibility plan's V2 bounds | A swing's predicted volume contains every pose the swing reaches, across a run with feints |
| 2. First contact | The field-against-field first-contact query with time, angle and velocity, on the certified contact solver | Two blades meeting at known times and angles report them exactly, reproducibly |
| 3. Sub-tick presses | Press timing inside the command snapshot, replayed exactly | A recorded duel replays with identical grades |
| 4. The salle and the duel kit | C12: the place, and the standard's duel kit with diegetic telegraphs | Two local seats duel; parry, evade and feint read from the blades alone |
| 5. Remote | Prediction and rollback, from its own plan | A remote duel feels as fair as a local one at an authored latency |
