# Classes as things the avatar does: nine concepts (Fable)

One voice of a three-family ideation round on the bard, the puzzle-healer and
the spreadsheet-mage. The owner asked for avatar interactions first and
mechanics left open. So each concept starts from what the body does with a tool
and says only enough about play to show how people depend on each other.

One idea runs through all nine. Puck's simulation is the truth, and an avatar's motion can be part of it. So a class action should be something you can watch happen in the world (a note that pushes you, a wound that is a hole, a card that is in a hand), never a bar filling.

Concepts 1 and 2 are the owner's bard. Concepts 3 to 9 are outside the lead's frame.

## 1. The Jet Horn (the bard's instrument)

**Fantasy.** You play a horn that breathes through your suit's thrusters, so every note you play also moves you.

**Tool and body.** The Moth's suit already carries two flight pods with nozzles [S1]. The horn is a short valved instrument that plugs into the pod line, so the pods are the lungs.
- Right hand: four valves, one finger each, on the pad's four face buttons.
- Left hand: holds the horn's body and slides a shroud over the bell (left bumper).
- Breath: trigger pressure sets how hard the pods blow, shown as chest swell and nozzle flare.
- Aim: the right stick or gyro points the bell.

Each note vents a small burst of thrust away from the bell. That is the authored effect the standard dash already uses (`planarImpulse` in `dashAbility` [S2]). A held low note is a hover, a staccato run is a strafe, and a phrase is a path across the floor. "The avatar will move as one might expect" becomes literal: she leans against her own recoil.

Notes are judged against the music clock, which is simulation state, using the press's time inside its tick. A debug-world fixture already does this with `clock(pulse, phaseErrorAt, 1, inside, Press, 0)` and grades at sub-millisecond precision [S3].

**Group of 5.** The bard does not hand out a number. The bard sets the tempo and the count, and every other tool's rule can ask the same clock how far from the beat a press was. Speed the song up and everyone's windows come sooner. Call a bar of rest and acting in it is punished.

**Raid of 40.** Forty soloists cannot be heard, so bards lead sections. The score's conditional layers are recomputed every step from that tick's events [S4], so a layer comes in as each section lands its part. The soundtrack becomes the raid's status display: a missing layer is audible to everyone. A crowd of forty avatars cannot be read by eye, but it can be read by ear.

**Combines with.** The horn owns the hands and the breath and leaves the feet free, which makes Footwork (2) its partner. A whistle register could command a companion. With the Echo (6), the bard plays a round with herself.

**Puck capability and stress.** It uses the deterministic music clock, sub-tick press time, synthesized instruments and authored kit effects. It stresses:
- the held-tool rig: two hands kept on a moving tool, finger motion, and an upper body layered over walking legs;
- audio latency calibration;
- remote ensemble: hearing another player's note in time when their press arrives late.

## 2. Footwork (dance fighting)

**Fantasy.** You fight by dancing, and the dance is real motion that hits what it sweeps through.

**Tool and body.** There is no tool; hips and feet are the instrument. The left stick and the beat choose a step: a flick on the beat is a step that way, a direction held across two beats is a slide, a circle is a spin. Each step is an authored motion with a visible wind-up.

The readable-interaction plan's idea applies exactly: "A motion program is a pure function of time and state, so the engine can compute the volume a moving part will sweep over the next ticks" [S5]. The beat is the commitment point. A kick lands because the boot's swept volume met something.

It is funny because the moves are big (a mech suit doing a windmill with thruster flare) and because the feet keep dancing while the hands do something serious.

Partner moves: two dancers who step toward each other on the same beat join hands and get a two-body move, such as a lift, a throw or a swing that flings the lighter player across the room. The rig's limb solvers can already target another body [S6]. Whoever committed first leads.

**Group of 5.** Steps are how anyone, not only the bard, stays in time. A healer who side-steps on the beat keeps their hands' work uninterrupted. A formation step (a line, a circle) is the group's answer to an area attack.

**Raid of 40.** A line dance. A boss attack avoided by forty players doing the same four-beat figure reads from any distance: you see the wave travel through the crowd and you see who is off.

**Combines with.** Footwork is the lower-body half of any loadout. With the horn, the note's thrust and the step add up: a note played on a spin is a spinning jet sweep.

**Puck capability and stress.** It needs simulation-side swept motion, first contact between two fields, and sub-tick presses. Today the rig is presentation only: "None of it moves a collider or the simulation" [S6]. So dance fighting needs steps 1 and 2 of the readable-interaction plan built. It also stresses two-body rigs.

## 3. The Mender (a puzzle-healer: damage is shape)

**Fantasy.** Wounds are pieces missing from your friends, and you heal by shaping the piece that fits and pressing it back in.

**Tool and body.** A satchel of soft slip on the hip, and a shaping hand. Damage carves a real hole in an ally's armour: a subtraction in their signed distance field, placed where the hit landed.
- Off hand: pulls a lump from the satchel.
- Right hand: shapes it. Twist the wrist to turn it, squeeze the trigger to swell or pinch it, from a small set of primitive shapes.
- Palm: presses it into the wound.

Fit is measured, not timed. How well the lump's field fills the hole's field is an exact distance comparison. A good fit blends smooth; a poor one leaves a seam that fails sooner.

**Group of 5.** No health bars are needed, because everyone can see who is missing what. A wounded ally presents the wounded side: the tank turns a broken shoulder toward the Mender between hits, which makes facing a cooperative choice. The Mender can shape a piece ahead of time and throw it to someone to press in themselves.

**Raid of 40.** Triage by silhouette. Menders split the work: one roughs out blanks, another fits them. A breach in a wall or a bridge is the same mechanism on a structure, with three menders shaping one large piece.

**Combines with.** With the horn, a press on the beat sets faster. With the Actuary (5), the Mender sees the hole a committed hit will make and has the piece ready. With the lead's action alchemy, the slip's material is a compound.

**Puck capability and stress.** It is signed distance fields as the source of every shape: carving, smooth blends and exact fit. It stresses a gap the tree already names. A cutter that moves on its own makes its group bake again, and "contact still reads the cut at rest" [S7]. Forty avatars with forty different wounds cannot share one bake, so the engine needs a carve per instance whose cost follows the size of the change.

## 4. The Groundskeeper (a puzzle-healer: the ground does the healing)

**Fantasy.** You do not heal people; you make the ground good for them and steer it to where they stand.

**Tool and body.** A long hoe in two hands and a tank on the back. A world's ground can carry field rows on a lattice: a quantity per cell that spreads, fades and runs downhill by authored reactions (`diffuse`, `decay`, `flow` [S8]).
- Pour: kneel, palm down, and the tank empties into the cell.
- Channel: drag the hoe to cut a path.
- Dam: push to bank earth up; tamp with the butt to seal it.

The puzzle is hydrology in real time. The quantity flows where the surface sends it, and allies are wherever the fight puts them.

**Group of 5.** Allies help by where they stand. Bodies already deposit into the cell they stand in (`emit` [S9]), so a burning ally warms the flow as it passes and a frost one freezes a dam. The tank chooses to fight at the bottom of the basin. Enemy effects are the same fields, so the Groundskeeper is also the group's firefighter.

**Raid of 40.** Landscape scale: three groundskeepers run an irrigation system across a valley, and the raid leader calls which basin the fight moves to. The fields show on the ground for everyone.

**Combines with.** The Clockmaker's slow dome (9) holds a pool. The lead's geometer makes terrain and the Groundskeeper runs it.

**Puck capability and stress.** It uses field lattices that evolve deterministically, are read by bodies and are drawn by the renderer. It stresses lattice cost at fight pace with many bodies, and digging is the moving cut of concept 3 again. The design page says lattice topology and cadence changes "remain restart-required" [S8].

## 5. The Actuary (a spreadsheet-mage: what-if)

**Fantasy.** You run the numbers on the next few seconds and show your party the answer before it happens.

**Tool and body.** A slate in the off hand and a stylus in the other. What a spreadsheet is really for is "what if": change one input and see the outcome. Puck's state can do that for real. The rule engine opens a scope, applies changes, reads the result and takes it all back; the search that plays out moves for a computer opponent works this way [S10].
- Select: circle something in the world with the stylus (aim and a short trace).
- Assume: write an assumption on the slate (a short chord).
- Read: the slate plays the branch out a bounded number of steps and shows the result as a ghost in the world. Examples: where a committed swing lands, which of two wounds fails first, what the hand is worth if this card is played.
- Share: flick the result off the slate toward an ally.

The posture is head down, scribbling, glancing up. It is readable and exposed. A forecast has a price, because the rule engine already prices a tick's worst-case work [S10]. That price is what the mage spends, and a deeper or wider what-if costs more.

**Group of 5.** A forecast shows only to the players the Actuary flicks it to. The skill is choosing who needs to see what. The role lives on other people's decisions.

**Raid of 40.** Actuaries are the raid's analysts. Each watches one thing and publishes one ghost or one number to a section leader. An author can build telegraphs that only a forecast reveals.

**Combines with.** Everything. With the Dealer (7) the Actuary counts cards, inside what their seat may read.

**Puck capability and stress.** It uses determinism, journal scopes, search, costed execution and per-viewer disclosure. It stresses two things:
- Search covers rule state, but body motion lives outside it. Predicting bodies is the readable-interaction plan's prediction and rollback, "a dependency with no design yet" [S5].
- A forecast must not leak hidden state. The disclosure plan's rule is "Search cannot widen its inputs' audiences" [S11].

## 6. The Echo (a loop pedal for the body)

**Fantasy.** You record a few seconds of yourself and set it running again beside you while you do something else.

**Tool and body.** A tape reel on the back and a crank at the hip. Crank forward to record, and the reel visibly spools. Pull the handle to release.

The echo is not an animation. It is the recorded commands driven again through the same doors as live input, with the world's rules deciding what they mean. Scoped replays work this way today: "Replay uses the same doors and live grants" [S12]. An echo's kick is a kick, and if the target has moved, the echo misses.

**Group of 5.** Lend your echo: record a phrase and hand the reel to an ally, who releases it where they need it. A bard's echo keeps the beat while the bard solos. Two players record a partner dance and release a pair.

**Raid of 40.** A chorus: ten players with a few echoes each fill a figure. Echoes draw simplified and tinted so the crowd can tell live from recorded. The cap is the world's to author.

**Combines with.** Anything. It multiplies whatever the other slots do.

**Puck capability and stress.** It turns determinism into play: same commands, same rules, same result. Today's scoped replay restores and re-drives the same bodies. An echo needs the tape driven onto a second body from a moved start, and many more bodies in the room.

## 7. The Dealer (the casino class)

**Fantasy.** You deal a hand to your party, and the fight is played with it.

**Tool and body.** A deck in a wrist holster.
- Shuffle: a riffle between both hands, which is a real shuffle of a seeded stream.
- Fan: the left hand opens your hand of cards, faces toward you.
- Deal: the right hand flicks a card, aimed, to an ally, who catches it and holds it face-in.
- Play: slap a card onto an ally, an enemy or the ground.

The 52 cards already exist as shapes [S13]. Who sees a face is the world's disclosure policy: "a dealt hand only its seat reads" [S11].

**Group of 5.** The party plays a hand of a real card game against the encounter while fighting. Each player holds two or three cards only they can see. Sets and runs made across players are the combination, and the rule engine already matches hands [S10]. A pass is a physical throw that can be intercepted. In player-against-player play the other side sees only backs, so bluffing is real.

**Raid of 40.** Forty hands in one shedding game; the tree already has one [S14]. The raid must play out in order, and "who holds the seven?" is a callout. The Dealer's skill is choosing who gets what.

**Combines with.** Play on the bard's beat. The Actuary counts. A deck could also be a cartridge's game on the lead's handheld.

**Puck capability and stress.** It takes per-viewer disclosure, seeded draws and the tabletop rules off the table and into moving bodies. It stresses:
- disclosure with forty recipients while bodies move;
- pictures that differ per viewer (a face drawn only for its holder);
- passing an object hand to hand inside one world.

## 8. The Tabletop Hand (a player outside the room)

**Fantasy.** One of you is not in the fight: they stand over a table-sized copy of it and reach in.

**Tool and body.** A framed table in another world, such as the hub or the player's own. Through the frame the battlefield shows small. A portal pair whose two faces differ in size already scales what crosses it; that is the Wonderland painting and mouse hole [S15].

The Hand player leans over the table and reaches an arm through the frame. To those inside, a gauntlet the size of a house comes down through a window in the sky. It can pick an ally up and set them down, block with a palm, or point with a finger everyone can see.

**Group of 5.** The seat rotates: each player takes a turn at the table while their body waits in an alcove. The Hand sees the whole board and the four see detail, so each needs the other's view.

**Raid of 40.** The commander's chair made literal: one or two Hands, who see the layout and point. A raid could face an enemy Hand.

**Combines with.** The Dealer deals from above. The Clockmaker's globe (9) is a table too.

**Puck capability and stress.** It uses worlds within worlds, windows that are doors, and scale across a pair. It stresses:
- crossing: a crossing moves a whole body today, and an arm through a frame is one body in two worlds at two scales;
- rendering: the same world drawn at two scales for different viewers;
- grants: what a visiting hand may move.

## 9. The Clockmaker (pocket worlds on their own clock)

**Fantasy.** You carry a small world that runs at its own speed, and you put things in it.

**Tool and body.** A glass globe on a chain and a winding key. Every Puck world declares its own simulation rate, and a rate write "is a durable live rate change" [S16]. The globe is a small world.
- Open: set it down and pull the halves apart with both hands. It becomes a dome you walk into through a seam with no frame.
- Wind: turn the key, and the arm cranks, to raise or lower the dome's rate.

Inside a fast dome everything happens faster, you and the music included. It is haste you have to keep up with: you get more done per outside second only if you can play at that speed. A slow dome gives a Mender room for a careful fit while the outside moves on.

**Group of 5.** The choice is who goes in. Put the extra enemy in a slow dome, or the bard in a fast one to play double time. The seam has a width, so leaving is a commitment.

**Raid of 40.** Domes as stations: a slow field hospital and a fast dome for the damage group. Each dome shows its rate with a visible pendulum.

**Combines with.** The bard: the inside and outside tempos stand in a ratio, and two to one is double time.

**Puck capability and stress.** It uses per-world time and authority. It stresses bodies crossing a hidden seam many times a minute between rates, and one music clock heard across two rates. The owner's deferred call on layer moves waits on a layer mechanism that does not exist yet [S17].

## The system that ties them together

**Class is a posture.** I would make the slots parts of the body. Each has its own piece of the controller and its own piece of the rig.

| Slot | Controller | Rig | Examples |
|---|---|---|---|
| Hands | face buttons, bumpers | arms, fingers | horn valves, slip, slate and stylus, deck |
| Breath | trigger pressure | chest, pods | the horn's air, a whistle |
| Feet | left stick, jump, dash | hips, legs | Footwork, or a planted stance for careful work |
| Back | none of its own | the pack mount | reel, tank, frame, globe |
| Companion | the wheel | a separate body | the faerie holds a second tool, or stays as a totem |

Three reasons to prefer this to "two hands and a totem":
- It is what gets built first. Each slot is one rig layer and one input page, built and judged alone.
- Two expressions combine without a special case when they own different parts. Horn and Footwork work together because one owns hands and breath and the other owns feet.
- Conflicts are physical and visible. The Mender needs both hands, so the Mender cannot hold the deck, and no rule table is needed.

The class name is text derived from the slots (Horn with Footwork reads as a bard, Slip with Stance as a mender). It is displayed and never stored.

**The engine already has most of the mechanism.**
- A body register already picks how the Moth moves and looks: "the look and kit assignments select by it" [S18].
- The game design page describes "a binding overlay swapped by a row" [S8].
- The world model lists "Loadout presets. A named set of slot values and something that applies them—authored data plus a batch of writes" [S19].
- An ability is already a template expanded into a kit (`dashAbility` [S2]).

So a tool is a standard-package template. It contributes a shape parented to a hand or the back, rig poses, kit actions, an input page and rules. The owner's ruling "A character claims, a world clamps" [S20] covers the rest: the loadout is the character's, and each world admits or restyles tools.

**Tools combine through calls and answers, not pairs.** Authoring a verb for every pair of tools grows with the square of the tool count. Instead, a tool never knows another tool. Using one opens a typed, visible call in the world's state: a beat window, a hole of this shape on this body, a forecast branch, a dealt card, a dome. Any tool's rules may answer a call of a kind they read. A new tool adds a call kind or some answers, and combinations fall out. The music clock is the one call everyone can answer, which makes the bard the group's hub without making a bard required.

**The hand-off is the unit of interdependence.** A shaped patch, a card, a reel and a globe all pass from one body to another. One mechanism (throw, catch, hold) serves them all, and it reads in a crowd because you can see the thing travel.

## What I would build first

1. **The Jet Horn with Footwork.** It is the owner's first ask, and most of its substrate exists: the clock fixture, synthesized music and kit effects. It forces the held-tool rig every other concept needs, and the first dance step that truly hits forces simulation-side swept motion. It also gives every later tool the shared clock.
2. **The Mender.** It puts the engine's most distinctive claim onto characters: carve and blend per instance at fight pace, with contact that reads the cut. The gap is already named and designed [S7], and the Mender makes it urgent. It also tests "no health bars", the idea here most likely to change how the genre plays.
3. **The Dealer.** It takes disclosure off the table and into a moving group, which the MMO needs for everything hidden. The card stack exists. It also forces the hand-off.

The Echo is a cheap rider on the first, if driving a tape onto a second body turns out to be a small change. Someone should read the replay runtime before promising that.

The Tabletop Hand and the Clockmaker are the most Puck-specific, but they wait on designs that do not exist (a body partly through a frame, the layer mechanism). They should be the forcing functions for those designs when the lead opens them.

## Smaller sparks, not developed

- **Link cable.** Two players cable their handhelds, and the pair of cartridges is one co-operative ability set. Linked machines already "advance once as a coupled group" [S21].
- **Murmuration.** A conductor steers a real flock whose bodies blend into one surface that becomes a shield, a bridge or a lance.
- **The Namer.** Spells are words spelled in time, spoken aloud by the synthesized text voice. The input decision already says typing is judged the way rhythm is [S22].

## Engine gaps these concepts need

These are things a concept needs that the round did not find in the engine. Each comes from the documents and authored files in the sources list; no C# source was read, so every one is a lead to check.

1. **Limb motion is presentation only.** The rig reference says of the rig "None of it moves a collider or the simulation" [S6]. Footwork's hits, the Mender's press and every hand-off need a limb's swept volume inside the tick. That is steps 1 and 2 of the readable-interaction plan [S5], which are not built.
2. **Rig drivers do not read input channels.** The driver signal table lists travel, speed, turn rate, accelerations, `state.<row>` and `driver.<name>` [S6]. A finger pressing a valve would need a rule copying the channel into a body cell first. Whether each Moth digit is its own shape that can take a swing was not checked; [S1] says only that digits are quadratic sweeps.
3. **Eight limb solvers per creation.** "`effectors` is up to 8 CCD solvers" [S6], and the Moth uses two for her feet. Two hands on a tool fit; a solver per finger does not, so fingers would be swings or frames.
4. **Scoped replay re-drives the same bodies.** A cancel "restores that start", `replay.scope` is operator-only, and "Field, vector, timed and pool rows are refused" [S12]. The Echo needs a tape driven onto a second body from a moved start.
5. **A moving cut bakes its whole group again, and contact ignores it.** The validator's warning says "the group bakes again each time it moves a step of the group's tier cell" and "contact still reads the cut at rest" [S7]. No surface was found for a cut placed at a point read from body state, or for a carve per instance that keeps the shared bake. The Mender and the Groundskeeper both need this.
6. **A crossing moves a whole body.** The Wonderland pair maps position, velocity and heading and multiplies the `size` register, whose range is 0.25 to 1 across faces "a power of two apart" [S15]. The Tabletop Hand needs a body partly through a frame, at a wider scale ratio.
7. **No layer mechanism or hidden-seam crossing at fight pace.** Deferred call 3 says the standard's layer move comes back "when the layer mechanism exists" [S17]. Nothing was found on one music clock heard across worlds at two rates. The Clockmaker needs both.
8. **Remote ensemble has no design the round found.** Late input is admitted under an authored policy [S22], but nothing read covers hearing another player's note in time. Body-placed voices are open too: a babbling identity "has no live-body correlation yet" [S4].
9. **Search does not cover body motion.** The state skill describes search over journal scopes of rule state [S10]. Forecasting bodies is prediction and rollback, "a dependency with no design yet" [S5]. The Actuary's ghosts of moving things need it.
10. **Per-viewer pictures are partly open.** The disclosure plan says "a narrowing inside "disclosed" moves no level" and routes the remainder to the security packet [S11]. A card face drawn only for its holder in a moving crowd depends on that work.
11. **Hand-to-hand passing is unconfirmed.** Kits have a `carry` facet beside `rigid` and `tether` [S23], but throw, catch or passing a carried thing between two bodies was not found.

## Sources

- [S1] [The Moth flight studio](../../../worlds/debug/avatars/moth.md)
- [S2] [The standard abilities](../../../worlds/standard/abilities.puck)
- [S3] [The input clock fixture](../../../worlds/debug/input-clock-fixture.puck)
- [S4] [Audio mixer, synthesis, and music direction](../../reference/audio.md)
- [S5] [Readable interaction](../readable-interaction.md)
- [S6] [The rig reference](../../../.claude/skills/sdf-authoring/references/rig.md)
- [S7] [Blends between parts that move](../moving-blends.md), its section on gap 8, a cutter that moves against the surface it cuts
- [S8] [Reference game design](../../game/design.md)
- [S9] [The state document reference](../../../.claude/skills/puck-world/references/documents-state.md)
- [S10] [The state skill](../../../.claude/skills/state/SKILL.md)
- [S11] [Disclosure](../disclosure.md)
- [S12] [The replay reference](../../../.claude/skills/puck-world/references/replay.md), its section on scoped live replays
- [S13] [The standard deck](../../../worlds/standard/cards.puck)
- [S14] [The shedding game](../../../worlds/games/shedding.puck) and [its debug-world table](../../../worlds/debug/shedding.puck)
- [S15] [Wonderland](../../../worlds/debug/wonderland.md)
- [S16] [Worlds and federation](../../architecture/worlds.md), its section on authored per-world time
- [S17] [MMO foundations](../mmo-foundations.md), its deferred calls
- [S18] [The Moth's suit](../../../worlds/debug/suit.puck)
- [S19] [Worlds and federation](../../architecture/worlds.md), its section on compositions supported by the model
- [S20] [Play: decisions](../../decisions/play.md), its section on the game
- [S21] [Machines and cartridges: decisions](../../decisions/machines-and-cartridges.md)
- [S22] [Input channels](../../decisions/input-channels.md)
- [S23] [The motion document reference](../../../.claude/skills/puck-world/references/documents-motion.md)
