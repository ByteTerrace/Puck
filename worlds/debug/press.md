# Debug world press

The press is a typesetter's bench against the west wall of the gallery lobby,
between the hanging map board and the desktop computer, with its composing bed
tilted toward the room.
[`press.puck`](press.puck) holds it and `debug.puck` imports it whole
(`import "press"`). The game is the standard package's typing play
([`typing.puck`](../standard/typing.puck)); the press authors the bench, the
words and the lettering that puts them on the slugs, the proof, the seated
view, the lit keys, the sound and the kept still. Its design is in
[the game notes](../../docs/game/press.md).

## Playing

`debug.go press` stands the Moth square in front of the bench, in reach. Press
the context button (E, or the pad's RB) to sit: the camera leans in over the
bed, and a slug rises at the head of a channel with a word on its face. Type the
word. Tab (or the pad's B) stands her up and the chase camera comes back.

Slugs step down the bed on the score's beat. A word typed through sets its slug;
a slug that reaches the foot of the bed is lost, and three lost end the run. Six
set words turn the page, which opens another channel and a longer case of type.
The proof pinned beside the upper case shows the page, the score, the best score
and the slugs left.

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
| The seated view: the framing row held and how far the camera has eased onto it | `world.view.camera 1 framing,framingWeight` |
| A slug's lettering as authored | `world.row creations press document.palette[3].paints[0].text` |
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
| The words on the slugs and the proof | text paints on the prototype's brass and paper palette slots (`paints[].text`); a lane's text is marked up with the effect `typingStyle` names |
| The seated view | the `press-seat` camera program and the `press` row of `views.framings`, which reads `state.typingSeat` |
| The run's last line | the `press-over` HUD panel |
| The keyboard | `keyLights` |
| The still of a best score | `highlights` |
| The music | the `press` and `press-run` segments of [`score.music.json`](score.music.json), the `press-*` tunes and patches, and two `audio.cues` rows in `guide.puck` |

`lights.state` and `music.state` answer in every boot shape. The other audio
lines and `capture.status` need a window. `music.state` reads the score the
player hears: `press` once she sits, `press-run` from the second page, and each
voice as `<tune>@<tick>`, where the tick is the bar line it joined on.

## Known limits

The design page lists them: the stick does not fill with set words, one seat
plays, and a shaken word smears while it shakes.
