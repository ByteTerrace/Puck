# The campaign

**There is ONE campaign, and everything in this repository serves it.** Puck is a notation for
worlds ([vision.md](vision.md)); the campaign is the first official game, whose job is to prove the
notation expressive enough to be worth having. A change that does not move that proof forward is
either infrastructure the proof needs, or it is tunnelling.

Read this before picking up work. It is the only document that says what we are collectively
building; every other document under `docs/` is a reference you consult *while* building it, never a
place to start.

## The charter (owner-ratified 2026-08-06, binding)

**Four game worlds, no others.** **Play** — the overworld hub, the game's first main city, a plaza
that local multiplayer shares — and three instanced "dungeons" reached from it: **Dive**
(underwater), **Kart** (racing), **Jump** (platformer).

Each dungeon is entered through a wall-mounted picture-frame portal standing in Play. Walk to a
frame and the world underneath changes with no restart, never a loading menu and never a `--flag`
mode swap. Design is **feel-first**: a dungeon earns its place by how it feels to move through,
before any scoring, unlock or progression is layered on it. Play's own feel is gentler than any
dungeon's — a promenading pace fit for a shared plaza.

`studio` ships beside them as a non-game **dev canvas** for character work, and as Puck's first
formal border crossing (owner amendment 2026-08-09): play and studio meet at a mapped border, so
studio is reachable by walking out of the plaza as well as by `--world`. It is not a game world and
not a destination in the reveal graph. A doc counting "four worlds" is counting the charter's
roster; the directory holds five documents.

**Reveals are a core world mechanic** — attunement-like achievement facts carried on the identity,
general enough for cross-game unlocks between trusted servers. Every world is a starting point; all
starting points converge on the hub. An arcade cabinet stands dark in Play's plaza as the first of
them.

## Where the campaign actually is

**Do not trust this section's vintage — re-run the checks.** Each claim below names the check that
produced it, because a status sentence with no check behind it is how a reader ends up believing a
capability exists. This is the whole reason the old per-capability register was deleted and must not
come back.

**Verified 2026-08-11, at the seamless Four Corners landing:**

| Claim | The check |
|---|---|
| Five world documents boot | `dotnet run --project src/Puck.World -c Release -- --world <name> --exit-after-seconds 2`, audit STDERR — exit code 0 is NOT success |
| Play authors ground, four walls, four portals, dark arcade cabinet | read `src/Puck.World/Assets/worlds/play.world.json`'s `placements` |
| Every world authors per-body action logic | the same documents' `actions` lanes carry `predicates`/`effects` |
| **No world authors WORLD-SCOPE rules** — 0 of 10 documents carry a `rules` or `interactions` section | the same read; `rules.schema.json` and `interactions.schema.json` both exist |

**The foundation is complete and overshot.** Three motion arms (grounded, vehicle, swim); the portal
lane end to end — step into a frame and the whole party transfers, all-or-nothing across capacity
*and* authorization; input vocabulary with ordered chord activators; the radial wheel; roster sync;
durations authored in seconds with ticks derived at compile; per-world clocks; the market/auction
  substrate; `studio` and the first border crossing; a walkable four-zone corner whose four hosts
  exchange geometry and generation-addressed bodies and migrate both human and autonomous entities
  through invisible reciprocal topology rather than portal furniture.

**The charter's back half has not started**: the owner feel sitting (the gate declared 2026-08-08,
never held — and now well behind the motion work, so its recordings want redoing), win conditions,
achievement facts and the meta-achievement, the cabinet quest, the Konami easter egg, Play's social
pass, and the first reveal edge.

**Win conditions split, and half needs no engine work.** A touch-triggered individual condition
("this body reached the trophy") is a per-body interaction whose effect writes `state`, with the HUD
binding the row — every world already authors `actions` lanes, so this is authorable today. A
comparative or aggregate condition ("first to N", "team score ≥ X", anything reacting to a state
change from an arbitrary source) cannot be expressed per-body, because a per-body action cannot
watch another body's write. That half needs world-scope rules. **So the fastest path to a world that
can be WON does not wait on the rules section.**

## How the work is shaped

Five tracks and **two thin prerequisites, no cycles** — stated as two rather than one because both
are real and an honest account is what keeps the fold from becoming a pile: **track 2's runner gates
track 1** (track 1's own proof is a canary), and **track 5's entity-address type gates track 3's
ghost records**.

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
   speed is bound by an authored envelope (`MoveSpeedEnvelope`/`ThrustSpeedEnvelope`/
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

   This envelope is intentionally not universal. The richer PowerShell batteries
   `undo-all-or-nothing`, `strict-definition-parse`, `sdf-decode-sign-refusal`, `doc-links`,
   `addon-mutation-seam`, and `four-world-boot-smoke` remain named, on-demand, and UNGATED; the
   canary runner neither wraps nor weakens them.
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
interactions) and that `combat.world.json` and `reconnect.world.json` boot headlessly — which
supports opening track 5 with verification rather than reconstruction, but does NOT by itself prove
behavioural survival. **If track 5 is aimed at the charter, its completion criterion becomes charter
EVIDENCE, not landed primitives**, and track 4 owns the feel gate.

The obsolete portal-border canary was deleted with that model. Its replacement,
`puck canary seamless-adjacency`, executes both the crossing and stationary discriminating legs on
the real headless composition path. The stronger `docs/verification/four-corners-sharded/run.ps1`
starts four distinct authorities and requires four simultaneous remote human handoffs, an
autonomous-body handoff through the same seam, one player's complete four-host circuit with its
original input credential and camera route still following, post-handoff queries, every host's two
edge peers plus its compiler-derived diagonal corner peer, four non-colliding entity-address
namespaces, and zero wire errors.

**Owner decision:** no sixth track. Track 5 is aimed at the charter from the start, so its rule
primitives land with the content that proves them.

## The rules that keep this honest

These are earned, each from a defect that cost real time.

**Every durable artifact declares its own falsifier.** A canary names what in the observation is
bound to the variable under test — a pixel diff where nothing in frame tracks the variable proves
nothing, and one such witness persuaded two reviewers at once. A design document states the premises
that would kill it, as re-runnable checks. An artifact that cannot say what would falsify it is
asking to be believed.

**Never write a status column.** A status claim duplicates what the code answers better, so it is
pure liability with a superior substitute always available. A decision records what the code cannot
answer — why, what was rejected, where a boundary sits — and stays irreplaceable even when stale.
Keep decisions; delete status; generate inventories or do without them.

**Security claims default the other way.** For a feature, unverified means not-done and the cost of
error is re-planning. For an escalation, unverified means **still open** — the cost of the other
default is shipping a hole because its citation rotted.

**Verify by running, and by content.** Exit code 0 is not success; audit the streams. A commit hash
absent from the branch does not mean its content is absent — that has produced two false alarms
here. And a search hit is not a repository fact until the file is tracked.

## Work list carried out of retired plans

Retired 2026-08-10 with their decisions moved into the code they govern:
`capability-channels-plan.md`, `capability-channels-STATE.md` (whose `Landed?` column was the banned
per-capability register, and which drifted in *both* directions — closed decisions listed as open
security risks, and a stale gap list), and `design/navigation-field-spike.md`.

What survives them, as work rather than prose:

- **Binding-destination escalation — SECURITY-OPEN-PENDING-WITNESS. TRACK 2 owns the witness;
  TRACK 5 owns remediation if it comes back red.** (It was open and unowned, which is how a security
  item quietly becomes nobody's.) `Mutate`/`section:bindings` may still let a binding name any
  registered verb. The plan's stated mechanism (`CommandRegistry.Push` carrying no principal) no
  longer exists — the registry threads `CommandPrincipal` — but that kills the citation, not the
  hole. The witness is one real-path refusal-with-control canary: a non-privileged principal
  authoring a binding whose destination is an administrative verb must refuse, while the same
  mutation naming an ordinary verb applies.
- **Replay coverage.** `WorldReplayEntry` captures 12 kinds. Mutation, undo, composition and query
  submissions are still bare passthroughs, so a mid-recording mutation or undo does not replay, and
  `replay.verify` proves the pose trajectory only.
- **Unverified, check before scheduling** — session-lever routing (`world.volume`, the render levers,
  `world.save`); per-route translation and channel masks (document-only, no `player.engage` override
  for the mask); whether fuel is still the only stop for a spinning guest.
- **Navigation.** The decision worth keeping: navigation derives walkability from the SDF a world
  already authors, and adopts Puck's existing quantize-once boundary rather than inventing one — the
  bake is the only place a float may appear, and every consumer after it reads `FixedQ4816`. Match
  `WorldQueryBaker`/`WorldQueryArtifact`/`BakedWorldQuery`, which already exist and already pack this
  way. **Falsifier:** the design assumes no chunk or pathfinding primitive exists —
  `puck declarations src --name Chunk`, `--name FlowField`, `--name Nav` must all return nothing.
