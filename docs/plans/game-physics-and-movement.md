# Game physics and movement

This plan defines the proposed physics and locomotion work for the reference
game. It keeps authoring decisions, solver constraints, feel settings, and
their acceptance evidence together so implementation can be checked against
the experience the game needs. The decisions below describe intended work;
dated results and current behavior remain in the [milestone records](../development/game-milestones.md).

Authorable rigid dynamics is an owner decision. A rigid body is a kit facet
(`rigid`), never a second body kind: physics-first authoring derives mass and
inertia from the kit's own collider and an authored mass, never a free density
or tensor. A kinematic character contributes its velocity to a rigid contact
but is never itself pushed unless its own kit says so. Substep count for
continuous collision is derived per body per tick from speed and collider
size against an authored ceiling and an authored per-substep travel
fraction, never a free per-tick knob. Restitution against the static world
fires only on a genuine impact (the rising edge of contact) on each of the
ground and obstruction contact channels independently, never every tick of
continued rest — the naive per-tick reapplication is a stable non-decaying
bounce, not a settling body, and conflating the two channels is what let a
grounded ball's continuous floor contact mask a fresh wall impact. A
rigid-vs-rigid pair carries no such latch, so its restitution is instead
floored to zero below a small closing-speed threshold — the same "settle,
don't chatter" intent applied to a contact with no rising-edge state of its
own — the threshold is an authored field, not a C# constant. A contact
anchor — static (ground/obstruction) or pair alike — is the struck shape's
own true witness point (the real support point of its box/capsule/sphere
geometry oriented by the body's quaternion), never a point on the
conservative bounding sphere and never the body center, so a strike carries
the real lever arm its own surface implies. A grounded box or capsule
resolves over its own support manifold (up to four box corners, or two
capsule cap points when it lies on its side) with a few authored
sequential-impulse passes rather than one witness point — the thing that
keeps an upright body's centre of mass over its support polygon without an
artificial extra damping term standing in for real contact geometry. A
struck pair's own normal impulse propagates further than one pair-hop per
tick too: the dynamic-contact solver runs a few extra full broadphase-plus-
narrowphase sweeps over the same tick — never a replay of only the pairs the
first pass happened to find, since a pair's own positional split can move a
body into a third one only a later sweep discovers — the count itself derived
down from an authored ceiling by how many pairs that first pass routed
through the rigid impulse path, so a rack break or a falling line of dominoes
spreads within the tick it happens rather than one body-hop per tick. Every
one of these counts — manifold passes, extra sweeps, the sweep ceiling itself
— is an authored field
echoed in `world.budget`, never a free per-tick knob. A pair's tangential
response is a real Coulomb impulse
through the two-body kernel, clamped to the friction coefficient against the
normal impulse just applied, never an independent rescale of either body's
whole velocity (which would burn or invent momentum along the normal).
Friction carries the same Coulomb meaning against the static world and
against another rigid body — one authored coefficient, one physical model,
never a decay rate; rolling friction and both damping channels remain
authored per-second decay rates, not per-tick fractions, so one authored
value decays identically at any simulation rate. Each substep rotates and
translates the body about its own centre of mass, never its root, so a
rolling collider's rendered position does not orbit the root as it spins.
Cross-world transfer of a rigid body is
out of scope and refused by name — a carrier holding one refuses its OWN
transfer for the same reason, rather than dropping or orphaning what it
holds. The garden's `billiardsTray`/`bowlingLane`/`dominoes`
placements are the proof fixture; see the
[server](../../src/Puck.World.Server/README.md#rigid-dynamics-worldbodyrigidcs-worldpopulationrigidcs)
and [schema](../../src/Puck.World.Schema/README.md#rigid-dynamics-worldrigidcs) references.

**Locomotion feel is a kit field, not a baked constant (owner decision).**
`WorldBody`'s per-tick catch-up bias against a curving surface (`StickSpeed`,
the old flat `2.0`) is genuine feel, not a value derivable from the kit's own
resolved move speed: a first pass tried deriving it from speed and measurably
regressed slope climbing on any `GradientDerivedUp` world (a faster kit's
larger inward bias converts into downhill drift under depenetration faster
than it converts into held contact) — no shipped world caught it because none
authors `GradientDerivedUp`. It is now its own `motion` row field
(`groundStick`), independent of `motion.speed`, defaulting to the engine's old
`2.0` bit for bit. The up-axis steering ceilings (how fast a solved gravity
field, and separately a measured ground-contact normal, may turn a body's up
axis), the drive frame's pitch clamp, and the non-walkable-contact witness's
latch (displacement, idle threshold, grace) are likewise genuine feel, not
derivable from anything else a document declares — each is a `motion` row
field (`upTurn`, `turn.maxPitch`, `obstruction`) whose default reproduces the
engine's old hardcoded value bit for bit, so no shipped world's behavior
moved. `world.kits` echoes all five.

