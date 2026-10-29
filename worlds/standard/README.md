# Puck standard

The standard is an optional authored design package. The engine supplies input,
layout, camera and motion mechanisms; these documents supply their design.

Import the package from a sibling world:

```puck
import "../standard/standard"
```

`standard.puck` composes `skin.puck` and `controls.puck`. The controls document
authors the cursor, icon and badge tables, binding bar, shared pages and play
wheel. `pad-kit.puck` is a library of source lists and shared ring definitions.
The debug world's Go and Tour pages, Places ring and gallery icons live in
[`../debug/controls.puck`](../debug/controls.puck), which refines this package.
The garden and night field import the standard directly.

The Bar ring selects the `standard` clusters, recovered `keys` and `pad`
strips, `wings`, and `sidebars`. Wings use the same mirrored six-slot
diamonds as the addon reference, with each secondary bank pushed outward and
downward; sidebars place the Move strip at the bottom and the View/Debug
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
