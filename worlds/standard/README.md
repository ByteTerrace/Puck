# Puck standard

[paths.puck](paths.puck) supplies reusable quadratic corridor masks for paving and
scenery exclusions. It is a pure authoring library; a world owns its routes and widths.

The standard is an optional authored design package. The engine supplies input,
layout, camera and motion mechanisms; these documents supply their design.

Start from the worked example, [`../minimal/minimal.puck`](../minimal/minimal.puck): a world that imports the
standard through its plain walker and declares only its floor, its spawn point and its seat count. It boots on its
own (`--world worlds/minimal`), and everything it seats, frames, binds and builds with is the standard's.

```puck
import "../standard/walker"
```

`standard.puck` composes `inputs.puck`, `quality.puck`, `skin.puck`, `controls.puck`, `editor.puck` and
`companions.puck`, and expands `camera.puck`'s `cameraFeel()`. It names no schema, so it is a fragment the importer
composes, and it depends on nothing outside this package: its controls author the resting page (the `bare` overlay)
they refine. `walker.puck` is the plain avatar: a capsule that walks, runs, jumps and falls, framed by `chaseCamera`.
It imports the standard and refines it, so a world imports it in the standard's place; an author's own avatar refines
its `walker` kit, look and prototype by id, or replaces it (the debug world's Moth, whose own controls refine the
standard's in [`../debug/moth-controls.puck`](../debug/moth-controls.puck)).

`motion.puck` authors the standard's body motion programs: `walk`, which both the walker and the Moth import, so a
correction to it reaches every avatar that walks. An avatar's speeds, holds, turn and actions stay in its own kit; a
world refines the program itself by declaring a `bodyMotionPrograms` row named `walk` in its own body.

`skin.puck` also authors the standard's screen materials (`screenMaterials`): the display treatment a screen row or a
creation face names by id (`material: crt`), the CRT among them. A screen's picture always fills its face; a
material draws the display's pixel structure over it (scanlines, subpixel stripes, a vignette, a glint and a soft
rounded edge), and a device's bezel is geometry it authors round the face. A material id is a module's name like a
state row's: an aliased import qualifies it, and a host names one an import declares only when that import exports it
under `bindings`, as `skin.puck`, `standard.puck` and `walker.puck` export `crt`.

`inputs.puck` declares the shared semantic channels (`forward`, `strafe`, `turn`, the face axes, `jump`, `run`,
`flight`, `rise`, `dash`, `interact`, `start`, `select`, `confirm`, `cancel`) the controls and the games bind; a world
declares its own actions beside them (the debug world's `suit` and `noclip`). `quality.puck` authors the
`world.quality low|medium|high` presets; until initial development is done every machine runs `high`. `skies.puck` is
a library of sky and air presets (`clearDay`, `overcast`, `starryNight`, `polarNight`, `clearDayAir`,
`overcastAir`) a world expands inside its `render.sky` and `render.atmosphere` blocks, and the day cycle
(`dayCycle`), which a world expands at the top of a fragment of its own: a state clock whose row holds the day's phase,
and on it the `key` light's direction, colour and illuminance and a clear sky that turns to night
(`worlds/debug/day.puck` is the worked example).

`editor.puck` authors build mode: the `editor` section (the grid, snapping, editor-camera feel, the build toggle every
resting page carries, the read-only label and the build bar) and the `editor` binding overlay, whose context row
selects its build page while a seat builds. A world without the section has no build controls, and the editor verbs
refuse by name. The controls document
authors the cursor, icon and badge tables, binding bar, shared pages and play
wheel. `pad-kit.puck` is a library of source lists and shared ring definitions.
The standard's rings and pages name only what the standard provides. The Moth's
Respawn, Suit, layout and jetpack controls live in
[`../debug/moth-controls.puck`](../debug/moth-controls.puck), which the debug world,
the garden and the night field import in the standard's place; the debug world's
Go and Tour pages, Places ring and gallery icons live in
[`../debug/controls.puck`](../debug/controls.puck), which refines those.

`styles.puck` is a library of cel styles (`celStyle`, `celHardStyle`, `continuousStyle`): a palette row names one
as its own (`toon: celStyle`) and a world names one as the default every other material inherits
(`render { toon: celStyle }`), which `world.toon` switches live.

`sealed.puck` is a library of sealed inboxes (`sealedInbox`): a row per seat that only that seat and the world's rules
read, through which a seat hands a game a value no other seat may read (a sealed bid, a vote, a whisper, an order),
and which a game's own rule collects. The games in [`../games/`](../games/README.md) use it.

`bounds.puck` is a library of boxes given their bounds (`boundsBox` for a world-space box in a creation at the origin,
`localBox` for one in the creation's own frame, `barRing` for the three bars round a canvas), for shells, furniture and
frames built from boxes.

`kits.puck` is a library of kit parts: the gravity and the ground, fall and air holds a body wears (`groundHold`,
`fallHold`, `airHold`), the motions a kit derives its own from with `with` (`walkerMotion`, the walker's 4 m/s walk
and run, and `stillMotion`, a body the rules pose), and the kits of a piece that stays where it is put (`pieceKit` for
a rigid piece the physics settles, `heldKit` for a cursor the rules pose).

`seat.puck` is a library of a seated player's buttons at a table game: the buttons and the channel each reads
(`seatButtons`, `seatSteps`), whether one is held past half way (`seatHeld`), and the rule template that records its
press in a seat row's `<button>Edge` and `<button>Down` cells (`seatPress`), which a game's presses rule calls in a
`for` over its buttons.

`terrain.puck` and `foliage.puck` are optional libraries. Foliage supplies
`broadleafTree`, `mossyRock`, `grassTuft`, `sceneryPatch`, `sceneryScatter` and
`windMeadowTile`; import it with `import "../standard/foliage"` and expand its
templates inside `prototypes` or `placements`. `sceneryScatter` translates a
table of positions, scales and yaws by an origin, with optional solid contact.
The [debug meadow](../debug/meadow.md) is the worked placement and wind example.

`hydrology.puck` is a library: `hydrology(...)` authors a conserved water cycle on a flow lattice (a melt clock, a
glacier's meltwater, a lake at its sill, falls, a river and an aquifer that levels against it) and its gauges, and
`water.puck` is the scenery bound to them (`riverChannel`, `fallsRibbon`, `glacierMass`, `lakeSurface`, `floatGauge`,
`stoneBridge`). The river is a `waterViews` field body in a cut channel and the falls are geometry, each with shading that flows: the standard's `streamFlowLayer`,
`fallsFlowLayer` and `lakeFlowLayer` are palette `flow` layers, and `foliage.puck`'s `meadowWind` runs the same layer in
world space over the meadow's grass. `kits.puck`'s `swimHold` is the hold a kit leads its list with to swim where a world has a medium. The
debug world's [water](../debug/debug.md#water) is the worked example.

`camera.puck` is a library. `cameraFeel()` authors the shared input feel (pointer, stick and gyro), the `camera-boom`
response, the zoom bounds/step in `views.seatControl.boom` and the `views.builtinTransition` envelope (seconds and render-scale dip) of the built-in seat ladder; `chaseCamera(rig, ...)` authors a seated body's whole
chase camera (seat control, clearance mode with each treatment's rise ceiling and close target, the eye's radius and
skin, character fade, automatic first person, the boom and the rig) from the body's own offsets, with the chase-camera
decision's values as its defaults. An avatar expands it in a document of its own: the
Moth's is [`moth-camera.puck`](../debug/avatars/moth-camera.puck). Mouse wheel changes the requested
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

A context overlay replaces only what it declares
([decision](../../docs/decisions/input-channels.md#an-overlay-replaces-only-what-it-declares)).
Sitting at a board (the debug world's `tabletop` overlay, `../debug/chess.puck`)
makes its Board page replace page 1, the resting page. Every page and chord
command the overlay does not declare stays the player's: the triggers still open
View and Debug, the shoulder and Tab still open the wheel. An overlay page that
binds a control takes that control over, so the board's two mouse buttons pick
and cancel instead of walking. The layout, model and scale stay as the player
or world set them, and leaving restores the page before. No bank names Board, so it takes the place
of the bank whose page the same held modifiers select (Move's, the resting
page): under `single` the bar shows Board alone, under full wings every page
with Board active. `world.binding-bar` reads the active `page` and the
`activeBank` it draws in.

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
