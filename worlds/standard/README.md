# Puck standard

The standard is an optional authored design package. The engine supplies input,
layout, camera and motion mechanisms; these documents supply their design.

Import the package from a sibling world:

```puck
import "../standard/standard"
```

`standard.puck` composes `skin.puck`, `controls.puck` and `camera.puck`. It names no schema, so it is a
fragment that refines the importer's rows (the Moth basis's `bare` overlay among them) and not a world that boots on
its own. The controls document
authors the cursor, icon and badge tables, binding bar, shared pages and play
wheel. `pad-kit.puck` is a library of source lists and shared ring definitions.
The debug world's Go and Tour pages, Places ring and gallery icons live in
[`../debug/controls.puck`](../debug/controls.puck), which refines this package.
The garden and night field import the standard directly.

`camera.puck` authors the shared input feel, the `camera-boom` response and the
zoom bounds/step in `views.seatControl.boom`. Mouse wheel changes the requested
orbit distance; middle click resets distance and angles. The play wheel's hold
pages bind `mouse.wheel` to `player.wheel.scroll`, replacing the inherited zoom
source by source, so scrolling steps the open wheel's ring and leaves the boom
alone until the wheel closes. `world.view.camera 1 full` reports the requested
distance and bounds, including in a headless World.

`abilities.puck` is a library: `dashAbility(kit, speed, duration, cooldown)`
authors a kit action over the generic timed planar impulse. The avatar declares
the input channel and timer states and owns its tuning and rig response. The
Moth's [dash document](../debug/avatars/moth-dash.puck) supplies its burst; West/J activates it on Move.
Run remains Shift while held, with N/left-stick press switching run on and off.

The standard authors two bar layouts; every page fills the same twelve pad
slots (the d-pad, LB, L3, the face buttons, RB and R3) in both. `wings` is the
default, drawn with the `single` model: the mirrored six-slot diamonds (45/600
plates, a 60/600 central gap, 24/45 badges offset by 0.4375 plates) showing the
active page alone. Full wings (`wings wings`) draws every page at once: View
and Debug nest above the primary pair, one pitch inward and two outward, two
pitches up, after the
[GamePadActionBars](https://github.com/ByteTerrace/WorldOfWarcraft.Addons.GamePadActionBars)
addon; inactive banks draw at half alpha. `sidebars` is straight bars at the
wings plate size, likewise single (the active page in the bottom strip) or full
(`sidebars wings`: the Move strip along the bottom edge, the View/Debug columns
at the left/right edges), with the modifier row and page name clear above the
strip. The debug refinement adds Go and Tour to both (below the primary in
full wings, inside the side columns) without putting its places or pages in
the standard.

A player swaps live from the play wheel's Bar ring (Wings, Full wings,
Sidebars, Full sidebars, Compact, Full size) or the console: `run controls.bar
wings`, `run controls.bar wings wings`, `run controls.bar sidebars` and
`run controls.bar sidebars wings`.
`run controls.bar.compact` applies a 0.72 plate multiplier and
`run controls.bar.full` restores 1. These are per-seat preferences; a wheel
sector acts on the seat that opened it. They join session rebindings when
`identity.bindings.save` saves the selected identity. `world.binding-bar`
reads the resolved layout name, model, scale and placed slots.

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
