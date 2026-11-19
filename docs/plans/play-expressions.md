# Play expressions

A play expression is a way of playing that is defined by what the avatar's
hands and body do, and only afterwards by what it does to a number. The owner
has named three. Each is chosen for the engine systems it loads.

This page is the family's home. The first expression, the bard, is planned in
full in [instrument and dance play](instrument-play.md). The other two are
open explorations: this page records the owner's words and the questions, and
[the synthesis](#the-synthesis) of the ideation round that answered them. The
synthesis compares proposals and recommends a direction. It decides nothing
that is the owner's, and it ends with
[the questions the owner chooses](#the-questions-the-owner-chooses).

## The owner's words

On the bard, the two requests quoted in full in
[its plan](instrument-play.md#what-the-owner-asked-for).

On the other two: "puzzle-healer, and spreadsheet-mage. These are not literal,
just the most abstract + direct way I could think to express the concept.
Solving puzzles or maths or riddles, who knows to do damage, tank, heal,
whatever. We don't have to decide the exact mechanics or which does damage or
heals, etc. We just have to come up with the avatar interactions for now and
work out the gameplay once the engine is capable of expressing such things."

And, on what the names mean: "we have to figure out what they even mean". The
owner's examples of where the answer might lie:

- "a much more refined and improved actiony version of the alchemy game";
- "what might work in a group setting";
- "maybe each class has their own instrument/toy/tool/totem";
- "maybe players mix and match, their class is a result of the current
  configuration".

The intent, in this page's words. The two names are handles for two ideas,
play by solving and play by calculating, and neither is a literal healer or a
literal spreadsheet. What each one is has not been decided. Which one harms,
protects or heals is not asked. The first thing wanted is the picture of the
avatar doing it, and the gameplay follows once the engine can express it.

## Where the family stands

| Expression | State | Where |
|---|---|---|
| The bard: a worn wind instrument and dance fighting, on the world's beat | Planned in full: the systems it rests on, its engine gaps, stages, lanes, research and concept-art brief | [Instrument and dance play](instrument-play.md) |
| Play by solving (the owner's handle: puzzle-healer) | An open exploration. The ideation round proposes two readings, fitting a shape and mixing substances, and the owner chooses | [The synthesis](#the-synthesis) |
| Play by calculating (the owner's handle: spreadsheet-mage) | An open exploration. The round proposes three readings and the owner chooses | [The synthesis](#the-synthesis) |

The bard goes first because most of what it needs is built. Its first wave
writes designs for the rig and for sound that the other two will lean on,
whatever they become: a hand that reaches a chosen thing, a press that moves
the hand before the next tick, more shading values a body can drive, and a
rule's cue that reaches every client
([the engine gaps](instrument-play.md#the-engine-gaps)). Those designs are
written for hands in general and carry no instrument's vocabulary.

## The two open explorations

### The questions

These are the owner's questions and the ones they imply. The ideation round
answers them as proposals; the owner chooses.

1. What does play by solving look like on an avatar, in the world, during
   action? What does play by calculating look like?
2. Is the alchemy table the seed? The debug world has one
   ([alchemy](../../worlds/debug/alchemy.md)): a seated board game where two
   elements combine in a cauldron to discover others. What would a faster,
   bodily version of that be?
3. What works in a group? What does a second player add that one player
   cannot do alone?
4. Does each expression have its own object: an instrument, a toy, a tool or
   a totem? The bard's is a worn instrument.
5. Is a class a fixed thing, or the result of what a player has equipped and
   configured at the moment, so that players mix and match?
6. What does each stress in the engine that the bard does not?

### The ideation round

Three memos answer the questions, one from each model family, written without
sight of each other:

- [Astra's memo](play-expressions/ideation-astra.md), six concepts;
- [Neon's memo](play-expressions/ideation-neon.md), nine concepts;
- [Fable's memo](play-expressions/ideation-fable.md), nine concepts and three
  undeveloped sparks.

The memos are the sources and stay as written. Each concept's picture of the
avatar, its account of a group of five and a raid of forty, and its
combinations are there and are not repeated here.

The writers were given a starting frame from the lead: a class is two hands
and a totem, with action alchemy, a terrain shaper and a handheld machine as
example concepts. That frame is not in the tree. This page knows it from the
memos that argue with it.

## The synthesis

This section says where the three memos agree, where they part, which system
could hold all of the concepts, what to build first and why, which engine gaps
the concepts share, and what the owner chooses.

### Summary

The six questions above, each with the round's answer in a sentence:

| Question | The round's answer |
|---|---|
| 1. What do solving and calculating look like on an avatar? | Solving: all three memos say healing is fitting a shape into something that is broken as geometry. Calculating: three different pictures (predicting, asking what would happen, and tying two quantities by a ratio), with no agreement. |
| 2. Is the alchemy table the seed? | It can be. Its rules carry over unchanged and its board is replaced by the body and the ground ([action alchemy](#action-alchemy-built-on-the-table)). The memos did not develop it, because it was in the lead's frame and they were asked to step outside. |
| 3. What works in a group? | A visible invitation that another player answers, and a thing that passes from one body to another. At forty, the picture moves off each body and onto something shared. |
| 4. Does each expression have its own object? | Yes, in 23 of the 24 concepts; the exception is Fable's Footwork, where the feet are the instrument. The object is worn or stowed on the body, so that letting go of it is part of play. |
| 5. Is a class fixed, or the current configuration? | The configuration, in all three memos. None kept "two hands and a totem". Each replaced it with slots that are parts of the body. |
| 6. What does each stress that the bard does not? | Geometry that changes on a body during a fight, things passed between bodies, hidden information among forty moving players, and ground that flows ([the engine gaps](#the-engine-gaps-the-memos-share)). |

What this page recommends:

- **A class is what occupies the parts of the body.** The slots are the hands,
  the breath, the feet and the back. Tools combine through calls that any
  other tool may answer, and never through a rule written for a pair of tools.
  A class name is derived from the configuration and is stored nowhere
  ([the system](#the-system-that-holds-them)).
- **Build the bard first, as planned, then geometry and ground side by side.**
  The mender, who fits a piece into a hole, is the best forcing function in
  the round after the bard. Action alchemy on the ground is the cheapest
  second expression and is the owner's own example
  ([the order](#what-to-build-first-and-why)).
- **Two of the bard's first-wave designs should be written for the whole
  family now.** The pose clip's shape mask is what makes a slot, and the
  effector target is what lets a hand reach a body a rule chose
  ([what the bard's first wave should take from this](#what-the-bards-first-wave-should-take-from-this)).

Four questions are the owner's, and nothing beyond the bard is built until
they are answered. They are [at the end](#the-questions-the-owner-chooses).

### Where the memos converge

Three writers who did not see each other's work reached the same answer in
nine places. That agreement is the round's strongest evidence.

1. **The bard goes first.** Each memo's build order opens with it.
2. **The instrument leaves the body free.** Astra's reed hooks over a
   shoulder, Neon's horn locks across the collarbone, and the bard's plan
   wears it on the forearm. Fable's horn is held in the left hand, which the
   bard's concept-art brief rules out ("Nothing is held, gripped or strapped
   across a palm"). The plan stands. Fable's idea that survives the correction
   is that a note also moves the player.
3. **Healing is fitting a shape.** Astra's Splintmaker folds ribs around a
   damaged joint. Neon's Mosaic-Mender punches a wedge into a fracture.
   Fable's Mender shapes a lump and presses it into a hole in an ally's
   armour. In all three the damage is geometry, the cure is a piece, and the
   hands turn the piece until it fits.
4. **A class is a configuration.** Astra: "I’d let configuration name the
   player’s practice". Neon: "Class is a named rig preset, never an engine
   type." Fable: the class name "is displayed and never stored". All three
   also apply the owner's existing ruling that a character claims its kit and
   each world limits it ([the game](../decisions/play.md#the-game)).
5. **A tool invites, and another player answers.** Astra's bard "plants a heel
   facing the opening" and a partner crosses on the rest. Neon's lantern
   "hums and tilts toward the missing tile so teammates can read the
   invitation without voice chat". Fable names the pattern: a tool opens a
   call in the world and any tool may answer it.
6. **Things pass from body to body.** A shaped piece is thrown to the ally who
   presses it in, a half-solved plate is tossed to a second player, a card is
   dealt, a learned step is taught down a row. Fable calls the hand-off the
   unit of interdependence, and the other two use it without naming it.
7. **At forty, the picture moves to something shared.** Neon states the rule:
   individual tools bind to "shared encounter anchors". Its examples are one
   keystone a squad's menders work around and one tall gauge the raid reads.
   Astra's crews mend the hinges of one gate. Fable's bards lead sections and
   the score's layers become the raid's status display. All three read a crowd
   by posture and by a shared object, never by effects on each body.
8. **A recording of one body is played by another.** Astra's Understudy learns
   a partner's movement and performs it. Neon's Pocket-Pendulum loops a ghost
   of a teammate. Fable's Echo records the player's commands and drives them
   again through the same doors. Three memos found the same use for
   determinism.
9. **A world small enough to hold.** Astra's Bell Diver steps into a machine a
   friend cradles. Fable's Tabletop Hand reaches into the fight from a table
   in another world, and its Clockmaker carries a world that runs at its own
   rate. Neon's Cartridge-Deck wears a hosted machine on the forearm. Each
   turns the screen-within-a-screen and portal work into something a class
   does.

### Where they diverge

1. **What calculating is.** Astra's Reckoner samples a moving thing, sees the
   pattern and stands where it will be. The player does the arithmetic and may
   be wrong. Fable's Actuary asks the engine to play a branch out for real and
   chooses who sees the result. Neon's Fulcrum-Smith clamps a beam between two
   quantities and slides a collar to set the ratio between them. These are
   three different feelings (being perceptive, being the analyst, being the
   engineer), and they need different engine work. This is
   [question 2](#the-questions-the-owner-chooses).
2. **Whether the beat is everyone's.** Fable makes the music clock "the one
   call everyone can answer", so the bard is the group's hub. Neon locks a
   raid's bards onto one encounter clock and gives every module a beat port.
   Astra goes the other way: bards "trade phrases without putting the entire
   raid on one clock", and "A silent run should still be playable by watching
   the body". This is [question 3](#the-questions-the-owner-chooses).
3. **Who owns the tempo.** The bard's plan says the world owns one clock.
   Fable's bard speeds the song up for everyone, and Neon's Fulcrum-Smith ties
   the tempo to a tank's pressure. A tempo that players steer is a capability
   the plan does not have. Neither family of rule effects (`WorldEffect`,
   `ActionEffect`) has one that writes a tempo, and nothing more was read.
4. **What the memos assume the engine can do.** Astra cites documents and asks
   only for what a player can feel. Fable lists what it could not find. Neon
   cites engine types as capabilities ready to use, and for the central one,
   carving geometry during play, the code agrees with Fable that it is
   missing ([gap 5](#the-engine-gaps-the-memos-share)).
5. **What comes third.** After the bard and a geometry class, Astra builds the
   Reckoner, Neon the Floor-Caller and Fable the Dealer. Each choice follows
   from that memo's reading of calculating or of group play, so it follows
   from the owner's answers too.

### The system that holds them

The round produced six ideas for a unifying system. They do not all compete,
because they answer three separate questions: what a loadout is made of, how
tools combine, and how players come to need each other.

| Idea | From | What it answers | For it | Against it |
|---|---|---|---|---|
| Two hands and a totem | The lead's frame | What a loadout is made of | It is small, and it matches the alchemy table's two hands | Dancing is not held in a hand, a two-handed tool uses the whole loadout, and the breath has no place. Neon and Fable both reject it by name |
| Body-part slots: hands, breath, feet, back and a companion | Fable | What a loadout is made of | Each slot is one layer of the rig and one page of the controller, so it can be built and judged alone. A conflict is physical and visible: a tool that needs both hands excludes a second held tool without a rule | Five slots are more to author than three, and the companion slot needs a companion body |
| A three-layer rig: manipulator, chassis and core, joined by five kinds of port | Neon | What a loadout is made of, and how tools combine | The same division as the slots at a coarser grain. It sees that the legs hold a way of moving (a dance, a braced stance, a glide), which is more than a dance | The layers are named as equipment, so every way of moving needs a piece of kit. One of the five ports, the state signal, can mean anything |
| Calls and answers | Fable; Astra describes the same thing in the body | How tools combine | A tool never knows another tool, so adding one adds a call or some answers and never a rule for each pair. A call is visible in the world, so answering it is group play | Nothing in the engine keeps a tool from naming another tool. The discipline is the authors' |
| The hand-off | Fable; used in all three memos | How players need each other | One mechanism (throw, catch, hold) serves a patch, a card, a reel and a globe, and a thing in flight reads in a crowd | The engine's carry cannot yet be started by a rule or held in a hand ([gap 9](#the-engine-gaps-the-memos-share)) |
| The beat as a shared clock | Fable and Neon; Astra dissents | How players need each other | The clock is already simulation state every rule can read, so it costs the engine nothing more | It puts a pulse under every class, which is a matter of feel and is the owner's |

**The recommendation is the body-part slots, occupied from moment to moment,
with calls and answers between tools.** The reasons:

- **A slot is two things the engine already separates.** A slot is a set of
  shapes on the rig and a family of input bindings. The bard's first wave
  designs a pose clip "limited to the shapes the author names", and input
  modes already switch a family of bindings on authoritative state
  ([the bard's plan](instrument-play.md#what-exists-by-capability)). A slot is
  that mask and that mode under one name. Nothing new is asked of the engine
  for the slot itself.
- **Neon's three layers are the same division.** The manipulator is the hands,
  the chassis is the feet and the core is the back. Fable's version adds the
  breath, which the bard needs as its own analog control. What is worth
  taking from Neon is that the feet slot holds any way of moving.
- **Astra supplies what makes the configuration live.** In Astra's memo a
  shoulder hook matters "because the bard can let go of the reed", and when a
  Splintmaker closes both palms "somebody else has to turn the gate". The
  slots are occupied by what the body is doing now, and a stowed tool is not
  in the hands. That is the owner's "their class is a result of
  the current configuration" taken at its word: the configuration changes
  inside a fight, and other players can see it change.
- **Calls and answers are what the owner asked for in a group.** A call is
  something a second player can see and act on, so the same mechanism that
  keeps the tools from multiplying is also the group play. Neon's ports are
  kept for what they are good at, a continuous quantity (a pressure, a beat)
  that a call can carry.
- **The hand-off and the beat are not alternatives to any of this.** They are
  two shared mechanisms every tool may use. The hand-off should be built once
  for all of them. The beat is already available to every rule, and this page
  recommends that tools may answer it and that none but the bard's requires
  it, until the owner answers question 3.

The companion slot waits until a companion body exists to hold a tool.

In engine terms, all of this is authored data in the standard package, and
the engine learns no class and no tool. A tool is a template that contributes
a shape, its poses, kit actions, an input mode and rules. A call is a record
in a pool that a rule opens and another rule closes (`StatePool`;
[records and pools](records-and-pools.md)). Fable's memo cites the existing
pieces: a body register that already selects the Moth's look and kit, binding
overlays swapped by a row, and loadout presets in the world model. This page
did not re-read those three.

A design built on this recommendation has to hold three things, and today
nothing but review holds any of them:

| Invariant | What enforces it | Where the guarantee stops |
|---|---|---|
| A tool names no other tool. Tools meet only through calls | Nothing yet. A tool written as its own package that imports only the shared call rows would make it checkable, and the design should say so | An author may always write a pair rule in their own world |
| A class name is derived from the slots and never stored | Nothing but the absence of a row for it | A world may display any name it likes |
| The engine carries no tool's or class's vocabulary | The generic-engine rule and review, as for the bard | A leak is a defect fixed when found |

### The concepts, by family

The 24 concepts and three sparks fall into nine families. The table names
every one, so the memos serve as the catalogue. In each family the strongest
one or two are named with the reason.

| Family | Astra | Neon | Fable | The strongest, and why |
|---|---|---|---|---|
| Music and motion (the bard) | Reed Dancer | Reed-Dancer | Jet Horn, Footwork; the Namer (a spark) | The bard as planned, with two additions. From Fable: a note vents a little thrust, so a phrase is also a path across the floor, and "the avatar will move as one might expect" becomes literal. From Astra: a phrase ends in a rest that a partner answers, which gives the bard group play without a number |
| Mending by fit | Splintmaker | Mosaic-Mender | Mender | Fable's Mender, because the wound is a real hole and the fit is an exact comparison of two fields, with Astra's test added: a piece can fit at rest and bind when the patient moves, so the patient tries the movement. Neon's keystone is its form at forty |
| Ground and flow | | Lathe-Carver, Kiln-Siphon | Groundskeeper | The Groundskeeper, because the flowing ground it needs exists, and the Kiln-Siphon's body picture (inhale, clap two tanks together, vent), which is action alchemy performed by a suit |
| Reckoning ahead | Reckoner | Fulcrum-Smith | Actuary | The Reckoner for the clearest picture at the lowest engine cost, and the Actuary because it turns the engine's determinism into something a player asks for. They are two of the three readings in question 2 |
| Table play in the fight | | Floor-Caller | Dealer | The Dealer, because it serves the casino and Fable's memo cites the cards, the seeded shuffle and the hand-matching rules as existing. The Floor-Caller for making footwork the puzzle |
| A recorded self | Understudy | Pocket-Pendulum | Echo | The Echo for the mechanism (recorded commands through the live doors, so an echo's kick can miss) and the Understudy for the picture (you teach it to a person, who turns and teaches the next row) |
| A world in the hand | Bell Diver | Cartridge-Deck | Tabletop Hand, Clockmaker; the link cable (a spark) | The Bell Diver and the Tabletop Hand. Both put one player at a different scale from the rest so that each needs the other's view |
| Who sees what | | Stagehand | | The Stagehand alone, a class that does one thing: it chooses what other players can see. The Dealer forces the same engine work sooner |
| Flocks and companions | Flock Tender | | Murmuration (a spark) | The Flock Tender, kept for when crowds of small deciding bodies are affordable |

### Action alchemy, built on the table

The owner's first example was "a much more refined and improved actiony
version of the alchemy game". The debug world's
[Alchemy table](../../worlds/debug/alchemy.md) is built in two layers, and
that is what makes the step short.

The lower layer is the game's rules, and it knows nothing about a table. A
`recipe` lattice holds the result of every pair of elements, a `known` bitset
holds what has been discovered, and the `mix` rule fires when the two cells
`left` and `right` both hold an element. A pair with no recipe fizzles. A pair
that uses an undiscovered element is refused. The upper layer is the board:
31 tokens on a grid, a cursor, two hand wells, a cauldron and a lectern screen
([`alchemy.puck`](../../worlds/debug/alchemy.puck)).

Action alchemy keeps the lower layer and replaces the upper one with the body
and the ground:

- **The two hands are the avatar's two hands.** Each takes an element from the
  world: scooped from the ground under the feet, drawn off a burning floor, or
  caught from an ally. This is the hands slot, and it is the one place the
  lead's "two hands" frame is exactly right.
- **The cauldron is wherever the two meet.** Clapped together at the chest,
  they mix in the suit, which is the Kiln-Siphon's picture. Poured into the
  same patch of ground, they mix there and the result runs downhill, which is
  the Groundskeeper's. The ground can already do this: a lattice's reactions
  include one that rewrites a cell when a condition on its fields holds, and
  one that moves a field downhill
  ([the lattice reference](../../.claude/skills/puck-world/references/documents-state.md#statelattices--the-lattice-row-trait--the-lattice-scalar-rows-reactions-lattice-derived-geometry)).
- **Discovery stays, and it is the solving.** The book of known elements is
  what a player or a party has found. Trying a pair in the middle of a fight
  and watching it fizzle or take is the puzzle, now under time pressure.
- **A second player holds the other hand.** One player carries fire and the
  other throws water, and the mix happens at the catch. A recipe that needs a
  third element is a call the party answers. This is the hand-off and the call
  from [the system](#the-system-that-holds-them), with no new idea.
- **It feeds the mender.** Fable's memo makes the mender's material a
  compound, so the two expressions meet at a seam.

What it asks of the engine is small beside the other concepts, and three
things were read for it:

- Today `left`, `right` and `known` are single cells, so the world has one
  pair of hands and one book. A pair for each player is a pool of records.
- Pouring exists. A lattice's `emit` reaction deposits into the cell a tagged
  body stands in, and its `expose` reaction tells a rule whether a body's
  cell passes a test on a field. Taking a substance out of the cell under a
  body was not found: the effect that paints a cell
  (`WorldEffect.PaintField`) names its cell as three fixed numbers in the
  document.
- Changes to a lattice's layout or cadence still need a restart
  ([reference game design](../game/design.md)).

What it stresses is the lattice's cost when a fight's worth of bodies stand
in it. Whether one authored recipe list can generate both the rule lattice
and the ground's reactions was not checked. It should be, so that a recipe is
written once.

### What to build first, and why

The order below is by what each concept forces the engine to become, weighed
against what it waits on. Only the bard is settled. Every later row assumes an
answer from the owner, which the row or the questions name.

| Order | Concept | What it forces | Why here |
|---|---|---|---|
| 1 | The bard, with a dance step that truly strikes | Rig layers that can be masked by body part, a hand that reaches a point on its own body, a press that moves a finger before the next tick, a rule's sound that crosses the wire, and a limb whose sweep the simulation knows | Every memo puts it first, most of its timing machinery exists, and every later tool stands on its hands and its clock |
| 2 | The mender, on a structure first and then on a body | Geometry that changes during a fight at a cost that follows the size of the change, contact that reads the change, a hand that reaches a body chosen a moment ago, and a measure of fit between two fields | All three memos chose a geometry class second. It puts the engine's most distinctive claim, that every shape is a field, onto characters. A structure first (Astra's gate hinge, Fable's breach in a wall) is one instance, and the cutter that exists may draw it, though contact would not yet feel the fit (gap 5). Forty bodies with forty different wounds need the new design |
| 2, beside it | Action alchemy on the ground | Lattices under many moving bodies at fight pace, state for each player in pools, and the hand-off | It shares no gap with the mender, so it does not wait. It is nearly all authored data, it is the owner's example, and it is the first test of slots and calls on something other than the bard |
| 3 | The Dealer, if table play leaves the table (question 4) | Views that differ by recipient for forty moving players, a picture drawn for one viewer only, and a hand-off of something hidden | An MMO needs hidden things everywhere, and the cards exist. It waits on the [disclosure plan](disclosure.md)'s open work |
| 4 | The Reckoner or the Actuary (question 2) | The Reckoner forces little: it is mostly authored rules and is a test of feel. The Actuary forces search over moving bodies, which has no design | The Reckoner can be played early and cheaply to find out whether calculating in a fight feels like being perceptive |
| Later | The Echo; the Bell Diver and the Tabletop Hand; the Clockmaker; the Stagehand | A tape driven onto a second body; a body partly through a portal; worlds at two rates sharing music; geometry that some viewers do not have | Each waits on a design that does not exist, and each is the right forcing function for that design when the lead opens it |

Astra's memo proposes that the first tools meet at one place: a moving gate on
the way from the gallery into the valley, which a newcomer watches, crosses,
and then helps the next person through. That is the right shape for a proving
ground whose places must compose, and
[the composition plan](debug-world-composition.md#the-map) owns where it
goes.

The design for geometry that changes on a body is the longest piece of work
in this list and has no owner yet. It should be opened as a design lane as
soon as the owner answers question 1 in favour of fitting, without waiting
for the bard to finish.

### The engine gaps the memos share

Each row is a gap that more than one source raises. The last column says what
was read in the code for this page and what nobody has checked. Astra's memo
cites documents only. Neon's cites engine types by name without saying what
was read; every type it names exists, which is all this page confirmed for
most of them. Fable's memo states that it read no C# source. An unchecked row
is checked by the lane that opens that concept's plan, before it designs
anything.

| # | The gap | Raised by | In the bard's plan | The engine source, and what was checked |
|---|---|---|---|---|
| 1 | The rig cannot layer by body part: play with the arms while the legs walk or dance | Neon (Reed-Dancer), Fable (Jet Horn), and Astra by implication (which parts of a movement transfer to another body) | [Gap A](instrument-play.md#the-rig) | The caps the bard's plan cites were read: `CreationDocument.MaxDrivers` and `ShapeDocument.MaxSwings`. That a driver's weight reaches every shape it moves is the plan's reading and was not read again |
| 2 | A hand cannot reach a chosen point: its own body, or a body picked a moment ago | All three (a splint on a patient's arm, a wedge into an ally's plate, a palm on a wound, two dancers joining hands) | [Gap B](instrument-play.md#the-rig), for its own body only | Read: `CreationEffectorTargetDocument` has three kinds of target, and the body kind takes a population index fixed in the document. Fable's memo says the solvers "can already target another body", which holds only for a body the author named ahead. Read: `CreationDocument.MaxEffectors` caps a creation's limb solvers at the count Fable's memo gives, and the Moth uses two, for her feet. A solver for each finger does not fit under it |
| 3 | The rig does not read a press | Fable (its gap 2) | [Gap C](instrument-play.md#the-rig) | Read: the signals `CreationDriverDocument` accepts are motion signals, state cells and other drivers. Fable's memo and the bard's plan agree |
| 4 | A limb touches nothing in the simulation | Fable (its gap 1). Astra's splint that "catches under motion" and Neon's wedge punched home need it without saying so | "A strike is a distance test", in [the body](instrument-play.md#the-body) | Read: `CreationEffectorDocument` says its result is presentation only and that "every simulation value" is blind to it. The swept volume and first contact are steps 1 and 2 of [readable interaction](readable-interaction.md#build-order) and are not built |
| 5 | Geometry cannot change on a body, or at a chosen point, during a fight | All three: Astra's impression and splint, Neon's Mosaic-Mender and Lathe-Carver, Fable's Mender (its gap 5) | Not raised | The sources disagree. Neon treats live carving as available. Read: the one moving cut that exists is a shape inside the creation it cuts (`CreationMotion.OperandShapes`), and the validator's own warning says its group bakes again as it moves and "contact still reads the cut at rest" ([moving blends, gap 8](moving-blends.md#gap-8-a-cutter-that-moves-against-the-surface-it-cuts)). The types Neon cites for cheap updates exist (`IRegionalContactField.TryDifference`, `NavigationRuntime`), and their comments describe comparing a field with the one it replaces. Nobody traced them to a rule that carves. One thing no memo noted: a lattice row with a height is already ground that moves as it is drawn and that bodies stand on (`FieldLatticeSolid`), at the size of a lattice cell |
| 6 | One timed sound and pose over a remote connection | Astra (the note and the foot as "one event" on a remote connection), Neon (many voices and ducking at forty), Fable (its gap 8) | [Gaps I, J and K](instrument-play.md#sound) | The [delivered-cue design](instrument-play.md#delivered-cues-and-music-design) carries one disclosed cue to both consumers and defines voice stealing for `VoiceSynth`. |
| 7 | Views that differ by recipient, at forty, in motion | Neon (Stagehand), Fable (Dealer and Actuary; its gap 10), Astra (the Reckoner predicts only from "observations the player actually received") | Not raised | The types exist (`WorldRecipientView`, `WorldStateDisclosure`). Nobody measured the cost at forty recipients, and nobody read whether a picture can be drawn for one viewer. The [disclosure plan](disclosure.md) holds the open work |
| 8 | Rule work that grows with the number of players | Astra (Reckoner, Flock Tender), Neon (Floor-Caller, Fulcrum-Smith), Fable (Actuary) | Its stage 1 reports the cost for each pressing seat | The budget types exist (`RuleWorkBudget`, `RuleCapacity.MaxWorkUnitsPerTick`). No memo counted the work of any concept |
| 9 | A thing cannot be handed from one body to another by play | Fable (its gap 11, marked unconfirmed); Neon and Astra pass things without asking how | Not raised | Read, and now confirmed in part. A carry exists: a body whose kit has the facet holds one rigid body at an offset from its root, and a release gives the held body the carrier's velocity (`WorldCarry`, `WorldCommand.CarryBody`, `WorldCommand.ReleaseCarry`). It starts only from the `body.carry` command. Neither family of rule effects (`WorldEffect`, `ActionEffect`) has a carry, so a kit action or a rule cannot start a catch. A rule can push a rigid body (`WorldEffect.ApplyRigidImpulse`), which is the throw. A body already carried is refused to a second carrier, so a pass is a release and then a carry. The held thing rides the root and not a hand |
| 10 | A recording cannot be played by a second body | Astra (Understudy), Neon (Pocket-Pendulum), Fable (Echo; its gap 4) | Not raised | Read in its comments only: `WorldScopedReplayRuntime` records commands addressed to the bodies its scope owns and puts commands that cross bodies outside its boundary. Fable's warning stands: read the runtime before promising the Echo as a small change |
| 11 | A body is in one world, whole | Astra (Bell Diver: a carried, turning entrance), Fable (Tabletop Hand: an arm through a frame; Clockmaker: a seam crossed many times a minute between rates) | Not raised | Not checked in code by anyone. The memos rest on [Wonderland](../../worlds/debug/wonderland.md) and the [world model](../architecture/worlds.md) |
| 12 | The engine cannot forecast a moving body | Fable (Actuary; its gap 9). Astra's Reckoner avoids it by having the player forecast | Prediction, as a dependency of its remote stage | Not checked in code. [Readable interaction](readable-interaction.md#build-order) lists prediction and rollback as its last step, designed from its research round |

Gaps 1, 2 and 6 change what the bard's first wave should write, and the next
section says how. Gaps 5 and 9 are the new engine work this round names,
and neither is in any plan's order yet. Each goes to the front of an owning
plan's order when its concept is chosen: gap 5 to
[moving blends](moving-blends.md), which owns the cutter, and gap 9 to the
plan the chosen concept opens.

### What the bard's first wave should take from this

This page says above that the bard's rig and sound designs "are written for
hands in general". This round shows what that has to mean, in three places:

1. **The pose clip's mask is the slot.** The design for interpolated poses
   limited to named shapes ([gap A](instrument-play.md#the-rig)) should let an
   author name a set of shapes once, as a body part, and use it for a clip's
   mask and for a tool's reach. Then a second tool on the hands costs no new
   mechanism. [Designed](instrument-play/pose-clips.md#1-a-body-part-is-a-named-set-of-shapes).
2. **The effector target should take a body from state.** The design adds a
   target on the creation's own shape
   ([gap B](instrument-play.md#the-rig)). Every other concept in this round
   needs a hand on a body that a rule chose, and that is the same record with
   one more kind of target. Designing both together avoids a second pass.
   [Designed](instrument-play/pose-clips.md#5-an-effector-reaches-its-own-creation-or-a-body-a-rule-chose).
3. **The delivered cue should carry a call.** A rule's cue that reaches every
   client ([gap I](instrument-play.md#sound)) is also how a client learns that
   a call opened near it. The design should treat a cue's payload as general.

### What is deferred

- Which expression harms, protects or heals, and every number. The owner left
  these open, and no concept here depends on them.
- The look of every tool. Each chosen expression gets a concept-art brief of
  its own, as the bard has.
- A player's own program on the authority, which the findings below raise. No
  concept in the round needs it: the Reckoner's operations are a fixed
  authored set, the Actuary's assumptions are chosen from what a rule offers,
  and the Cartridge-Deck's cartridges are authored.
- The concepts in the last row of the build order, each until its design
  opens.
- Stages and their checks. This section proposes no implementation step. Each
  expression the owner chooses gets a plan of its own, with stages and the
  check that accepts each one, as the bard has.

### The questions the owner chooses

These four are directions and feel. Each one decides which engine work
starts. None is a look value or a number.

1. **In the hands, is play by solving fitting or mixing?** Fitting: something
   is broken as a shape, and you turn a piece until it fits and press it in.
   A friend's condition is then read from their body, a hole or a loose
   plate, and there may be no bar at all. Mixing: action alchemy, where you
   take two substances from the world in two hands, combine them and discover
   what they make, and the ground carries the result. The three memos
   converged on fitting, and your own example was mixing. They could be two
   expressions. Fitting starts the largest new engine design in this round
   (geometry that changes on a body during a fight). Mixing starts on ground
   that mostly exists.
2. **In play by calculating, who does the sum?** The player does: you watch,
   see the pattern, and stand where the thing will be, and you can be wrong
   (the Reckoner). The tool does: the engine plays the next seconds out for
   real, and your skill is what to ask and whom to show (the Actuary). Or
   nobody sums and you set a ratio between two things in the world and hold
   it (the Fulcrum-Smith). The first needs almost nothing new. The second
   needs the engine to forecast moving bodies, which has no design. The third
   needs quantities tied together across bodies.
3. **Is the beat for everyone, or the bard's alone?** For everyone: any
   tool's press can be judged against the world's music, the bard becomes the
   group's hub, and a raid moves as one. The bard's alone: the other
   expressions keep their own time, a careful fit takes as long as it takes,
   and bards trade phrases with those near them. This page recommends a
   middle (every tool may answer the beat and only the bard must) until you
   say which feeling you want.
4. **Does table play leave the table?** The casino's cards and boards could
   enter the fight: a dealer deals a hidden hand to the party in the middle of
   an encounter, or a board is stamped on the ground and the players are its
   pieces. Or the casino stays a place where you sit down. If it leaves the
   table, hidden information among forty moving players becomes the third
   thing the engine is forced to do well.

### What the owner chose

The owner answered all four (the owner's rulings):

1. **Both, mixing first.** Action alchemy is built first, on the ground and
   the Alchemy table; fitting follows as a second expression, and its design
   (geometry that changes on a body during a fight, with contact reading the
   change) opens as a design lane once the bard's first wave is under way.
2. **The player does the sum.** The Reckoner direction: the player watches,
   reads the pattern and commits a position, and may be wrong. Forecasting
   moving bodies is not required of the engine for this expression.
3. **The middle ground on the beat.** Every tool may answer the world's beat;
   only the bard must. The bard is the group's hub without being required.
4. **Table play leaves the table.** Cards and boards enter the fight, so
   hidden information among many moving players is a forcing function: the
   Dealer joins the build order after the mender and alchemy, and the
   withdrawal chokepoint's build is a dependency.

## What a search of the engine found

These are findings for a chosen expression's plan to check and use, not parts
of a design. The ideation round had them, and
[the synthesis](#the-engine-gaps-the-memos-share) says which it checked in
the code.
Each was found by searching for a capability an exploration of solving or
calculating is likely to ask about. A row says what exists and one limit that
was read beside it. Nothing here was reproduced by running.

| A proposal might ask for | What exists | A limit read beside it |
|---|---|---|
| A hand that reaches a point in the world or another body | An effector whose target is a world point in a state cell, or another body's root with an offset (`CreationEffectorTargetDocument`) | The body target is a fixed population index in the document, so a hand cannot reach a body a rule chose a moment ago |
| Pieces that appear, move and vanish under rules | Rule effects that upsert and remove placements (`WorldEffect.UpsertPlacement`); placements attached to a body or swapped on state ([puck-world](../../.claude/skills/puck-world/SKILL.md#limits-worth-knowing)) | A body comes apart only into parts its creation already declares |
| Geometry cut again as a piece moves | A cutter that moves alone and only carves is an operand of the group it cuts, planned again when its snapped pose changes (`CreationMotion.OperandShapes`, `SdfMeshPathBake.Revisit`; [moving blends](moving-blends.md#gap-8-a-cutter-that-moves-against-the-surface-it-cuts)) | Each such move is a new bake of that group, and contact still reads the cut at rest |
| Boards, graphs, rays across cells | Compiled topologies and their queries (`CompiledTopology`, `BoardQueries`) | None read |
| A seeded puzzle that is known to be solvable | Generators with persisted draw cursors (`GeneratorEngine`), and search over journal scopes judged by rules (`ArenaSearch`) | Search's work is priced with the rules' |
| Taking a move back | Arena undo and rule groups (`ArenaUndoGroup`) | None read |
| One instance of a rule program for each of many players | Pools of typed records (`StatePool`; [records and pools](records-and-pools.md)) | Worst-case rule work is priced when the world loads (`RuleWorkBudget`, `RuleCapacity.MaxWorkUnitsPerTick`) |
| Liquids routed through channels | A flow lattice with reactions and a rule effect that paints its cells (`FieldLattice`, `WorldEffect.PaintField`) | The water plan's open gaps ([water](water.md)) |
| A line with a length between two bodies | A tether that caps distance and keeps swing (`FixedTetherConstraint`) | One length between two points; no rope with segments. Whether the tether is drawn was not read |
| A fact about what a surface blocks | Deterministic nearest-surface queries on the contact field (`FixedSurfaceQuery`) | The simulation never reads the renderer, so light and shadow as a rule's fact must be computed this way |
| Many small lights | Lights are authored rows the frame shades | A frame holds 128 (`SdfLights.MaxLights`) |
| Transparent pieces, and pieces that glow | A glass material with its own pass; emission by lane on an opaque material ([rendering skill](../../.claude/skills/rendering/SKILL.md#which-dispatch-owns-the-code)) | The skill's glass contract: exact for up to three front faces along a ray, and absent from the irradiance trace |
| Letters on a surface in the world | Embossed and engraved text runs in a creation (`TextRunDocument`) | The string is a literal that is baked, so text that changes is a new bake |
| Text that changes on a surface in the world | Nothing yet | It is the last step of [the UI overhaul](ui.md), behind its text, tree and interaction steps |
| A line spoken aloud | Text speech from a HUD text element (`TextSpeechSource`) | Placed at the listener, not at a speaker in the world |
| Typed text from a player | Text input events in the timed history (`WorldInputEventKind`) | No keyboard on screen for a pad ([input packet](open-items.md#input-channels)) |
| A drawn gesture | Input patterns over presses and axis crossings in time (`WorldInputPattern`) | No recognizer for a path in two dimensions |
| Knowing what the pointer is on | The frame's picker for presentation (`SdfWorldPicker`), the pointer ray carried in the intent for the authority (`PlayerIntent.SourceRay`), and the tabletop's pointer cell (`WorldTabletopFrame.TryPointerCell`) | A pointer selects one thing; selecting several is an editor package that is not built ([editor](editor.md)) |
| Marking a target | An effect that designates a body into a named register (`WorldEffect.Designate`) | None read |
| Holding a thing and letting it go | A kit's carry facet: a body holds one rigid body at an offset from its root, and a release gives it the carrier's velocity (`WorldCarry`, `WorldCommand.CarryBody`, `WorldCommand.ReleaseCarry`); an effect that pushes a rigid body (`WorldEffect.ApplyRigidImpulse`) | A carry starts only from the `body.carry` command, never from a rule or a kit action, and a carried body is refused to a second carrier |
| Ground that changes shape under rules | A lattice row with a height is ground bodies stand on, and it moves as it is drawn, with a new bake only where a column appears or vanishes (`FieldLatticeSolid`; [the lattice reference](../../.claude/skills/puck-world/references/documents-state.md)) | The surface is columns one lattice cell wide, and contact reads it under the field provider only |
| Playing a recording back | Scoped live replays, which record the commands addressed to a scope's own bodies and drive them again (`WorldScopedReplayRuntime`; [replay](../../.claude/skills/puck-world/references/replay.md#scoped-live-replays)) | Commands that cross bodies are outside a scope's boundary, so a tape played by a second body is not covered. Read in the type's comments only |
| An effect on bodies in an area | Interaction rows over a bounded spatial query (`WorldInteraction`) | None read |
| Different players seeing different parts | Per-recipient views over row and cell visibility (`WorldRecipientView`) | The [disclosure plan](disclosure.md)'s write door is unfinished |
| A grid drawn from live state | Authored render-graph panes (`views.graphs`) | A state lattice bound into such a pass exists only in a quarantined world, and [the port plan](port-experimental.md) wires it into the debug world |
| A view of something else on a surface | Screens that show a machine, a camera, a session or the desktop ([engagement](../../.claude/skills/puck-world/references/engagement.md)) | None read |
| A formula | The expression language rules compile and run (`ExpressionProgram`, `RuleCompiler`) | A player's change to rules or interactions is refused by its kind before it compiles (`WorldRecipientMutations`, `WorldSectionFacts.IsProgram`) |
| A sandboxed program with a budget | Scripting guests charged by fuel (`WorldAddonRuntime`) | A second language beside the rule language |
| A saved composition of operations | Authored `commands` rows that run under the caller's own grants ([commands](../reference/commands.md#document-authored-commands)) | None read |
| Something a character carries from world to world | The owned identity document, which carries a look and a kit that each world may limit ([the game](../decisions/play.md#the-game)) | None read |

One question in these findings is the engine's and not an author's: whether a
player's own program may ever run on the authority. Today only the authority
authors a rule. The
[disclosure plan](disclosure.md#3a-a-declaration-is-judged-inside-a-scope-its-declarer-reads-whole)
designs a grant for declaring inside a scope the declarer reads whole, which
is the nearest existing design. The question is the owner's if a proposal
needs it, and no work waits on it now.
