# Field Notes: the learning games

The world and its characters come from Puck's own art and ideas, and the Moth is the main character of every game
in the family unless the owner says otherwise (the owner's ruling). The hero is the Moth as the
[concept pack](../art/moth-concept-pack/README.md#preserve-the-character) draws her: dusty lavender and ivory suit,
the hood with its round ear discs, her braid. The places are the debug world's own: the valley floor, the pond plot,
the glacier, the gallery and its great tree, the toybox and the two moons. Nothing in the family recreates another
game, its names, characters or levels.

*Field Notes* is a family of three original games on Puck's emulated machines: the Humble Gaming Deck
(NES-class), the Humble Gaming Brick (Game Boy-class) and the Advanced Gaming Brick (GBA-class). It is Puck's first
learning content, and each game is meant to push its machine's authoring as far as it goes.

## Premise and loop

The gallery's exhibits have strayed into the valley, and the valley's creatures have picked up what the gallery's
plaques teach. The Moth walks each place, meets its critters (swallows, dragonflies and the garden's hound pups) and
answers the question each one asks: reading, maths or logic, by the critter's kind. A right answer earns a clue that
names a spot in the scene, the old fir or the pond reeds; searching that spot turns up the key; the key opens the
place's chest, which holds a stray exhibit, and opens the way on. A wrong answer sends the critter off for a while
and shows the right answer, so every question teaches something. The game never says what a world or a portal is;
the Moth simply walks from the valley into the gallery and on to the moons.

## One world, three devices

The three games are one world seen through three machines. Each place belongs to one device (`places` in
[places.puck](../../../worlds/standard/learning/places.puck)): the Brick shows the outdoor valley (the valley floor,
the pond plot, the glacier rim), the Deck the gallery's halls and game room, and the Advanced Brick the great tree
and the moons. Places run in order, so finishing the valley opens the gallery on the Deck.

Progress travels as the **notes record** (`notesRecord`): places finished, one bit per treasure returned, and a skill
per subject. Every device keeps it in its battery save. Two machines pass it as follows:

- **Brick to Advanced Brick, by link cable.** Both machines carry a serial link, and the emulators link them; the
  cartridge language has no step that exchanges bytes over it yet. That is the next wall to fill: a generic `send`
  and `receive` of declared state over the link, in both native compilers, so a game copies the record across.
- **To and from the Deck, by a notes code.** The Deck has no link, so a device shows the record as a short code in
  the shared alphabet and the other reads it back: the record's six bytes packed into eight glyphs of the alphabet's
  first 32 codes (five bits each, forty bits), the last glyph carrying a check of the other seven.

## What each machine is pushed on

| Machine | What the game stresses |
|---|---|
| Humble Gaming Brick | 256 tiles shared by font, scene and sprites; ten sprites a line (the Moth's four, three critters, the key, the answer pointer); the panel drawn over the scrolling valley as status line and text box, typed six cells a frame into the 24-entry map queue; a 32 by 32 scene scrolled with the camera; the text bank as long arrays read through a two-byte cursor; the boot-only payloads in switchable banks; the battery save; one source for the Color and monochrome machines. |
| Humble Gaming Deck | The `hgd` cartridge target: eight sprites a line, nametable mirroring, attribute colour per 16 by 16 block, mapper banks for the gallery's larger rooms. |
| Advanced Gaming Brick | Mode-0 layers for depth in the great tree, the turning background for the moons' sky and the flight between them, blending for lantern light, recorded samples for critter calls, and the larger question bank its memory holds. |

## The learning content

The content is target-independent `.puck` data in the standard package,
[`worlds/standard/learning/`](../../../worlds/standard/learning/), which any cartridge or world imports:

- [text.puck](../../../worlds/standard/learning/text.puck): one alphabet and its 8 by 8 glyphs, and the compile-time
  functions that turn text into glyph codes and wrap it into a text box. Every text fits the narrowest box a game
  draws, three lines of 18 characters with answers of five, so every device shows it unchanged.
- [questions.puck](../../../worlds/standard/learning/questions.puck): the question bank by subject and level, the maths
  drills a device writes itself from its own seeded draw (an operation over two ranges per level), and `adaptation`.
- [clues.puck](../../../worlds/standard/learning/clues.puck): the spots a clue can name, and `clueText`, which names
  a spot directly at level 1, describes it at level 2 and gives a riddle at level 3.
- [finds.puck](../../../worlds/standard/learning/finds.puck): the critters, each with its subject and its motion in
  units every device reads, and the treasures, the gallery's strayed exhibits.
- [places.puck](../../../worlds/standard/learning/places.puck): the places and the notes record.

Difficulty adapts per subject: a skill from 0 to 8 rises by one on a right answer and falls by two on a wrong one,
and the level asked is one more than the skill over three, never below the floor of the place being played. A
maths critter asks a drill half the time, so the level's arithmetic is practised without the bank running dry.

## The Brick slice

[field-notes.puck](../../../worlds/debug/cartridges/shared/field-notes.puck) is the Brick game's first place, the
valley floor, compiled for the Color machine ([cgb](../../../worlds/debug/cartridges/cgb/field-notes.cgb.puck)) and
the monochrome one ([dmg](../../../worlds/debug/cartridges/dmg/field-notes.dmg.puck)). It plays the whole loop:
title, walking the scrolling valley, critters that wander at their own pace, a question from the bank or a drill, a
clue, searching the named spot (A faces and searches; select shows the clue again), the key, the chest, the door,
music and effects, and a save of the notes record before the next place. The pond plot and the glacier rim replay
the valley floor's scene at their own level floor and with their own treasure until their scenes are drawn.

The slice rests on these cartridge-language capabilities, all generic
([cartridge forge](../../emulation/shared/cartridge-forge.md#source-contract)):

- A map step's `surface: "panel"` writes the panel's own map at run time, which is how the text box is typed.
- An array longer than a byte index reaches holds the text bank, read through a two-byte cursor.
- Value functions and members of compile-time records read in cartridge operands, so the shared content and its
  helpers are written once and read where the game uses them.
- The Brick compiler places the array seed table in a switchable bank once the fixed window fills, and music reaches
  down to octave 2 for the wave voice's bass.
