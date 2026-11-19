# Avatar Expressions & Class-as-Configuration: Concept Memo

Puck's advantage at MMO scale is not bigger numbers or more hotbars—it is that geometry, sound clocks, board topologies, per-viewer truth, scoped time, physical fields, and nested machines are all live, deterministic simulation state. Every concept below starts from **what the avatar's hands, arms, torso, and legs physically do** with chunky, readable, painterly-futuristic hardware suited to Moth's suit, leaves numbers and strict healer/tank/damage labels open, and is tested against both a 5-player party and a 40-player raid.

Concepts **1** and **9** build directly on the owner's prompt and the lead's frame. Concepts **2 through 8** step outside the lead's frame.

---

## 1. The Reed-Dancer *(extends the owner's bard + dance-fighting seed)*

**Fantasy:** A thruster-assisted brass skirmisher who plays a forearm-mounted wind horn while spinning, sliding, and kicking so the suit's own movement and breath-intakes pump the instrument.

- **Tool and body interaction:** A compact, hinged **bellows-reed horn** mounted on the left forearm and collar, paired with articulated key-rings on the right fingers and breath-intake vents built into the lower-leg jump-jets.
  - *Playing phrase:* Left forearm rises so the mouthpiece latches under the visor; head tilts slightly; left wrist rotates the reed barrel to change register while right fingers tap crisp keys on the beat.
  - *Dance fighting:* A quick shrug locks the horn flat across the collarbone (still sounding off a pressurized chest bladder) while both arms whip free for low capoeira sweeps, thruster-slides, handstand pivots, and heel-strikes. Landing a spin or kick on the beat visibly compresses the chest bladder and flares the horn bell, feeding breath pressure straight back into the next musical phrase.
- **Group of 5 / Raid of 40:**
  - *5 players:* Sets a live party tempo on the authority's `MusicClock`. Teammates read the groove through diegetic horn audio, subtle suit-light pulses, and optional controller haptics; timing their own bursts, dodges, or throws near the downbeat strengthens them, while the Reed-Dancer weaves through hazards to keep breath pressure full.
  - *40 players:* Eight bards playing eight tempos would be chaos. At raid scale, Reed-Dancers lock onto one encounter `MusicClock` as a **brass section**: one leads the measure while others pick harmonic registers (bass pulse, syncopated accents, chorus swell). Floor beat-rings aggregate per squad rather than per player, and audio mixing ducks duplicate voices into a cohesive horn choir.
- **Combinations:** Pairs cleanly with anything cyclical or timed—drives a *Floor-Caller*'s tile rotation every bar, vents a *Kiln-Siphon*'s stored heat on the downbeat, or pairs the horn with a one-handed *Lathe-Carver* so dance spins carve curved trenches in rhythm.
- **Puck capability and engine stress:** Uses deterministic `MusicClock` / `MusicDirector` simulation state, sub-tick `WorldInputHistory` matched against `$clock:<music>:phaseError`, and diegetic `VoiceSynth` emitters anchored to the horn bell (`TryShapePosition`). *Stresses:* fair beat-window judgement across varied player latency without breaking determinism, multi-emitter audio polyphony and ducking at 40 players, and seamless upper/lower-body rig blending between fine finger articulation and acrobatic motion programs.

---

## 2. The Mosaic-Mender *(outside lead's frame — a tactile "puzzle-healer")*

**Fantasy:** A field-surgeon machinist who pulls cracked geometric "fracture plates" out of damaged allies and slams matching ceramic wedges into the gaps to make them whole—or jams mismatched wedges into enemies to lock up their joints.

- **Tool and body interaction:** Magnetic **caliper gauntlets** paired with a rotating **die-wheel** strapped to the backpack that feeds interlocking faceted wedges (porcelain-and-teal tangram/polyomino pieces).
  - *Reading and fixing:* The avatar sweeps both gauntlets apart to project a waist-high floating fracture ring off an ally (or directly across a damaged construct). The left hand tilts and rotates the ring; the right hand reaches over the shoulder to pluck a wedge from the die-wheel, twists the wrist to orient it, and punches it straight into the void. A true fit snaps flush with a sharp shoulder recoil and chime; a rushed misfit sparks and leaves an ugly overhang that crowds the next piece.
- **Group of 5 / Raid of 40:**
  - *5 players:* Different trauma types cut distinct geometric notches (a jagged burn gap, a split collar, a hollow ring). The Mender reads shapes on the move, rotates pieces while sliding behind cover, and can toss a half-solved plate to another player to hold or finish.
  - *40 players:* Tiny per-person jigsaws on 40 bodies would vanish in a crowd. In a raid, group trauma condenses onto shared **squad keystone polyhedra** floating at chest height over sub-groups or banners. Multiple Menders orbit opposite faces of the same 3D socket at once—one spinning the keystone while two others punch wedges into the faces turning past them.
- **Combinations:** With a *Lathe-Carver*, a teammate can slice an oversized wedge down to fit a cramped gap; with a *Pocket-Pendulum*, a botched cram can be rewound cleanly; with a *Thruster-Dance* chassis, the Mender flips through the air while snapping wedges into overhead faces.
- **Puck capability and engine stress:** Pairs `CompiledTopology` / `CompiledPattern` (exact tile occupancy and fit validation in rules) with live **SDF boolean unions and subtractions** so the fracture genuinely looks carved out of the plate and seals seamlessly when solved. *Stresses:* rapid per-instance SDF updates without heavy mesh rebakes, legible 3D shape silhouettes at mid-camera distance, and fast piece-orientation controls on gamepad and mouse that never hijack movement.

---

## 3. The Fulcrum-Smith *(outside lead's frame — a physical "spreadsheet-mage")*

**Fantasy:** A kinetic mathematician who clamps a brass-and-glass slide-rule between two live quantities in the world and physically pushes the fulcrum collar to trade one's speed, weight, charge, or rate into the other's.

- **Tool and body interaction:** A telescoping **vernier beam** (part surveyor's balance, part oversized slide-rule) carried across the back that unfolds into a two-handed graduated bar with a heavy weighted collar in the middle, plus a clicking shoulder **differential dial**.
  - *Clamping and tuning:* To link two subjects, the avatar jabs the left end toward A (magnetic clamp locks), swings the right end toward B, and pulls the beam across their chest. Both hands grip the sliding collar and visibly brace shoulders and knees as they shove it left or right to steepen the ratio (`1:2`, `1:4`, `1:8`)—the beam vibrates and vents amber heat as leverage climbs. Spinning the beam end-over-end with a sharp wrist flick inverts the ratio on the spot.
- **Group of 5 / Raid of 40:**
  - *5 players:* Turns the party into a live coupled system rather than isolated stat sheets. Clamp a sprinting flanker's ground speed to a stationary defender's bracing strength; clamp an enemy's heavy windup mass to the party's jump lift so the whole squad vaults a shockwave; slide a hazard's momentum collar toward zero and dump its energy into an ally's charge rate.
  - *40 players:* Forty crisscrossing lines would be spaghetti. At raid scale, Fulcrum-Smiths clamp onto **shared buses** (a squad's average speed, a boss's posture meter, a generator pylon's draw) and plant a single tall **master vernier pylon** in the arena floor. Anyone 60 meters away can glance at the glowing collar's height on that pylon to read what trade-off the raid is currently running.
- **Combinations:** Clamping a *Kiln-Siphon*'s tank pressure to a *Reed-Dancer*'s cadence automatically speeds up the beat as the room heats up; clamping two *Lathe-Carvers*' heat budgets lets one carve continuously while the other cools off.
- **Puck capability and engine stress:** Built on `StateArena` reactive `StateDynamics`, `RuleDataflow` / `RuleHazards`, and clamped `FixedMotionTuning` per body. *Stresses:* runtime cycle detection and graceful refusal (`A` driving `B` driving `A`) without blowing fixed-point bounds, keeping coupled body physics independent of `WorldPopulation` iteration order, and drawing sparse, readable spatial gauges in a busy scene.

---

## 4. The Lathe-Carver *(outside lead's frame — live subtractive & fillet SDF play)*

**Fantasy:** An industrial suit-sculptor who gouges, hollows, bevels, and smoothly welds the signed-distance world itself during a fight to remake cover, ramps, traps, and sightlines on the fly.

- **Tool and body interaction:** A spinning segmented **rotary gouge** ringed around the right forearm (chunky workshop router meets arm-cannon) and a broad flat **trowel-plate** hinged on the left forearm.
  - *Cutting vs. blending:* The avatar drops into a wide brace and sweeps the spinning right arm through rock, floor, or hardened armor; the torso twists into the cut with heavy recoil and flying mineral shavings. Holding the left trowel-plate open widens the smooth-blend radius so cuts turn into curved bowls and skateable ramps (`smoothSubtract` / `smoothUnion`); snapping the left fist shut switches to a crisp sharp-edge carve. Pressing both palms to the floor and heaving upward pulls a smooth, rounded rib or blast-shield straight out of the ground material.
- **Group of 5 / Raid of 40:**
  - *5 players:* Opens firing windows through canyon walls, carves a trough to catch a rolling enemy, fillets a jagged cliff into a smooth ramp so teammates never break sprint, or trims an obstacle's silhouette to break line-of-sight.
  - *40 players:* Permanent freehand destruction from 40 players would shred any arena and stall navigation. At raid scale, carvings are **costed and elastic** (unmaintained cuts slowly relax back, or hold within a shared local carve budget), and Carvers cooperate on macro-geometry—three Carvers cutting a terraced amphitheater into a ridge so 40 players get clean stacked sightlines over one another.
- **Combinations:** Carves basins and trenches that physically steer a *Kiln-Siphon*'s flowing liquids or heat; carves curved acoustic alcoves that focus a *Reed-Dancer*'s sound; frames clean recesses for a *Stagehand*'s optic shutters.
- **Puck capability and engine stress:** Directly exercises live SDF booleans, `WorldSolidField` colliding exactly as drawn (`CollidesAsField`), and `NavigationRuntime` incremental region rebakes (`Domain.TryBeginRebakeFrom`). *Stresses:* real-time SDF updates without frame hitches or full mesh rebakes, clean depenetration when an elastic floor relaxes under a standing body (`WorldBody.SweepContact`), and keeping `IRegionalContactField.TryDifference` cheap under rapid group edits.

---

## 5. The Pocket-Pendulum *(outside lead's frame — scoped time & ghost loops)*

**Fantasy:** A horologist-sentinel who drops a weighted gyroscopic plumb-bob to record a few seconds inside a small sphere and cranks only that pocket backward—or loops a deterministic ghost of it—while the rest of the world keeps running.

- **Tool and body interaction:** A lantern-sized **brass escapement pendulum** suspended from a small articulated crane-arm on the left hip, paired with a chunky **winding crown** bolted onto the right gauntlet.
  - *Marking and cranking:* Dropping an anchor is a decisive downward plant of the left arm—the pendulum unhooks and hangs spinning in mid-air like a surveyor's bob. Rewinding is physically effortful: the avatar plants their feet and cranks the right-arm crown counter-clockwise in deliberate, ratcheting turns; the more mass or energy inside the pocket, the harder the torso leans into the turn. Clapping both gauntlets snaps the pendulum back onto the hip crane and seals the pocket.
- **Group of 5 / Raid of 40:**
  - *5 players:* Mark an ally before they dive into a lethal zone to pull a lever or draw fire, then crank them back out; rewind a collapsed bridge segment while the team crosses; or lock a 3-second replay **ghost** of a teammate holding a floor plate while the real teammate moves on to the next puzzle room.
  - *40 players:* Forcibly yanking 40 players' cameras backward would feel awful. In a raid, scoped rewinds target **objects, arena structures, replay ghosts, and opt-in bells**: rewinding a shattered cover pylon, looping a 4-second ghost echo of a defensive shield wall, or ringing a checkpoint bell that allies *choose* to snap back to with a single button tap.
- **Combinations:** Loops a *Reed-Dancer*'s 4-bar solo as a ghost musician while the real dancer relocates; rewinds a *Mosaic-Mender*'s ruined fracture plate; pairs with a *Stagehand* who hides which twin is the ghost and which is the live player.
- **Puck capability and engine stress:** Turns `ArenaJournal` / `ArenaScopeSnapshot` and `WorldScopedReplayRuntime` (scoped live replay alongside a running world) into direct play. *Stresses:* clean boundary rules where a rewound scope meets the un-rewound world (crossing bodies, items under custody/escrow), rolling ring-buffer memory across several Pendulums in a 40-player raid, and visual treatment that keeps replay ghosts clearly distinct from live players.

---

## 6. The Stagehand *(outside lead's frame — per-viewer reality & optic masking)*

**Fantasy:** A theatrical light-rigger who frames slices of the battlefield through articulated shutters so allies and enemies literally receive different versions of the room.

- **Tool and body interaction:** Folding **barn-door shutter wings** mounted over the shoulders like a compact studio lamp-rig, paired with **framing-gloves** whose rigid thumb-and-forefinger segments snap into a crisp director's viewfinder rectangle.
  - *Framing and filtering:* Raising both hands into the viewfinder L-frame immediately gives a distinct silhouette from behind (elbows high, head tucked slightly behind the open rectangle). Pulling the hands apart casts a floating, crisp-edged optic frame into the space ahead. Twisting the wrists inward *masks* what lies behind the frame from hostiles (presenting an empty corridor or decoy silhouette); twisting outward *discloses* hidden state layers—buried mines, internal boss weak seams, puzzle markings—exclusively to allies looking through or standing inside the beam. Dropping the visor shutter over their own eyes lets the Stagehand briefly scan all hidden layers at once while moving at a careful, deliberate pace.
- **Group of 5 / Raid of 40:**
  - *5 players:* Masks the squad's flank behind a false canyon wall while they reposition, or sweeps a "truth beam" over an armored boss to expose internal puzzle sockets that literally do not exist in allies' views until framed. In an asymmetric riddle encounter, a Stagehand can sweep their frame from a player holding private information onto the rest of the party to share it spatially.
  - *40 players:* Doubles as encounter play and **raid clarity control**. One Stagehand can strip decoy clutter for a specific sub-squad, mask a melee group's rotation from an enemy's perception, or project a clean squad-only route banner across a crowded room without polluting the other 35 players' screens.
- **Combinations:** Reveals hidden fracture sockets for a *Mosaic-Mender*; projects a *Cartridge-Deck*'s private handheld screen onto an arena wall for everyone to see; masks a *Floor-Caller*'s trap tiles from opposing teams in PvP.
- **Puck capability and engine stress:** Runs directly on server-enforced `WorldRecipientView` and `WorldStateDisclosure` (`ReadAudienceEncloses`), never client-side honor-system hiding. *Stresses:* authority CPU and snapshot bandwidth when 40 recipients carry shifting per-viewer disclosure masks every tick (forcing audience-group batching rather than 40 bespoke serializations), ensuring physics contact never leaks masked geometry unexpectedly, and subtle UI/visual grammar so viewers understand *that* a layer is masked or disclosed.

---

## 7. The Kiln-Siphon *(outside lead's frame — live scalar fields & phase-change)*

**Fantasy:** A pressurized-suit meteorologist who vacuums ambient heat, mist, frost, or static into dorsal tanks and blasts it back across the terrain to trigger physical phase changes.

- **Tool and body interaction:** Twin chunky **bellows-tanks** on the back with glowing sight-glasses, plumbed by ribbed hoses to a wide iris-petal **intake cowl** on the left arm and a heavy **venturi nozzle** on the right.
  - *Inhaling, mixing, venting:* Inhaling is an unmistakable full-body pull—left arm thrusts forward, cowl petals flare open, torso leans backward against the suction, and the dorsal pistons visibly rise and shift colour as they fill. Venting swings the right nozzle low across the floor or skyward into a plume, knees bent into recoil, shoulder valves puffing mist. Clapping the intake cowl and nozzle together across the chest cross-mixes two stored fields inside a small mixing chamber (the suit visibly shudders before releasing a phase-changed wave).
- **Group of 5 / Raid of 40:**
  - *5 players:* Turns environmental hazards into ammunition. Vacuum fire off a burning floor to clear a lane, carry that heat across the room, and vent it into standing water to raise a thick steam screen or a thermal updraft that lifts the whole squad onto an upper gallery ledge.
  - *40 players:* Multiple Siphons work as a **thermal bucket brigade**: front-line Siphons inhale lethal heat off the melee stack and vent into a mid-field relay tank, while back-line Siphons draw from that relay to thaw frozen allies or lay down a wide, low-friction frost highway for a rapid raid reposition. Cohesive ground and air layers (frost sheen, shimmer haze, water pools) read far more cleanly at 40 players than 40 overlapping particle fountains.
- **Combinations:** Pours fluid or heat into channels sculpted by a *Lathe-Carver*; pairs with a *Reed-Dancer*'s thrusters to ignite or scatter vented clouds on the beat; feeds tank pressure directly into a *Fulcrum-Smith*'s slide-rule.
- **Puck capability and engine stress:** Built on `FieldLattice` and `FieldLatticeSolid` (coupled evolving scalar fields whose thresholds generate real solid surfaces and wake sleeping bodies). *Stresses:* sparse/chunked lattice stepping so large MMO zones only pay for active cells, painterly SDF rendering of smoothly blended field boundaries in motion, and stable fixed-point transitions (friction, buoyancy, updraft impulses) as 40 bodies cross a moving phase edge.

---

## 8. The Floor-Caller *(outside lead's frame — terrain as a live board game)*

**Fantasy:** A kinetic gamesmaster who stamps a live geometric board onto the terrain and calls out spatial patterns—runs, enclosures, circuits—that trigger whenever moving players and enemies complete them on the tiles.

- **Tool and body interaction:** A weighted **surveyor's staff** topped with a rotating brass **stencil lantern** (projecting crisp hex, square, or Penrose tilings onto the ground) and a wrist-mounted **chime-clicker** that cycles or rotates the active pattern rule.
  - *Stamping and calling:* Planting the board is a theatrical two-handed butt-slam of the staff into the floor; clean geometric borders ripple outward across slopes and stairs. To rotate the grid or shift the rule, the avatar twirls the staff overhead or sweeps one forearm across the chest as if sliding rows on a giant board. Pointing the lantern tip at a single tile flips or pins it with a crisp wrist snap. When a pattern is one piece short of firing, the lantern hums and tilts toward the missing tile so teammates can read the invitation without voice chat.
- **Group of 5 / Raid of 40:**
  - *5 players:* Makes ordinary footwork a group puzzle. The Floor-Caller stamps a seven-hex ring around a skirmish and calls a "three-in-a-row" or "surround" pattern: allies dash onto open tiles to snap a line through a target, or shove an enemy onto the final tile to close a trap. Because the pattern is generic, the same board can trigger recovery waves, barriers, or strikes depending on the rule loaded.
  - *40 players:* Scales into clean **macro-choreography**. Raid Floor-Callers stamp broad zone tiles or chain sub-group micro-boards into a room-wide circuit: completing Squad A's triad on the west ridge illuminates the keystone tile that unlocks Squad B's completion on the east. One glance at the terrain tells 40 people where the next play is forming.
- **Combinations:** Links its pattern rotation to a *Reed-Dancer*'s bar count (musical group hopscotch); projects across multi-tier ramps carved by a *Lathe-Carver*; accepts live rule-table swaps from a *Cartridge-Deck*.
- **Puck capability and engine stress:** Projects `CompiledTopology`, `BoardQueries`, `CompiledPattern` (`$match`), and `PenrosePatch` directly onto live 3D body positions. *Stresses:* projecting crisp, non-shimmering board lines over arbitrary curved SDF terrain and multi-storey galleries, running `$match` updates cleanly as 40 bodies cross tile edges each tick, and keeping tile boundaries unambiguous on slopes.

---

## 9. The Cartridge-Deck *(extends the lead's handheld machine frame into group play)*

**Fantasy:** A retro-futuristic field operator whose forearm console unfolds into a live, playable micro-world cabinet whose cartridge defines the ability—and whose controls teammates can physically share, shoot, or swap mid-fight.

- **Tool and body interaction:** A chunky **clamshell deck** strapped to the left forearm with a hinged matte-glass screen, oversized thumb/shoulder switches, and a spring-loaded **cartridge slot** on the side; a back-mount boom lets it flip out into a waist-high standing cabinet or shield-screen.
  - *Handheld vs. deployed:* In handheld stance, the left arm cradles the screen at chest height while the right hand works chunky physical toggles (a rapid thumb-flick ejects a spent cartridge with a metallic clack and slaps a fresh one home from a hip bandolier). Deployed in group stance, the avatar swings the deck forward onto its tripod boom—the screen enlarges into a diegetic world monitor showing the live emulated cartridge, and the avatar's hands are free while the machine runs.
- **Group of 5 / Raid of 40:**
  - *5 players:* Each cartridge is a real authored Puck machine program: one plays a rhythm-shield timing bar, another runs a miniature pinball bumper that banks real projectiles, another runs a logic-gate puzzle that charges a team beam. Teammates can physically toss cartridges to each other, or stand beside the deployed screen to hit its oversized side paddles together.
  - *40 players:* Deployed cabinets act as **raid command stations and shared mini-objectives**. While 30 players hold the floor, a 4-person specialist crew rotates through a deployed multi-seat console array—or a *Stagehand* mirrors one operator's live screen onto a canyon cliff face so the whole raid can time its moves to the cartridge's display.
- **Combinations:** Any other expression can feed a Cartridge-Deck's input pins (a *Reed-Dancer*'s beat pulses its clock; a *Kiln-Siphon*'s tank pressure drives an analog gauge inside the cartridge), and the cartridge's output registers can drive *Floor-Caller* rules or *Fulcrum-Smith* clamps.
- **Puck capability and engine stress:** Uses Puck's native emulated machines, `puck.cartridge.v1` authoring, diegetic screen-within-a-screen rendering, and nested worlds/portals. *Stresses:* running many live emulated machine instances simultaneously in one authority tick budget without frame drops, smooth handoff of engagement and input routing (`inputModes`) in active combat, and crisp readability of small diegetic screens in motion.

---

## The Unifying System: The Three-Layer Exo-Rig

A "two hands plus totem" model gets cramped quickly—some expressions genuinely want two hands (a vernier beam, caliper gauntlets), and **dance fighting is not something you hold in your hand**, it is how your legs, hips, and thrusters move.

A cleaner, more Puck-native frame—authored entirely in `worlds/standard/`, with no hardcoded class enums in engine code—is a **Three-Layer Exo-Rig**:

1. **Manipulator (Arms & Hands — *What you shape or play*):** One-handed pair or two-handed coupled hardware (Reed-Horn, Caliper Gauntlets, Vernier Beam, Rotary Lathe, Framing-Gloves, Stencil Staff, Venturi Nozzle, Cartridge-Deck). Owns upper-body rig poses and primary input channel grammar.
2. **Chassis (Legs, Torso & Thrusters — *How your body moves and generates energy*):**
   - *Thruster-Dance* (spins, kicks, slides, handstands that pump air/momentum on beat)
   - *Anchor-Brace* (heavy cleats and gyro-locks that absorb recoil for steep ratios or heavy carving)
   - *Hover-Glider* (FireFall/Anthem jump-jets for aerial framing, stamping, and diving)
   - *Vault-Skate* (low-friction momentum surfing across sculpted ramps and frost sheets)
3. **Core (Back / Hip — *What persistent engine scope you carry*):** Bellows/Tank Pack, Die-Wheel, Pendulum Escapement, Shutter-Rig, or Cartridge Bay.

**How class and combinations work without combinatorial bloat:**
- **Class is a named rig preset, never an engine type.** A player can load a classic preset (*Reed-Horn + Thruster-Dance + Bellows* = Reed-Dancer) or mix modules freely (*Reed-Horn + Anchor-Brace + Kiln-Tank* = Steam-Organ Sentinel; *Lathe + Vault-Skate + Die-Wheel* = Ramp-Sculpting Surgeon). Following Puck's world model, *a character claims a rig, and each visited world clamps or themes what it admits*.
- **Generic physical ports instead of `N × M` special cases.** Modules publish and subscribe to a tiny set of authored `.puck` signals: **Beat/Clock**, **Pressure/Momentum**, **Field/Shape**, **State-Signal**, and **View/Screen**. Because *Thruster-Dance* emits beat-synced momentum and *Reed-Horn* consumes pressure, they interlock automatically; swap the *Reed-Horn* for a *Kiln-Siphon* nozzle and dance kicks now puff rhythmic gusts—zero bespoke class code required.
- **40-player readability by architecture ("Silhouettes + Shared Buses").** Every expression is built to read from posture and chunky articulated hardware first, never from noisy particle halos. At raid scale, individual tools bind to shared encounter anchors (one brass-section `MusicClock`, squad keystone rings, master vernier pylons, terrain-projected boards, smooth lattice surfaces) so 40 players compose into one legible stage.

---

## Build First: Three Forcing Functions

Build these three first in the debug world. Together they stress Puck's three hardest pillars—**feel/audio/rigging**, **live SDF/contact/navigation**, and **multiplayer state/topology/readability**—and they compose into a playable group loop on day one before any combat numbers or loot tables exist.

1. **The Reed-Dancer (Reed-Horn + Thruster-Dance Chassis)**
   - *Why first:* Directly proves the owner's core vision and forces **feel** ahead of everything else. It exercises upper/lower-body rig layering on Moth's suit, sub-tick rhythm input against `MusicClock` (`$clock:<music>:phaseError`), moving diegetic `VoiceSynth` speakers, and how a Chassis slot changes locomotion and feeds a Manipulator. If playing a horn while thruster-sliding across the canyon floor feels joyful in the debug world, the engine's feel bar is real.
2. **The Lathe-Carver (Rotary Gouge + Blend-Trowel)**
   - *Why second:* The sharpest forcing function for Puck's signature spatial technology—**live SDFs**. Carving a real alcove or filleting a skateable ramp out of the debug valley and immediately running or sliding up it stresses real-time SDF updates, `WorldSolidField` contact without tunneling or depenetration bugs, and incremental `NavigationRuntime` rebakes (`TryBeginRebakeFrom`) under real play. Solving live SDF edits here also lays the exact technical path for *Mosaic-Mender* next.
3. **The Floor-Caller (Stencil-Lantern Staff)**
   - *Why third:* Proves **puzzle play and group interdependence** using Puck's native `CompiledTopology` + `$match` engine right on the debug world's canyon floor—with zero combat math required. It forces clean terrain-conforming board projection over uneven SDF geometry, fast pattern matching over moving bodies, and instant multi-player legibility. Better yet, it immediately composes with the first two: carve a multi-level ramp with the Lathe-Carver, stamp a board across it with the Floor-Caller, and run a rhythmic musical puzzle across it with the Reed-Dancer.

*(Once multi-seat sessions and cross-body disclosure/coupling are ready to stress next, **Stagehand** and **Fulcrum-Smith** are the immediate second wave—each attacking a distinct MMO-scale frontier: server-enforced per-viewer truth and deterministic cross-body state coupling.)*

