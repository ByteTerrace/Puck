# The press: a typing game in the world

The press is a typesetter's bench in the debug world's gallery lobby. A player
walks up to it, sits, and sets type by typing. It is the debug world's place for
everything a keyboard is beyond movement keys: typed text as timed input, the
lamps under the keys, text that moves a glyph at a time, music that follows
play, and a kept picture of a best run. This page is its design. How to play it
and read it over the console is in [the place's own page](../../worlds/debug/press.md).

## The fantasy

Puck's worlds are written. Everything a player sees in one was typed by an
author, so the game about typing is a game about setting words into the world,
not about shooting things with them. The bench has three channels. Slugs of
type, each carrying a word, come down the channels toward the composing stick
at the foot of the bed, one step on each beat of the world's score. Type a
slug's word and it is set: it drops back into the bed and the next one is cast.
Leave a slug alone and it reaches the stick unset and is lost. Three lost slugs
and the run is over, "out of sorts", which is what a printer who has run out of
a letter is.

The genre is typing action: words arrive, the keyboard is the only control, and
speed and accuracy are the score. The design is the press's own. Nothing is
attacked, the pressure is a rhythm rather than a chase, and a run is a page set
well rather than a wave survived.

## Rules of play

- A typed letter that begins a waiting word locks the player onto that slug,
  the furthest along if several begin with it. Each letter after it must be the
  word's next.
- A wrong letter is a miss. It ends the run of set words and the lock holds.
  There is no backspace: type is cast, not edited.
- A set word scores its length times ten, plus two a letter for each word of
  the run before it, up to ten words.
- Six set words turn the page. Page one runs one channel of three and four
  letter words at a step every two beats. Page two opens a second channel and
  the five and six letter case. Page three opens the third channel and the long
  case, and the slugs step on every beat.
- Standing up ends the run at once. The best score stays.

Every number above is a parameter of the standard package's typing play, and the
words are a list of strings in the place's document.

## How it teaches without a word of instruction

The bench is in the lobby, the first room a player crosses on the way to the
exhibits, between the map board and the desktop computer, with `PRESS` raised
on its front like any plaque.

1. Standing in reach, the keyboard goes dark but for one key, the context
   button's, in the bench's brass colour. A player with a lit keyboard is shown
   the one thing to do. A player without one has the binding bar, as at every
   table in the world.
2. Seated, the first slug rises out of the bed and its word appears on the
   panel. The word's first letter lights blue on the keyboard. The first press
   is the lesson: the letter on the panel lifts, the key flashes white, the next
   key lights, and the keys after it glow dimmer, so the hand is led one letter
   ahead of the eye.
3. A wrong key flashes every letter red and shakes the word.
4. Page one cannot be failed quickly: one short word at a time, ten seconds to
   set it. By the time two channels run, the lock on a word has been met a dozen
   times.
5. The music is the last teacher. The slugs step on its beat from the first
   one, and setting words in a row brings another voice in, so a player hears
   that they are doing well before reading the score.

## The capabilities it wires, and how each is used for real

| Capability | What the press does with it | Authored in |
|---|---|---|
| Typed text as input | Every character is a Text event at the time it was typed, judged by one rule against the word's letters. A replay reproduces every keystroke. | `typingPlay` in `worlds/standard/typing.puck` |
| Keyboard lamps | The `keyLights` section lights the next letter, the two after it, each waiting word's first letter and the stand-up key, from state cells and the active binding page, and flashes on a hit, a miss and a lost slug. | `worlds/debug/press.puck` |
| Per-glyph text | The lane's word is marked-up HUD text: a `caret` effect dims the typed letters and lifts the next, and the rule swaps it for `shake` for a moment after a miss. | `press.puck`, drawn by the HUD writer through `TextGlyphChannel` |
| Music direction | The score moves to the `press` segment when a run starts and `press-run` at the second page. Every voice joins on a bar line; a second and a third are layers held by cues the rules emit while the run of set words lasts, and a set word, a lost slug and a page turn are stingers. | `worlds/debug/score.music.json`, the rules' cues |
| Frame capture | A `highlights` row keeps the frame half a second after a new best score, through the capture sink. | `press.puck` |
| Speech | The run's last line, `OUT OF SORTS`, is spoken once by the HUD's text speech. | `press.puck` |
| State-driven motion | The slugs are parts of the bench's creation. Two drivers on each read the lane's state cells: one lifts it out of the bed, one carries it down the slope, eased so it settles into each notch. | `press.puck` |

The engine learned five things to make this possible, each general: the
`keyLights` and `highlights` document sections with the loop that writes lamp
arrays, HUD text markup, a `caret` text effect, and a `scalars` builtin that
turns an authored string into the numbers a Text event carries. None of them
knows the press exists.

## What makes it Puck

- **It is a place.** The game is not a mode or a menu entry. It has a spot in
  the lobby, a spawn point, a region other rules and the benchmark route can
  read, and a body that stands in front of it.
- **It is documents.** The rules, the words, the bench, the panel, the lamps
  and the sound are `.puck` and three small JSON assets. The reusable half, a
  typing game with no place or look, is in the standard package for any world
  to import with its own bench and word list.
- **It runs where the world runs.** The judgement is simulation state, so it
  is the same on a remote authority as in one process, and a headless scenario
  plays a run by submitting text.
- **The keyboard is part of the world.** Lamps, panel and sound are three
  readings of the same state cells, so they cannot disagree, and another seat's
  view of the bench shows the same slugs.

## What it does not do yet

- The bench is a first shape: a cabinet, a tilted bed, four rails and three
  brass slugs. It reads as a bench with a plaque, and it is below the visual
  bar the rest of the lobby is held to.

- The camera stays the chase camera. A seated framing of the bed, as the
  board tables have, needs the bench to be a tabletop or a camera state of its
  own.
- The words ride the panel, not the slugs. Text on a moving creation part is
  baked relief today, so a slug cannot carry a word that changes.
- A lost slug and a set one leave the bed the same way. The stick should fill
  with the set words.
- One seat plays. The standard's play takes a seat number; a second bench or a
  shared one needs the lanes keyed by seat.
