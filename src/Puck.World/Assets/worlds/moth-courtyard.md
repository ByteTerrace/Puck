# Moth courtyard

[moth-courtyard.world.json](moth-courtyard.world.json) imports
[the Moth studio](avatars/moth.md) through `basis`. All eight bodies share its creation;
the courtyard supplies pose drivers, placement, cameras and environment.
This is a diagnostic scene for visual inspection and GPU profiling.

## Landscape

The pose stations sit in a grassy clearing connected by irregular sandy patches.
Five low banks give the surrounding ground relief. Six broadleaf trees frame the
clearing with root flares, branching trunks and layered crowns; seven mossy rocks
and thirteen clusters of grass tufts break up the edges of the sandy route.

The floor, trees and rocks are solid. The banks and sandy patches have modeled
relief, while surface weathering adds color variation to the grass and bark.
Fine noise relief is visual: contact follows the underlying shapes, with the
ground's displacement limited to one centimeter. Grass tufts are decorative.
Keep the central stations and inspection-camera corridors open when adjusting
the scenery tables in the source.

The dense meadow beside the path uses sixteen bounded tiles containing 1,994 two-part blades.
Their roots stay planted while a spatially phased gust bends the stems and
lighter flutter moves the tips. Dark roots and lighter tips give the patch
depth; varied heights, orientations and an irregular edge break up the grid.
The northern edge leaves a clearing around the boulder. This is an art prototype
using expanded animated geometry, with no distance LOD or body interaction yet.

`meadowSpacing`, `meadowHeight`, `meadowWidth`, `meadowTiles` and `meadowBlades`
hold the source parameters. Tile centers assume twelve samples at 0.075 m spacing;
adjust their separation with spacing to keep the field continuous. Wind phase
uses world X/Z so gusts cross tile boundaries. Live `meadowWindSpeed` and
`meadowFlutterSpeed` state cells control cadence; set both to zero to hold their
current phases. `meadowWindPose` adds a shared bend for comparing silhouettes.
Reload before comparisons that require identical initial phases.

For the optimization pass, preserve the patch's coverage, varied blade silhouettes,
buried root pivots, continuous gust phase across tiles and independent tip flutter.
The low `meadow` camera exposes these together. Thin-blade edge aliasing remains
visible in the 800×500 art captures; it is a rendering issue to resolve, not part
of the desired style. Use the native-resolution, warmed measurement recipe below
before drawing performance conclusions from this deliberately expanded prototype.

## Authoring

[moth-courtyard.puck](moth-courtyard.puck) is the canonical source;
[moth-courtyard.world.json](moth-courtyard.world.json) is its compiled runtime
output. Edit the source and regenerate the JSON. Do not decompile over the
source again: that would discard its constants and collection expressions.

```powershell
dotnet run --project src/Puck.Cli -c Release -- compile src/Puck.World/Assets/worlds/moth-courtyard.puck -o src/Puck.World/Assets/worlds/moth-courtyard.world.json --validate
```

The `stations` table supplies the metadata, held pose cells, body placements and
plinth positions. Peer placements expand in descending body-index order because
the allocator assigns them in reverse. `poseChannels` connects station columns
to the inherited drivers, and `limbPoses` holds the existing shape ids, pivots
and swing parameters. `inspectionViews` supplies both the layouts and their
number-key bindings; `courtyardCameras` supplies the additional cameras.
Shared sky stops keep the default lighting and the blue-sky cycle key aligned.
The `floor`, `courtyard-tree`, `courtyard-rock` and `courtyard-grass` prototypes
define the landscape; their placement loops control the grove and grass clusters.

Use the full DSL freely; JSON-to-source round-trip fidelity and parity with the
pre-refactor JSON are not requirements for this world. Check source changes with
`puck fmt`, `puck lint --strict`, and `puck compile --validate`; run the compiled
world to verify its intended behavior.

## Run the courtyard

From the repository root, open it with:

```powershell
dotnet run --project src/Puck.World -c Release -- --world src/Puck.World/Assets/worlds/moth-courtyard.world.json --backend directx --width 1440 --height 900 --state-dir artifacts/moth-courtyard/state
```

In an existing World session, use
`world.load src/Puck.World/Assets/worlds/moth-courtyard.world.json`.
Start a fresh session for the full keyboard/console input vocabulary: the new
`skyToggle` channel must be present when the body command table is constructed.

## Inspection controls

| Key | View or action |
|---|---|
| 1 | All eight, including the separated subject at the origin |
| 2 | Isolated subject, three-quarter inspection |
| 3 | Seven-body group, front and side |
| 4 | Group from the rear, exposing wings and exhaust |
| 5 | Cloud banks |
| 6 | Isolated face close-up |
| 7 | Isolated back close-up |
| 8 | Toggle blue sky/fog/sun and a neutral studio background |
| 9 | Toggle three volumetric cloud banks; initially off |
| 0 | Low view across the dense meadow |

The corresponding console controls work without keyboard focus:

```text
view.override layout ensemble
view.override layout isolated
view.override layout group
view.override layout group-rear
view.override layout clouds
view.override layout meadow
body.press skyToggle 1 0.02 0
world.row.step creations.cloud-bank.document.volumes[0].enabled 1
world.state.cell.set meadowWindSpeed $value 0
world.state.cell.set meadowFlutterSpeed $value 0
world.state.cell.set meadowWindPose $value 1
```

The sky binding uses a tapped activator so a press and release drained together
still reach the rule for one simulation tick. The cloud binding carries the
complete row-step arguments, including its `1` delta, in its text payload.

The clouds contain advected three-dimensional density and are clipped by opaque
geometry. Their ramp and height tint provide simple illumination; cloud shadows
and multiple scattering are not implemented. Sky and cloud switches are independent.
For an explicit sky state, use `world.state.cell.set skyMode $value 0` (neutral)
or `world.state.cell.set skyMode $value 0.5` (blue sky).

## Pose stations

The initial allocation assigns peer rows in reverse order. These are the body
indices for this document's initial population; `body.where` verifies them.

| Body | Station | Position (X, Y, Z) |
|---|---|---|
| 0 | Isolated neutral subject | 0, 0, 0 |
| 1 | Signaling | 11, 0, 8 |
| 2 | Banked flight | 16, 3.2, 6.2 |
| 3 | Rear-facing hover | 12.5, 1.9, 4.5 |
| 4 | Lift-off | 9, 1.15, 4 |
| 5 | Touchdown | 16, 0, 1 |
| 6 | Stride | 13, 0.08, -0.4 |
| 7 | Ready stance | 10, 0, 0 |

The seven peers hold authored positions and driver values. They are live bodies
with independent render transforms, but these are held diagnostic poses, not
simulated jump/landing trajectories. The isolated subject retains the studio's
movement controls; leave it at the origin for repeatable captures. Pose states
such as `stageCrouch` and `airWings` are keyed by body index. Reloading restores
the initial arrangement after experimentation.

## Measure consistently

Use native 1440×900, one named camera, and the same lighting settings for each
comparison. Keep all eight bodies alive even in the isolated camera: that view
tests the cost of off-screen peers, not a one-body scene. Start with clouds off,
warm the scene, then record several completed GPU frames with `world.gpu`.
Record `body.where` and `world.budget` beside them; sixteen submitted volumes
are the eight pairs of jets, and enabling clouds adds three. A body must not
also register a duplicate animated placement.

Measure exact shadows and AO separately, then cloud cost from the same camera.
Diagnostic lighting reductions are not equivalent-quality performance wins.
Use `world.screenshot <path>` for each view and wait for its completion echo.
The target remains 60 FPS at native 1440×900; this scene makes no claim to meet it.

For faster visual inspection, `world.shadows off` and `world.ao off` remove the
two largest measured costs. Restore `world.shadows high` and `world.ao on` for
the full-quality baseline. These switches change the image; record them with
every timing or screenshot.
