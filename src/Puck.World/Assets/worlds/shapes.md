# Debug world shape gallery

The debug world lays every authored primitive, prism profile, lift and per-shape
facet out on the open ground at +X/+Z, one prototype an exhibit, so a change to
the field, the bake or the renderer shows on every shape at once.
[`shapes.puck`](shapes.puck) holds the exhibits; `debug.puck` imports it, expands
`galleryPrototypes()` in its `prototypes` block and `galleryPlacements()` in its
`placements` block, and declares the font the labels emboss in.

Each exhibit hangs 1.2 m over a dark plinth whose label faces -Z, toward the
origin. The plinth is the exhibit's last shape, so an exhibit's subtraction or
intersection never cuts it. A plinth that renders wrong shows the exhibit's
scope or bound reaching its siblings. Exhibits stand 3 m apart from x = 14.

| Row | z | Exhibits, from x = 14 |
|---|---|---|
| Uniform | 14 | sphere, box, torus, cylinder, capsule, ellipsoid, round cone, cone, superellipsoid (exponent 4), plane (a sphere intersected with a tilted plane) |
| Stretched | 19 | sphere, box, torus with X ≠ Z, torus with Y alone stretched, cylinder with X ≠ Z, capsule with X ≠ Z, ellipsoid, round cone, cone with X ≠ Z, superellipsoid (exponent 3) |
| Extruded profiles | 24 | trapezoid, rounded rectangle, polygon 6, polygon 12, polygon 12 with X ≠ Y, ellipse, chamfered rectangle, convex pentagon, path fill, path stroke, path stroke with X ≠ Y |
| Revolved profiles | 29 | trapezoid, rounded rectangle, polygon 6, ellipse, chamfered rectangle, convex pentagon |
| Sweeps and facets | 34 | sweep, tapered sweep, three-strand sweep, rounding, chamfer, onion, dilate, flare, shear, bumps, cells F1, cells F2 − F1, polar fold |

Prototype names are `shape-<exhibit>` (`shape-torus-xz`, `shape-revolve-convex`);
the rows' arrays in `shapes.puck` list them in order.

## Looking at one exhibit

Exhibit `i` of a row stands at x = 14 + 3i. The operator camera frames it from
the label side:

```text
view.override camera look <x> 1.9 <z - 2.8> <x> 1.2 <z>
```

`view.override camera look 26 1.9 21.2 26 1.2 24` frames the polygon 12 with
X ≠ Y, for example. `view.override camera look <x> 2.3 <z - 4.4> <x> 1 <z>`
frames three exhibits of a row at once. `view.override camera auto` returns the
seat's view.

Every inspection the debug world offers works on an exhibit:

- `world.debug-view normals` shows the baked mesh's shading normals, where a
  frayed edge, a crease cut short or a torn facet shows plainly; `world.debug-view
  off` restores the image.
- `world.wait bakes 60` waits for the bake after a `world.reload`, so a capture
  shows the baked mesh and not the bake in progress.
