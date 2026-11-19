# Instrument and dance play

The avatar plays a wind instrument and fights by dancing, both in time to the
world's music. This is one feature with one owner. It is chosen for what it
stresses as much as for what it adds: fingers and hands on a rig that has only
moved limbs, a whole body thrown through large fast moves against the contact
field, presses judged inside a tick, sound and motion that must agree, and a
crowd of such bodies at the frame bar.

This plan states the feature as the owner described it, the systems it rests
on, the engine gaps it names, the stages with the check that accepts each one,
the lanes that build it, the research it owes and the brief for its concept
art. It is the first of a family of
[play expressions](play-expressions.md), and the only one planned so far.

## Summary

- **Most of the timing machinery is built.** The simulation already has a beat
  clock, presses stamped inside a tick, a rule operand that measures a press
  against the nearest beat at the moment the press was captured, a per-player
  calibration offset, and timed input patterns for taps, holds and motions.
  The first playable stage is mostly authored data in the standard package.
- **The body is where the engine falls short.** The rig can swing limbs and
  plant feet. It cannot blend from one authored pose to another, cannot aim a
  fingertip at a point on its own body, and cannot react to a press before the
  next tick. A body can dash in a straight line and cannot turn or follow a
  shaped path over a move. These are the feature's first lanes.
- **Sound stops at the process boundary.** A rule can fire a sound cue, but the
  cue reaches the speakers only when the authority runs in the same process,
  and it cannot carry a pitch. Music can react to a body entering a region and
  not to what a rule decides. Both are gaps on the path to the remote topology
  the frame bar is read in.
- **The play style is one rhythm with two expressions.** Breath and fingering
  play notes; steps, spins and strikes are dance moves. Both are steps of one
  authored phrase judged against one clock, so a phrase can pass between the
  hands and the body. The instrument is worn, which leaves the hands free to
  drop into a move and lets the avatar play while the legs dance.
- **Nothing here asks the owner for a decision.** The looks, the windows, the
  phrases and the effects are authored. Two feel sittings and the windowed
  checks are requested in [what the lanes ask of the lead](#what-the-lanes-ask-of-the-lead).

## What the owner asked for

The owner's words, verbatim, kept as evidence beside the intent:

- "Our avatar should be able to play some sort of wind instrument, like an
  Ocarina (mentioned here only so you understand what I mean, DO NOT copy the
  design). This should push our rendering in the sense that our avatar will
  have to properly articulate and have nice visual fidelity in the
  finger/hand/arm/body motions required for interesting stylized animations.
  The player will press buttons in time to some music and when they do the
  avatar will move as one might expect. This is work that goes across several
  systems in order to prove/stretch Puck's capabilities. Consider an active
  bard play style, in an action MMO setting, modernized for the futuristic
  sort of design that we're going for with moth (FireFall, MegaMan, and
  Anthem) also come to mind which should be obvious from our current moveset
  and action controls."
- "Dance fighting is a hilarious concept that the bard should employ to act as
  an additional expression which might interplay with the instrument. Also
  stresses the kinematics nicely."

The intent, in this plan's words:

1. The avatar visibly plays. Each press moves the finger, hand, arm and body
   that a player would expect it to move, and the motion holds up close.
2. Playing is timed. The player presses in time to music the world is
   playing, and the timing matters.
3. It is a way to play an action game, used while moving and fighting, and it
   sits beside the dash and the existing controls.
4. Dancing is a second expression of the same rhythm. Dance moves are attacks,
   they are funny, and they trade off with the instrument.
5. The instrument and the moves are original and belong to the Moth's
   futuristic direction. No existing instrument or game is copied.
6. The work is meant to load several systems at once and show where they must
   improve.

The rulings that bind every lane are in [AGENTS.md](../../AGENTS.md): authors
rule, so every look, window, phrase, move and effect is `.puck` data in
`worlds/standard/` with a neutral engine default; behaviour is an authored
program, never a C# feature; the engine carries no instrument or dance
vocabulary; the debug world is the proving ground, and a feature does real
work there.

## The play style

This section is the design the stages build. Every value in it is the standard
package's to choose and an importing world's to override.

### One clock, and the world owns it

A world declares one score, and its beat clock is simulation state that every
player in that world shares. Every bard and dancer in a world is therefore in
time with every other one without any agreement between them, and a crowd of
players forms an ensemble by construction. A portal leads to a world with its
own tempo, which is how the gallery can teach tempo by crossing a threshold.
A press in one world is never judged against another world's clock, which the
[input decision](../decisions/input-channels.md#sub-tick-time-and-clocks)
already requires of a pattern.

### A phrase is authored steps on beats

A phrase is a short authored sequence. Each step names a beat and what the
player does on it: a note, a dance move, or either. A rule grades each step by
how far the press fell from its beat, and the grades add up to the phrase's
strength. A completed phrase releases its authored effect: a shield on nearby
allies, haste, a damaging resonance, or a finishing strike.

The starting frame said a missed beat breaks the phrase. This plan authors a
softer default and leaves the hard break as one value an author may choose: a
miss lowers the phrase's strength by a step and the phrase continues, and two
misses in a row end it. A player who is also steering, dodging and reading a
fight should lose power for a slip, not everything. The play decisions already
name generous judged windows as the precedent
([the game](../decisions/play.md#the-game), the music ruling).

### Breath and fingering

The instrument is a wind instrument, so it takes air and fingering, and the
controls keep both:

- **Breath** is an analog control, one trigger by default. Holding it raises
  the instrument, and its pressure is the air. Releasing it lowers the
  instrument and returns the buttons to their ordinary actions.
- **Fingering** is four buttons under one thumb. Each button is one vent, and
  one finger stops it.

The left stick stays free, so the avatar walks, runs and strafes while it
plays. Because each button is one finger and the trigger is the breath, the
motion a press produces is the motion a player expects, which is the owner's
third sentence. The binding is rows in the standard's controls, where trigger
pages and held modifiers already exist.

### Dance moves

A dance move is a full-body action with real displacement: a step, a slide, a
spin, a kick, a leap. It is entered as a direction and a button on the beat,
the way a fighting game enters a motion. A move travels through the contact
field like any other motion, so a spin into a wall stops at the wall and a
slide off a kerb steps down. A move's active ticks strike what they reach, and
a strike that lands on the beat is stronger.

The joke the owner asks for comes from commitment. A move is large, it is
readable from across the stage, and it is exactly on the music. Enemies struck
on the beat are knocked into the next beat's move.

### How the two interplay

Three interplays are authored in the first standard set. They are examples of
the mechanism, and authors write others.

1. **A phrase passes between hands and body.** A step of a phrase may be a
   note or a move. "Note, note, spin, note" is one phrase.
2. **A move sets up or finishes a song.** A move that lands on its beat fills
   the next phrase step for free, and a completed phrase can end in a named
   finishing move that carries the phrase's effect as a strike.
3. **Playing while dancing.** The instrument is worn, so the arms and hands
   play while the legs and hips dance, wherever a move leaves the arms free.
   A move that uses the arms (a handspring, a strike with the forearm)
   suspends the fingering for its length and the phrase holds its place.

### Why the instrument is worn

The starting frame suggested a worn resonator. This plan adopts it, for three
reasons that come from the feature and not from taste. A worn instrument
leaves both hands free to drop into a move on the next beat. It lets the
avatar play while running without a hand occupied by carrying. And the Moth's
gauntlets already carry vents on their outer faces
([the Moth pack](../game/art/moth-concept-pack/README.md#preserve-the-character)),
so the instrument grows from her suit. Where the air comes from, how the vents
are laid out and how they glow belong to the
[concept-art brief](#the-concept-art-brief).

## What exists, by capability

Each row was found by searching for the capability, then read in the code or
the document named. The searches are listed in
[systems considered](#systems-considered).

| The feature needs | What already does it | Where |
|---|---|---|
| A beat clock in the simulation | `MusicClock` counts engine ticks into beats and bars from a score's authored tempo. The server steps it and hashes it. | `Puck.Audio.Simulation`; a `music` row naming a `puck.music.v1` document ([audio reference](../reference/audio.md)) |
| A press timed inside a tick | The host quantizes each device event to a tick and an offset in Q48.16 seconds on the 50,400 Hz engine clock. Several presses in one tick stay ordered. | `WorldInputInterval`, `WorldInputEvent`; [sub-tick time](../decisions/input-channels.md#sub-tick-time-and-clocks) |
| A press judged against a beat | The rule operand `$clock:<music>:phaseErrorAt:<owner>:<channel>:<kind>:<occurrence>` returns the signed distance in seconds from the press, at its captured time, to the nearest beat. | `WorldInputMusicSegment.PhaseError`; the worked rule is [`input-clock-fixture.puck`](../../worlds/debug/input-clock-fixture.puck) |
| Latency compensation | Each input owner carries a signed calibration offset that the judgement adds. It is authority state, hashed and replayed, bounded by `inputHistory.calibrationLimitSeconds`. | `WorldInputHistory`, `WorldInputRecord` |
| A late press over a network | A late event keeps its captured time and is judged by it, within an authored lateness bound. | `WorldInputHistory.MaximumLatenessSeconds`, `WorldInputJudgement` |
| Tap, hold, a held note, a motion, a buffered press | Authored `inputPatterns` rows over timed history, read with `inputMatch` and claimed with `consumeInput`. Axis crossings give stick flicks. | `WorldInputPattern`; [`input-pattern-tests.puck`](../../worlds/debug/input-pattern-tests.puck), [`input-continuity-tests.puck`](../../worlds/debug/input-continuity-tests.puck) |
| Buttons that mean notes only while the instrument is up | `inputModes` families selected by authoritative state, with keep sets that decide what survives a switch. Trigger pages and held modifiers in the standard's bindings. | `WorldInputModes`; [`controls.puck`](../../worlds/standard/controls.puck), [`pad-kit.puck`](../../worlds/standard/pad-kit.puck) |
| A rule that runs once for each player | A rule names an input owner by seat, by a state cell, or as the owner of a body. | `WorldInputOwnerReference` |
| An authored phrase and its grades | State rows, rules with `Edge` and `Level` modes, `schedule` for due ticks, rule groups for staged phases. | The `state` skill's map; no new engine vocabulary |
| An ability beside the dash | A kit `actions` row with a press trigger, a gate, a latch and effects, expanded from a standard template. | [`abilities.puck`](../../worlds/standard/abilities.puck), `ActionSpec` |
| An effect on allies or enemies in range | An `interactions` row pairs bodies that carry two authored properties within a range, nearest first, and runs rule effects on the pair. | `WorldInteraction` |
| A body moved by an ability | `planarImpulse` rides one direction at one speed for a duration, through the body's ordinary contact sweep. | `WorldEffect.PlanarImpulse`; [the physics skill](../../.claude/skills/physics/SKILL.md#the-body-step-and-the-seam-door) |
| A one-shot sound from a rule | `emitCue` publishes a named cue, and an `audio.cues` row binds the name to a synth patch, placed at the body. | `WorldEffect.EmitCue`, `WorldAudioCue` |
| A synthesized instrument voice | A `puck.synthesizer-patch.v1` document played by `VoiceSynth`. | [World audio](../../src/Puck.World/Audio/README.md) |
| Music in segments and layers | `MusicDirector` over a `puck.music.v1` score, with tracker tunes as layers. | [audio reference](../reference/audio.md) |
| Limbs that swing, lean and plant | Drivers, swings, slides and effectors in the creation document, all authored on the Moth. | [the rig reference](../../.claude/skills/sdf-authoring/references/rig.md#how-the-moth-moves), [`moth.puck`](../../worlds/debug/avatars/moth.puck) |
| A pose driven by simulation state | A driver whose signal is a state cell, eased by the cell's `dynamics` row. The dash lean works this way. | `CreationDriverDocument`; the `dash` driver in `moth.puck` |
| A glow driven by state | Four body render lanes a look row fills with expressions, read by `emissiveLane`. | `WorldLookMotion`, `SdfLanes` |
| A seam that holds while parts move | Two-owner skinning inside an admitted motion envelope. | [moving blends](moving-blends.md#admitted-motion-envelope-f1) |
| A close or reframed camera | Camera programs with authored ops, selected by seat state; a chase template with clearance. | [`camera.puck`](../../worlds/standard/camera.puck) |
| A scripted player for checks | `seatPrograms` rows and `player.signal <source> press at:<seconds>`, which press at a stated offset inside a tick. | `WorldSeatProgram`, `WorldSeatDriver` |
| Motion judged on exact ticks | `puck motion` scenarios with contact sheets. | [the rig reference](../../.claude/skills/sdf-authoring/references/rig.md#judging-motion) |
| Prior art for a played instrument | An engageable screen machine that plays a tune cartridge and folds its tempo into the world's clock. It is a machine on a screen, not an avatar's instrument. | `TuneInstrumentEngine`, `WorldServer.InstrumentClockBoundary` |

Two corrections to the starting frame follow from this table. Presses are not
"judged on ticks": the judgement is already finer than a tick, at the captured
offset. And a known input-to-display latency does not enter the judgement at
all. The judgement reads captured time plus the player's calibration, so
latency is something the player calibrates away, and the engine's part is to
measure it and offer the number.

## The engine gaps

Each gap below was read in the code. Each has a lane in this plan's first
wave, and a line in the packet of the plan that owns its area, so it is placed
and not only listed here (AGENTS.md, "Puck's purpose"). A lane that meets one
stops authoring around it.

### The rig

**Poses do not blend, and layers cannot be masked (gap A).** A creation's
`frames` are whole poses that replace one another with no interpolation
([frames](../../.claude/skills/sdf-authoring/references/rig.md#frames)). The
blended alternative is a swing under a driver's weight, and a creation holds
32 drivers and a shape 8 swings (`CreationDocument.MaxDrivers`,
`ShapeDocument.MaxSwings`). The Moth uses 18 drivers, and her forearm uses 5
of its 8 swings. A fingering chart and a set of dance moves, written as one
driver a pose, do not fit. A driver's weight also applies to every shape it
moves, so "the gait keeps the legs and gives up the arms while she plays"
needs a second gait driver for the arms. An author would write a clumsier
form to fit the caps, which "Ergonomic is optimal" calls an engine defect. The
missing mechanism is a pose clip: authored key poses that interpolate, played
by a driver, weighted, and limited to the shapes the author names.

**An effector cannot aim at the creation's own body (gap B).** An effector's
target is a surface the field probe finds, another body, or a world point in a
state cell (`CreationEffectorTargetDocument`). None is "this point on my own
forearm". Forward poses alone drift: the arm that carries the instrument and
the hand that fingers it are posed by separate swings on top of gait, lean and
breath, so a fingertip misses its vent by the sum of their errors. A hand
placed on a hip in a dance pose has the same problem.

**A press reaches the rig a tick late (gap C).** A driver reads motion signals
and state cells (`CreationDriverDocument`'s signal list). Nothing in the rig
reads the seat's input. A finger therefore starts moving after the next tick
has run and its snapshot has been presented, which is up to a tick (33 ms at
the debug world's 30 Hz) and a frame after the press, and a network round trip
later with a remote authority. The combat decision records that prediction is
new work
([combat](../decisions/engine-design.md#combat-is-world-content-on-generic-queries-fields-and-input)).
The design question is whether the seat's own presentation may read its own
routed input directly, as a signal that never feeds simulation, or whether the
answer waits for prediction.

**Four body lanes, three in use (gap D).** A body's look fills four render
lanes, and the Moth's jets, flight mode and boot flame use three
(`moth.puck`'s `lanes`). Four vents that light separately, a breath level and
a beat pulse need more carriers than one free lane.

**A small joint cannot keep a blended seam (gap E).** A blended seam between
parts that move is skinned only while their relative turn stays within 60
degrees and their relative travel within half the blend's radius, and a place
where a third owner's share passes 1/64 splits
([the envelope](moving-blends.md#admitted-motion-envelope-f1)). A finger joint
bends further than that on a part a few centimetres thick, and a knuckle is
where a finger, its neighbour and the palm meet. This is an inference from the
documented envelope, not a reading of a baked hand. The suited Moth avoids it
honestly: her fingers are armour segments with visible pivots, which the
direction asks for anyway. The pilot's bare hand cannot avoid it, so the bare
hand is the probe that measures this gap, in a later stage.

### The body

Stage 4 can use `easedTimedMove` from [the standard motion library](../../worlds/standard/motion.puck)
to move the simulated body and turn its facing over the same interval. For a
leap, combine it with `setVerticalVelocity` for the vertical launch. The usual
body sweep resolves the requested travel against the floor and obstacles.

**A strike is a distance test (dependency).** `interactions` pair bodies by
range. A strike that lands where the leg is, a telegraph a target can read
and a parry are the [readable interaction](readable-interaction.md#build-order)
plan's predicted sweep and first contact, which are not built. The first dance
stage uses range and facing, and the stage that makes strikes limb-accurate
waits for that plan's steps 1 and 2.

### Timing

**Rules cannot read which beat it is (gap G).** The clock operand has two
forms, and both answer the distance to the nearest beat. `MusicClock` knows
its current beat and bar and no operand reads them. A phrase that says "vent
two on beat three" and a rule that notices a beat passed with no press both
need the beat's index.

**Calibration has no door, and nothing measures latency (gap H).**
`WorldCommand.CalibrateInput` is decoded and applied by the authority, and no
verb, binding or client code sends it. The measurement a player would
calibrate from is the rendering plan's record for every present
([decision 15](rendering-research.md#15-present-frames-the-display-has-room-for-and-know-when-they-showed)),
which is a second-wave lane there.

### Sound

**Delivered rule cues (gap I).** The [design below](#delivered-cues-and-music-design)
replaces the authority's local audio callback with the recipient's tick. One
cue can drive a sound and the look frame of the same name. An authored patch
can use the cue argument for pitch.

**Music conditions and layer gain (gap J).** A score can match a delivered
rule cue alongside region and seat events, and a layer's authored gain reaches
its tune emitter.

**Thirty-two voices (gap K).** `VoiceSynth` uses the current envelope and
authored gain to select the voice it replaces when all slots are occupied.

**Nothing drives a controller's motors (gap L).** No world code calls
`IGamepadOutput` ([input packet](open-items.md#input-channels)). A beat felt in
the hands is the clearest non-visual metronome a pad offers, and it is an
unreached capability to wire.

#### Delivered cues and music design

`RuleEvaluator.Commit` already delivers external effects after a successful
firing. `WorldRuleHost.FireGameplayCue` will use that door to collect one-tick
events. A snapshot primer starts without events. The event carries the name,
optional authored text, an optional integer expression result, and the source
tick. A body target also carries its generation. The integer has no prescribed
unit in the event. A synth patch can opt in to reading it as base pitch in
millihertz. The creation look can use the same event name as an on-demand
frame. A missing or replaced body must never redirect the event to its successor.

Only the recipient's snapshot delivery can materialize wire cues. It checks
the firing's readable state dependencies through `WorldRecipientView` and the
target against the same entity image the recipient receives. The event's
presence, tick and payload all have the same audience. Authority narration
stays on the authority. Filter before any cue can affect the projection cadence,
and do not put cues in the primer or carry withheld events into a later tick.
The rule footprint includes its gate, bindings, effects, decision options and
group trigger. Cells with variable audiences and host facts whose disclosure
cannot be proved use conservative visibility. Explicit writes across audiences
remain authored disclosures under the
[disclosure decision](disclosure.md#where-the-boundary-stands).

Scores match `cue:<name>` alongside the existing world event conditions.
Transitions still wait for the chosen beat or bar, layers hold while the
condition matches, and embellishments use the existing one-shot voice path.
The presentation director matches rule cues after recipient disclosure so
music cannot announce a withheld firing. A layer carries its authored gain
into the bed emitter. The synth takes a free slot when possible, then replaces
the voice with the lowest envelope level weighted by voice gain, oldest first
on ties. Apply the same rule in offline and live mixing.

Check an admitted and a withheld recipient against the same firing, including
a body outside the recipient's entity image. Check a sampled projection with a
cue between its ordinary snapshots and attach a new client after the cue.
Use one patch to play two argument values and use a same-named look cue to
verify pose and sound. Arm a beat-boundary transition and an embellishment by
name, read the active layer's gain in the emitter table, and exercise more than
32 overlapping voices to check which voice is replaced.

### The crowd

These are owned by plans that already exist. This feature is a consumer that
loads them, and its last stage waits on them.

- A draw whose vertices ride several transform slots has no bounding box and
  no coarser levels, so it is drawn whole in every view and every shadow pass
  (`SdfMeshBox.TryOf`, `SdfMeshCollapse.Levels`;
  [virtual geometry](virtual-geometry.md#d2-bounds-for-skinned-and-multi-slot-draws)).
- An authored swing is interpreted from the document for every body on every
  frame; the compiled motion form that a CPU evaluator, a GPU kernel and a
  bound all derive from is proposed and not built
  ([decision 12](rendering-research.md#12-keep-motion-and-placement-as-authored-programs-at-zone-scale),
  [visibility](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu)).
- The pose cells the Moth's abilities write are each one cell for body 0
  ([play-feel packet](open-items.md#dual-stick-play-and-the-moths-motion)).
  This feature's cells are keyed by body from the first stage, and the lane
  that adds them keys the existing ones the same way.

## The invariants

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| A grade is a function of the input tape. The same presses at the same captured times give the same grades on every machine. | Only the authority's rule host reads input records, through `WorldInputOwner`; captured time, calibration and the clock segment are in the checkpoint and hash; `puck test --reproduce` compares two runs. | A device that reports no event time is stamped when it is read. A remote producer's captured time is a claim the authority bounds by the authored lateness and cannot verify. |
| Sound and pose decide nothing. A grade, a hit and an effect never depend on what was heard or drawn. | The architecture gate: the client never references the server, and `Puck.Audio`'s closure is exact. Gap C's design must keep any input the rig reads on the presentation side of that line. | A player's eyes and ears. Latency changes what a player does, which is why calibration exists. |
| A move's travel is simulation. The rig follows the body, and the body never follows the rig. | A body moves only through `WorldBody.SweepContact`, and kit and rule effects are the only writers of a move. | Presentation may add sway and lean that the capsule does not have, as the gait does today. |
| The engine learns no instrument and no dance. | Nothing structural. The generic-engine rule and review hold it, and the lanes' mechanisms are named for what they do (a pose clip, a timed turn, a delivered cue). | An engine name that leaks the feature's vocabulary is a defect fixed when found. |

## Stages and acceptance

A stage is accepted by the readings in its row, taken on the debug world. The
feature as a whole is accepted only by one capture series of the full play
style against the owner's words and the concept pack
([orchestration](../../.claude/skills/orchestration/SKILL.md#plan-from-the-milestone)),
which stage 6 takes. Counts and state readings come from a headless World.
Captures and presented bursts come from the lead's windowed batches at 2560
by 1440 on Direct3D 12, with a Vulkan boot of the same stops.

| Stage | What exists when it is done | View or reading | The check |
|---|---|---|---|
| 0. Grounding | The research round's synthesis, the concept pack, and the two reviewed designs (pose clips with gaps A to C; the delivered cue with gaps I and J). | The documents | Each design names its invariant and enforcement and has one cross-family review answered. The pack passes the [concept art review](../game/art/README.md#concept-art-review). |
| 1. The beat and the judge | The debug world has a score. The standard package has the breath and vent channels, the instrument input mode, the phrase rows and the grading rules. No body moves yet. | State | A scripted phrase with presses at stated offsets (`player.signal … press at:`) reads the authored grade for each step, the phrase's strength and its completion from state rows. Presses 1 ms inside and 1 ms outside a window grade differently. The run reproduces at 30 Hz and at 60 Hz. A second seat's phrase is graded independently in the same ticks. |
| | | Counter | `world.counters state` shows the rules' work for an idle seat unchanged from the build before, and the phrase's cost per pressing seat is reported. |
| 2. The hands play | The instrument is on the Moth. Breath raises it. Each vent button moves its finger onto its vent, standing, walking and running. | Close: hand cameras the lane adds to the Moth's inspection layouts (she has none today), from front, both sides, back and above | `puck motion` sheets for play-idle, play-walk, play-run and a four-note phrase: every fingertip is on its vent in the frame after its press, and off it after release. `body.rig` reads each finger shape's delta. `puck mesh audit` on the hand groups reports no split tuples and no torn seam. |
| | | Chase: the default boom | The stance reads as playing in silhouette from behind and from three-quarter view, and the gait's leg cycle is unchanged against the build before. |
| | | In motion | An eight-frame presented burst of a fast phrase shows the fingers with no trail behind them and no flicker on the still hand. `world.debug-view shadow` shows each finger's shadow on the gauntlet at the close camera. |
| | | Counter | Draws, vertices and transform rows for one Moth before and after, from `world.counters gpu` and `sdf.instances`. |
| 3. It sounds, and the music answers | A press sounds its pitch at the avatar. The score adds a layer as a phrase gains strength and marks its completion. The pad pulses on the beat. | State and read-back | The delivered note's argument matches the pitch in the audio capture. `speaker.state` shows the note's patch and the layer emitter. The tick of each note's cue equals the tick its finger cell changed. |
| | | Clip | A `capture.start` clip of an eight-beat phrase, inspected: each note is heard as its finger lands, and no note is doubled or dropped. |
| | | Topology | The same script against an authority in another process gives the same `speaker.state` cues on the client. |
| 4. The body dances | The dance input mode, three moves with real travel (a step, a spin, a leap), and strikes on their active ticks. | State | A scripted move on flat ground ends at its authored displacement and facing. The same move into a wall, up a kerb and down a slope ends where contact allows, with no `sweep=refused` in `body.where`. A strike on the beat and one off it write different damage to a target's state. All of it reproduces. |
| | | Chase and side | `puck motion` sheets for each move: the feet are planted on every stance frame (`body.rig` reads `planted`), the body's mass stays over a planted foot, and no limb passes through the body. |
| | | Camera | A presented burst of the leap from the seat's own camera keeps the whole body in frame from take-off to landing, and the camera does not enter geometry on the tight-space course. |
| | | Counter | `physics.sweep` and `physics.contact` per move tick, against a dash's. |
| 5. One rhythm | Phrases whose steps mix notes and moves, a move that fills a phrase step, a finishing move, and playing while the legs dance. The phrase effects act on allies and enemies. | State | "Note, note, spin, note" completes as one phrase. A finishing move applies the phrase's effect to targets in range and to no one else. Haste and a shield reach an allied seat and expire on their due ticks. |
| | | Chase | Sheets of a note played during a spin's leg cycle: the fingers are on their vents while the hips turn. |
| 6. The places | The gallery exhibit and the valley stage, a route chapter, and the full play style. | The gallery exhibit | A first-time route segment: a seat program that only answers lit vents completes the exhibit's call and response, and the exhibit's plaque reads as true in its capture. No text on screen explains the controls. |
| | | The valley stage | The end-to-end series: approach, raise, phrase, move, mixed phrase, finisher, effect on an ally and on a target, from the seat's camera and two fixed stage cameras, stills and presented bursts, on both backends. It is judged against the owner's six points above and the concept pack. |
| | | The benchmark | The stage is a chapter of the benchmark route, played by a seat program, with its frame-time report. |
| 7. A crowd | Many bodies playing and dancing at once, driven by seat programs, on the stage. | Frame bar | The route's stage chapter at 8, 32 and 128 performers, in motion, at 2560 by 1440 on the RTX 4070: frame-time statistics, and no performer popping, freezing or losing its instrument. |
| | | Counter | Per performer: draws, vertices, transform rows, field queries (`world.counters world.field-queries`), rule work and live voices, each against its count at 8. A count that grows faster than the crowd names its owner. |
| | | Topology | The 32-performer chapter with the authority remote, once the benchmark can drive a remote route. |

The fingers, not the big moves, are the hard test of the picture history. The
rendering plan's split history restarts a pixel's stored window when the pixel
moves
([decision 13](rendering-research.md#13-shade-through-one-path-and-shrink-the-image-history)),
and a finger is a few pixels wide and moves every beat. Stage 2's burst is the
reading that says whether a restarted window reads as clean or as shimmer.

## What this needs from other plans

| Dependency | What this feature needs from it | Which stage waits |
|---|---|---|
| The rendering plan's smaller image history (its `history` lane) | A moved pixel that reads clean | Stage 2's burst is judged after it lands; stage 2's authoring does not wait |
| The rendering plan's shadow maps by role | A near shadow fine enough for a finger on a gauntlet | Stage 2's shadow check |
| The rendering plan's colour conversion | Vent glow and bloom in display values that match the concept pack | Stage 2's close captures |
| [A record for every present](rendering-research.md#the-second-wave) | The time from a press to the frame that shows it, which the calibration exercise offers the player | Stage 3's calibration door can land without it; the offered number waits |
| [Depth of field](open-items.md#depth-of-field) | An optional close-camera look | None |
| [Readable interaction](readable-interaction.md#build-order), steps 1 and 2 | Strikes at the limb and telegraphs | The limb-accurate strike after stage 5 |
| Prediction, from readable interaction's step 0 round | A remote player's own presses felt at once | Stage 7's remote reading is judged with and without it |
| [Virtual geometry](virtual-geometry.md#d2-bounds-for-skinned-and-multi-slot-draws) and [visibility](visibility.md#v2-instance-updates-proportional-to-change-and-authored-motion-on-the-gpu) | A box and levels for a many-part body; motion evaluated once for many bodies | Stage 7 |
| [The benchmark](benchmark.md) | A route chapter, and a remote route driver | Stages 6 and 7 |
| [The composition plan](debug-world-composition.md#the-map) | A row for the stage. It shares the combat yard's reservation north of the hall, because the yard's targets are what the moves strike. The lane that builds the stage adds the row. | Stage 6 |

## The lanes

One owner holds the feature: a feature lead lane (a `principal`, since the
judgement that matters is whether the stages add up to what the owner asked
for). It briefs the lanes below, holds each to its stage's checks and takes the
end-to-end series. The engine-gap lanes run first and in parallel. World
authoring lanes are first-class: they start on the mechanisms that exist and
stop at a gap.

| Lane | Delivers | Model class | Why that class | Starts |
|---|---|---|---|---|
| Research round | The six topics in [research owed](#research-owed), one researcher a topic across the families, one synthesis, one adversarial review | Researchers: Sol, Neon, an Opus `specialist`. Synthesis: `principal`. Review: Astra. | A round wants divergent sources and a reviewer from another family than the author | Now |
| Concept art | The pack in [the brief](#the-concept-art-brief) | Codex art lane (Sol with `image_generation`), a Claude partner checking constraints | Codex is the primary artist and judge by ruling | Now |
| Pose clips (gaps A, B, C) | A design memo, reviewed, then the mechanism: interpolated key poses played by a driver with a weight and a shape mask; an effector target on the creation's own shape; the decision on a presentation-side input signal | Design: `principal`. Review: Astra. Build: an Opus `specialist`. | The rig's layering is invariant-heavy (the skin envelope and the bounds read swings), and a wrong design costs every later stage | Now |
| Beat index and calibration (gaps G, H) | Clock operands for the beat and bar; a calibration verb and binding that send `WorldCommand.CalibrateInput` | `implementer` | Both extend an existing operand and wire an existing command | Now |
| Delivered cues and music that follows rules (gaps I, J, K) | A design memo, reviewed, then: rule cues in the delivered tick, with a numeric argument a patch reads as pitch; one lane feeding sound and pose cues; score conditions on rule cues; layer gain in the mix; a voice-stealing rule | Design and build: Sol at `high`. Review: an Opus `specialist`. | It changes what crosses the wire, which is a disclosure question; another family reviews | Now |
| Body lanes (gap D) | More per-body carriers for shading-time values, or a second carrier, as the design finds | An Opus `specialist` | A C# and HLSL layout pair with a generated side | After the rendering wave's lanes that edit the same pass block land |
| Haptics (gap L) | A world-authored pulse on a seat's pad, driven by state | Hydrogen | A specified wiring of an existing output interface | Now |
| Standard package: the judge | Stage 1's channels, mode, bindings, phrase rows and rules in `worlds/standard/`, with the debug world's score | `implementer` | The mechanisms exist and the fixture shows the shape | Now; it meets gap G |
| The instrument and the hands | Stage 2: the creation, the segmented fingers, the playing stance and the fingering poses on the Moth | An Opus `specialist`, as a look lane with the probe rule | A creation built to push the rig; it stops at gaps A, B and E | After the pack's instrument panels; it meets the pose-clip lane |
| Sound and score | Stage 3's patches, cues, tunes and score layers | `implementer` | Authored data once the cue lane lands | After the delivered-cue lane |
| The dance | Stage 4's moves: kit actions, footwork, strikes and their clips | An Opus `specialist`, as a look lane | Whole-body motion judged on sheets from every side | After the pose-clip lane |
| Interplay and effects | Stage 5's mixed phrases, finishers and effects | `implementer` | Rules over state that stages 1 and 4 define | After stages 1 and 4 |
| The places | Stage 6's gallery exhibit, the valley stage, the route chapter and the guides | `implementer` for the documents, with the feature lead's captures | World authoring on existing families | After stage 5 |
| The crowd | Stage 7's workloads and its counted report | Sol | A measurement lane whose findings go to the plans that own them | After stage 6 |
| The bare hand | The pilot playing out of the suit, as the probe for gap E | An Opus `specialist` | A probe that leads its hand-back with what the engine could not do | After stage 2 |

Each look lane works under the probe rule in its own words: when a defect is
an engine limit, it stops sculpting that part and hands the limit back with
its evidence, and it takes at most two look-critic rounds a wave.

## Research owed

One round before the two designs are written, each topic with its own
researcher and verified citations, synthesized with Puck-native combinations
weighed beside what other engines do.

1. **Timing windows and latency compensation in rhythm-action games.** Window
   sizes, how grades are tiered, how calibration is offered, how audio and
   video offsets are separated, and what action games with a beat do when the
   player is also fighting. Puck's own ground: the grade is computed by the
   authority at the captured time with a hashed calibration, so a performance
   replays exactly and two machines never disagree about a note.
2. **Rhythm over a network.** How shipped games keep many players in time.
   Puck's own ground: the world's clock is simulation state every client
   already has, so notes can be sounded on the beat they were graded for
   instead of when their packets arrive, and a portal is a tempo boundary.
3. **Hand and finger animation in stylized action games.** Pose libraries,
   curl parameters, reach to a held or worn object, and how small motion is
   made readable from a chase camera. Puck's own ground: fingers are field
   shapes with authored pivots, vents are lit by lanes the simulation drives,
   and the surface an effector reaches for is the drawn field.
4. **Procedural against authored layering.** Additive layers and masks,
   reach and foot locking, balance over a support foot, and animation-driven
   travel against simulation-driven travel. Puck's own ground: a move's travel
   is a deterministic program whose swept volume can be computed ahead, so the
   telegraph, the strike and the bound come from one derivation.
5. **Dance and rhythm combat.** How beat-locked attacks stay responsive, how
   large moves stay readable and fair, and how a camera frames them.
6. **Crowds of animated bodies, and many voices.** Level of detail for rigs,
   update rates, motion evaluated on the GPU, and voice allocation. Puck's own
   ground: one compiled motion form for the CPU, the GPU and the bound, and
   update rates that already fall with distance.

## The concept-art brief

For a Codex art lane, as a pack under `docs/game/art/` beside the Moth's, with
its prompts and provenance recorded as the other packs do. A Claude partner
writes the prompts' constraints and checks them. Every panel passes the
[concept art review](../game/art/README.md#concept-art-review), with joint
range as the audit that matters most here.

**Fixed facts.** The Moth as her
[pack](../game/art/moth-concept-pack/README.md#preserve-the-character) fixes
her: the suit's proportions, plates, pivots and palette. Her gauntlets are
heavy blocks with a pair of recessed vent slots on the outer face. The world
is the [valley and gallery](../game/art/valley-and-gallery.md) direction:
painterly, cartoony, slightly futuristic.

**Original work.** The instrument and every move are new designs. No panel
echoes a real instrument's silhouette, a named game's instrument, or a named
game's dance or attack. The references the owner named describe a register
(powered suits, bright readable energy, movement abilities), not a source.

**What the designs must do.**

- The instrument is worn on the forearm and grows from the gauntlet's vents.
  It takes air, and the panel shows where the air comes from and where the
  sound leaves. It has four vents a hand can stop, spaced for a suited hand.
- Both hands stay free. Nothing is held, gripped or strapped across a palm.
- Each vent has a lit and an unlit state, and the breath has a visible level.
  The lit vent must read from a chase camera about four metres behind her.
- Fingers are armour segments on visible pivots. Each segment's range is
  drawn.
- A dance move is large, committed and funny, and it is a strike. Its key
  poses fall on beats.

**Panels.**

1. The instrument: turnaround, a section showing the air path, and the lit and
   unlit states.
2. The playing stance, suited: front, side, back and the chase camera's view,
   with weight over the feet.
3. The hands: the four vents stopped singly and in pairs, and the open hand,
   close, with each segment's joint range marked.
4. Playing in motion: a walk and a run with the instrument raised.
5. The dance stance and a footwork diagram from above.
6. Three signature moves as strips of four key poses on four beats, each with
   its path on the ground from above: a travelling step, a spin that strikes,
   and a leap that lands as a finisher.
7. A transition strip: playing, into a move, back to playing, showing what the
   arms do.
8. The silhouette at distance: the stance and one move as a player sees a
   crowd of them from across the stage.
9. The off-suit pilot's hand on the instrument, at her scale, for the later
   bare-hand stage.

## What the lanes ask of the lead

- **Windowed checks.** Stage 2's, 4's and 6's captures and bursts and stage
  7's frame-bar readings are the lead's batched windows. Each lane's hand-back
  states the stop, the commands and what it expects to see.
- **Two feel sittings with the owner**, as the play-feel packet already
  schedules them: one after stage 2 on how a press feels in the hands, one
  after stage 5 on the whole play style. The windows and the miss rule are
  authored values a sitting may correct.

## Systems considered

The capability searched for, what was read, and why the owner named in the
tables above owns it.

- **A beat clock and a judged press.** Read the `input` and `media` skills,
  the [input decision](../decisions/input-channels.md), the
  [audio reference](../reference/audio.md), `MusicClock`,
  `WorldInputHistory` and the rule host's clock read. A text search for
  `phaseError` across the sources, worlds and documents found the two operand
  forms, the fixture and no dedicated grader. A search for `Calibrat` found
  the command, its codec and its apply, and no sender.
- **Poses and layering.** Read the `sdf-authoring` skill and its
  [rig reference](../../.claude/skills/sdf-authoring/references/rig.md), the
  Moth's limbs, drivers and look in `moth.puck`, `WorldLookMotion` and
  `CreationEffectorTargetDocument`. The candidates for a blended pose were
  `frames` (no interpolation), look `poses` and `cues` (a frame held, no
  blend), and a `constant` swing under a weighted driver (blends, and is the
  only one that layers). The caps were read at their declarations.
- **Moving a body.** Read the `physics` skill, the effect vocabulary in
  `WorldEffect`, `BodyMotionOp`'s operations and the standard's `walk` program
  and dash template. The candidates for a turn were `SnapYawToPlanarIntent`
  (instant, toward the stick) and the `pose` effect (a teleport that drops
  velocity).
- **Hits.** Read the combat decision, `WorldInteraction` and
  [readable interaction](readable-interaction.md).
- **Sound from play.** Read `WorldAudioCue`, the cue effect and its tap in
  the composition root, `MusicSenseFamily` and the World audio README. A
  search of the protocol project for a delivered cue found none.
- **Crowds.** Read [virtual geometry](virtual-geometry.md),
  [visibility](visibility.md) and the rendering plan's decisions 12, 13 and
  15.
- **Prior art.** `TuneInstrumentEngine` and the play plan's audio wave; a
  search of the documents for a bard, a played instrument and a rhythm game
  found the input decision's rhythm genre check and nothing built as play.
