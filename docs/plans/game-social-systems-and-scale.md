# Game social systems and scale

**Creature collectives (owner decisions, 2026-09-02).** Authorable local laws,
not a prescribed group lifecycle, must allow solitary creatures to form packs,
split into overlapping subclusters, reunite, and leave again. Explicit orders
remain possible. Social membership, chosen activity, local steering, shared
navigation, and physical contact answer different questions. Sharing a compatible
route is an optimization of chosen behavior, never a reason to force membership;
followers retain independent progress and may detach without losing their bonds.
Ground travel uses the body's tangent plane; airborne and in-medium travel use
three dimensions, with medium membership remaining an actual traversal constraint.

Relationships are directed, contextual, and author-named numeric dimensions.
Affection, source reliability, and perceived competence must not collapse into
one score. A creature may follow a capable stranger it dislikes. Perception and
memory are distinct from world truth: observations and communicated claims carry
provenance, and repeated reports of one event do not become independent evidence.
Conflicts can motivate authored investigation without making the next observation
automatically decisive. Observable attempts and outcomes are separate evidence;
private intent is not magically disclosed. Compact impressions and retained
salient episodes have authored retention, including creatures that remember
everyone. Personality has authored baselines, bounds, plasticity, and optional
recovery; one mistaken expectation need not rewrite unrelated instincts.

Decisions filter inadmissible options, then use authored scoring and either
deterministic or reproducibly weighted choice, with commitment and interruption
rules. Choice randomness is local to the decision, not consumed anew every tick.
Authored cadence and deterministic work budgets control sensing and deliberation;
memory size does not require scanning every remembered individual. Bounded
attention must bound candidate inspection as well as retained neighbor count.
Engine primitives remain a closed declarative vocabulary; arbitrary policy stays
with addons rather than a second scripting language inside state.

The acceptance workload is a few thousand creatures densely packed on ground
or in a body of water, with visible presentation at least 60 FPS on the desktop
and Steam Deck targets. This is an acceptance requirement, not a measurement.
Falsifiers include density-dependent unbounded perception work, slot reuse
inheriting another creature's memories, duplicate hearsay increasing corroboration,
checkpoint divergence, incompatible followers borrowing a narrow route, and
split/replan bursts breaking the frame budget. Verification must include actual
world runs and rendered whole-frame costs, not only isolated steering timings.

Five tracks and **two thin prerequisites, no cycles** — stated as two rather than one because both
are real and an honest account is what keeps the fold from becoming a pile: **track 2's runner gates
track 1** (track 1's own proof is a canary), and **track 5's entity-address type gates track 3's
ghost records**.

**A per-body scale primitive, not a debuff gimmick (owner decision, 2026-09-03).** A body's live
geometric scale is a document-declared multiplier (`bodies.scaleRow`, a keyed `state.world` row whose
own `min`/`max` is the world's declared scale envelope), read and written like any other state cell —
never a bespoke "shrink" mechanic. Collider volumes, resolved move speed and turn rate, hold
probe/standoff/reach, a hold's own gravity fall/rise and its vertical-channel envelope (including a medium's idle/settle target), a wall hold's travel speed, and a pull's own
rate all scale with it on the server — a shrunk body's fall and depenetration stay proportionally
gentle rather than free-falling one tick of full-scale gravity into a collider whose own contact skin
margin it can no longer absorb; the client reads the same live cell into the rendered rig and the seat
chase camera's orbit distance and look-at height, so a shrunk body stays framed rather than shrinking to
a speck on screen. Body-vs-body contact (`WorldPopulation.ResolveDynamicContacts`), overlap events
(`WorldEventFeed`), the cross-boundary continuum trajectory (`WorldBody.ApplyContinuumTrajectory`), the
adjacency sweep's LOCAL side (`WorldAdjacencyContactField`), and a rigid body's own static-contact sweep
(`WorldBody.AdvanceRigid`) all read each body's live-scaled collider volumes now — a shrunk body's
contact with another body agrees with its contact with the world. A rigid body's mass and inertia scale
with it too (mass ∝ Scale³ against the authored mass at scale 1, inertia ∝ Scale⁵, so inverse mass ∝
Scale⁻³ and inverse inertia ∝ Scale⁻⁵ — `WorldBody.ScaleRigid`), along with its bounding radius, centre
of mass, and the linear (never angular) rest threshold. The one residual gap: the adjacency sweep's
REMOTE side still reads a neighbour authority's unscaled shared collider, because a delivered
`EntitySnapshot`/`IWorldAdjacencyNeighbour` carries no per-entity Scale on the wire yet — a shrunk body's
contact against a body standing in a neighbouring authority is not yet scale-consistent, unlike every
same-authority case above. `WorldServer.RestoreCheckpoint` and
every other door that mints a `WorldBody` (a detached-seat/peer restore, a silo's checkpoint boot)
resync the live value from the row, the same catch-up every other admission door already gives a
freshly minted body — a restored session's bodies never disagree with their own `scale` row cells. A
`Region` INTERACTION bound to a per-body carrier property is what turns a specific spot into a trigger,
scoped to the one body it affects — never the aggregate `$region:<placement>` occupant count, which
fires for any body standing in the region regardless of who. Two such interactions, each `Edge` mode
over its own physically separate region, is the trigger/restore shape — never one region's `Level` write
paired with a self-resetting flag cell, which turns every tick a body simply stands in the region into a
document mutation; the garden's `drinkMe` bottle (shrinks on entry) and `eatMe` cake (restores on entry)
are one authored instance of that primitive, not new engine surface of their own.

1. **Frames, as the envelope ratification** — one document shape, not two landings. Order: root/single
   frame, sibling frames, body-parented frames only on demand. **The envelope needs two inputs beyond
   a size and speed band**: an ANGULAR-speed bound, because the solver uses `ω × anchor` and linear
   speed alone cannot bound contact velocity; and a MINIMUM FEATURE SIZE or aspect-ratio bound,
   because one overall body-size band does not bound inertia for an arbitrarily thin box or capsule.
   `FixedMassProperties` is why: inertia scales as the fifth power of extent against mass's third, so
   it exhausts its range first. A third input is a mass-ratio ceiling — a maximum speed cannot bound
   how *slowly* a contact closes. **Size the bands analytically, never by sweeping the sample worlds**,
   which describe today's content rather than what a world may declare.
   An interval proof must name the failed quantity, kernel, frame, and envelope corner. Shift-by-zero
   makes bit identity plausible by construction, but the argument alone protects neither evaluation
   order, defaulting, nor serialization — the canary still needs a state-sensitive observation, and
   the read-back must show declared envelope values AND derived placements with proof margins.
   **Track 1 also closes the soundness input that adjacency overlap now consumes**: every kit's
   speed is bound by an authored envelope (`WorldSpeed.Envelope`/`ThrustSpeedEnvelope`/
   `TopSpeedEnvelope`). Adjacencies themselves accept no guessed depth; the compiler derives one
   symmetric overlap from body reach, interaction/targeting reach, and two slower-side delivery
   periods of closing speed, with outward rounding.
2. **The canary runner** — and it goes first, because track 1's own gate is a canary. `puck canary`
   strictly loads a central per-id manifest tree and runs each proof's positive and executable
   discriminating legs against one exact Release build of the real `Puck.World`. Every non-comment
   stdin command declares its accepted or intentionally refused outcome; observations select
   stream, verb, occurrence and exact cardinality, with ordered sequences, named values and small
   typed comparisons. The runner owns fresh state, separate stream drains, BOM-less closed stdin,
   exact `--world` origin, process exit, per-leg timeout/tree kill and a whole-suite budget. It
   REFUSES a blank binding declaration, but sensitivity comes from the required red leg, not prose.
   Boot shape is separate from environmental requirements, and only headless proofs with no such
   requirement form the nonempty automatic set. `puck landing` keeps every git-loss check first;
   only a clean git component runs that automatic set, followed by one final component-naming
   verdict and no skip path.

   A command claim's `stream` override lets an accepted outcome expect its confirmation on stderr
   instead of stdout — the shape server narration (`[world.grant: …]`, `[world.revoke: …]`) always
   uses regardless of accept/refuse — so `tests/Puck.World.Canaries/addon-mutation-seam` now covers
   the grant-door and guest-mutation claims a canary manifest could not represent before.
3. **The neighbour tape, then ghosts** — the ghost read-side now rides the same delivered snapshot
   as adjacency contact and rendering, and snapshots carry `(authority, body index, body generation)`
   addresses. The remaining work is transport determinism: hoist neighbour-field derivation to
   DELIVERY and tape per-tick records separately from definition revisions. **Pin which delivered
   revision a consumer tick sees at tick start** — "latest revision when accessed" must never become
   the input. Ghosts remain read-only and never authoritative.
4. **Playability** — and it OPENS with one seat-lifetime view state: world-owned camera structure,
   profile-owned input preference, standard dual-stick movement/look, and one logical basis shared by
   intent composition, local rendering, traveler rendering, cursor capture, and read-back. No
   slot-global orbit, binding-side feel cache, renderer-local orbit cache, or mixed schema survives. Then
   the owner feel sitting,
   then the touch-triggered win slice; navigation and equip facets follow. Ordering matters here:
   put navigation first and "feel is the gate" becomes prose while navigation expands underneath it.
5. **Ownership, membership, combat** — entity-addressable rules and elemental interactions, both
   with local first callers. The transport/runtime spine has landed as
   `WorldEntityAddress(authority, index, generation)` and is already exercised by adjacency ghosts;
   authored local `body:n` still needs to lower to that address at compile/install time. **Do not reuse `WorldHandle`** — it is a capability-table
   designation stamped with principal and capability, an authority identity, not an entity identity.

**Reviewed 2026-08-10 (independent, Codex/GPT). Its recommendation, which is advice and not a
ruling — the call below is still the owner's: ratify the five tracks, aim track 5 at the charter, do
NOT create a sixth.** Its reasoning: world rules, interactions, the property vocabulary and a local
combat caller ALREADY EXIST; what is missing is charter-world EXERCISE, so a sixth horizontal
"content later" track would add a lane without adding a capability. It also verified that the Phase A
nouns survive on the rebased tree (`WorldStateAdvance`, `WorldOwnership`, properties, rules,
interactions) and that `combat.world.json` and `reconnect.world.json` booted headlessly at the time (both deleted 2026-09-06; the
arena district carries combat's rules) —
track 5 must re-verify this before relying on it: both scenario docs have since drifted behind several
schema generations (stale basis reference, placement-policy fields, motion shape, kit vocabulary, host
fields — partially repaired in the garden/w1 integration) and, as things stand, refuse validation
outright (a kit claims a channel role and a held/action channel that `channels[]` never declares, and
the document is missing required `collision` and `views` sections entirely). Opening track 5 with
verification rather than reconstruction still holds ONLY once that drift is repaired; it does NOT by
itself prove behavioural survival. **If track 5 is aimed at the charter, its completion criterion becomes charter
EVIDENCE, not landed primitives**, and track 4 owns the feel gate.

The obsolete portal-border canary was deleted with that model. Its replacement,
`puck canary seamless-adjacency`, executes both the crossing and stationary discriminating legs on
the real headless composition path. The stronger `puck canary four-corners-sharded` starts five
distinct authorities — four ground worlds plus the floating island — and requires one player's
complete four-ground-world circuit through the router that follows a body wherever it now lives,
distinct binds, remote-authority naming, and zero wire errors on every authority. Vertical/island
handoffs, cross-host body contact, autonomous travellers, retained dual-stick control, and derived
diagonal peers are not yet exercised by it — widening its scripts is future work, not a runner gap.

**Owner decision:** no sixth track. Track 5 is aimed at the charter from the start, so its rule
primitives land with the content that proves them.

