# Debug world press

The press is a typesetter's bench against the west wall of the gallery lobby,
between the hanging map board and the desktop computer, with its composing bed
tilted toward the room.
[`press.puck`](press.puck) holds it and `debug.puck` imports it whole
(`import "press"`). The game is the standard package's typing play
([`typing.puck`](../standard/typing.puck)); the press authors the bench, the
words, the panel, the lit keys, the sound and the kept still. Its design is in
[the game notes](../../docs/game/press.md).

## Playing

`debug.go press` stands the Moth a step south of the bench, in reach, so the
chase camera shows the bed beside her. Press the context
button (E, or the pad's RB) to sit. A slug rises at the head of a channel with a
word on the panel: type the word. Tab (or the pad's B) stands her up.

Slugs step down the bed on the score's beat. A word typed through sets its slug;
a slug that reaches the foot of the bed is lost, and three lost end the run. Six
set words turn the page, which opens another channel and a longer case of type.

On a keyboard whose keys light, the letter to strike is amber, the two after it
dimmer, a waiting word's first letter blue, and the stand-up key grey. A struck
letter flashes white, a wrong one flashes every letter red.

## Reading it

| To read | Console line |
|---|---|
| The run: phase, score, best, run of set words, page, slugs left | `world.state typing` |
| Each channel's word, letters typed, steps taken | `world.state typingShown`, `world.state typingTyped`, `world.state typingStep` |
| The characters the keyboard lights | `world.state typingKeys` |
| The lamps as painted, and the lamp arrays written | `lights.state` |
| The panel's text as resolved, markup included | `world.hud` |
| The layers in the mix and the last cue and stinger sounded | `speaker.state` |
| What the mixer was last handed (a layer's bed arriving) | `audio.mix` |
| The stills kept of best scores | `capture.status` |

A scenario or a script types with `player.signal keyboard.text "<letters>"` and
sits with `player.signal keyboard.e press`;
[`press-tests.puck`](press-tests.puck) plays three short runs this way.

## What is authored where

| Part | Rows |
|---|---|
| The rules of play, the state and the typing page | `typingPlay(...)` in the standard, called once in `press.puck` |
| The word list, three cases | `pressWords` |
| The bench and its slugs | the `press` prototype: two drivers a slug read `state.typingLive.<lane>` and `state.typingStep.<lane>` |
| The panel | the `press` and `press-over` HUD panels; a lane's text is marked up with the effect `typingStyle` names |
| The keyboard | `keyLights` |
| The still of a best score | `highlights` |
| The music | the `press` and `press-run` segments of [`score.music.json`](score.music.json), the `press-*` tunes and patches, and two `audio.cues` rows in `guide.puck` |

`lights.state` answers in every boot shape. The audio lines and
`capture.status` need a window. `music.state` reads the authority's music
director, which does not hear a rule's cues, so it stays on `verse` through a
run: the segment a player hears is the listener's, which `speaker.state` shows
by the layers it plays.

## Known limits

The chase camera is not reframed when she sits, the words are on the panel and
not on the slugs, and a layer joins when its cue arrives rather than on a bar
line. The design page lists what each needs.
