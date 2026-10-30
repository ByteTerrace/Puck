# Puck standard

The standard is an optional authored design package. The engine supplies input,
layout, camera and motion mechanisms; these documents supply their design.

Import the package from a sibling world:

```puck
import "../standard/standard"
```

`standard.puck` composes `skin.puck`, `controls.puck` and `camera.puck`. The controls document
authors the cursor, icon and badge tables, binding bar, shared pages and play
wheel. `pad-kit.puck` is a library of source lists and shared ring definitions.
The debug world's Go and Tour pages, Places ring and gallery icons live in
[`../debug/controls.puck`](../debug/controls.puck), which refines this package.
The garden and night field import the standard directly.

`camera.puck` authors the shared input feel, the `camera-boom` response and the
zoom bounds/step in `views.seatControl.boom`. Mouse wheel changes the requested
orbit distance; middle click resets distance and angles. An open play wheel
owns scrolling until it closes. `world.view.camera 1 full` reports the requested
distance and bounds, including in a headless World.

`abilities.puck` is a library: `dashAbility(kit, speed, duration, cooldown)`
authors a kit action over the generic timed planar impulse. The avatar declares
the input channel and timer states and owns its tuning and rig response. The
Moth's [dash document](../debug/avatars/moth-dash.puck) supplies its burst; West/J activates it on Move.
Run remains Shift while held, with N/left-stick press switching run on and off.

The Bar ring selects the `standard` clusters, recovered `keys` and `pad`
strips, `wings`, and `sidebars`. The strips retain the geometry authored at
`8962c3d5a7`. Wings use the mirrored six-slot geometry at `e5841bfdad`:
45/600 plates, a 60/600 central gap, and 24/45 badges offset by 0.4375 plates.
That historical C# implementation already
replaced secondary bars with multiplayer quadrants. The standard authors
the requested fan-out: each secondary bank moves three pitches outward and
0.75 pitches downward. Its primary anchor and modifier anchor retain the
reference's 220/600 lift before the viewport fitting step. The shared group
inset accounts for all three standard banks or all five debug banks. Width
fitting may shrink the whole design; the lead's windowed captures judge this
new spacing. Sidebars place the Move strip at the bottom and the View/Debug
columns at the left/right edges. The debug refinement adds Go and Tour to
those designs without putting its places or pages in the standard.

From the console, `run controls.bar keys` selects a swapping strip;
`run controls.bar wings wings` and `run controls.bar sidebars wings` show
every page at its authored location. `player.binding-bar model single`
returns to one swapping page. `run controls.bar.compact` applies a 0.72
plate multiplier and `run controls.bar.full` restores 1. These are per-seat
preferences; a wheel sector acts on the seat that opened it. They join
session rebindings when `identity.bindings.save` saves the selected identity.
`world.binding-bar` reads the resolved layout name, model and scale.

The engine's `player.binding-bar layout|model|scale <value> [seat]` is the
generic setter those commands compose. `auto` clears one session preference.
Without a seat preference, the authored `LayoutCell` and `ModelCell` still
select the world design, with `single` displaying one bank and any other
model displaying every bank placed by the selected layout.

An import composes rows by id. To change a standard design, declare its id in
the importing document's own body; nested objects refine inherited fields.
For example, change the bar's plate size without copying its layout:

```puck
bindingOverlays [
  {
    id: "bare"
    bindingBar {
      layouts {
        standard { buttonSize: 0.045 }
      }
    }
  }
]
```

Unkeyed lists, including a binding overlay's `chords`, replace as a whole.
To replace a keyed list, begin it with `{ $replace: true }`; to remove a row,
declare its id with `$drop: true`. Imports that disagree on the same field need
an explicit override in the importing document. An author can also import only
`controls` or `skin`, or omit this package and author a complete replacement.
See [document composition](../../.claude/skills/puck-world/references/documents-composition.md#document-composition-basis-and-imports).
