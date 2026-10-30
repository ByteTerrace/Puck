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
