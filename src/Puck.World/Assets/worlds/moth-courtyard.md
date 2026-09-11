# Moth courtyard

[moth-courtyard.world.json](moth-courtyard.world.json) imports
[the Moth studio](moth.md) through `basis`. All eight bodies share its creation;
the courtyard supplies pose drivers, placement, cameras and environment.
This is a diagnostic scene for visual inspection and GPU profiling.

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

The corresponding console controls work without keyboard focus:

```text
view.override layout ensemble
view.override layout isolated
view.override layout group
view.override layout group-rear
view.override layout clouds
body.press skyToggle 1 0.02 0
world.row.step creations.cloud-bank.document.volumes[0].enabled 1
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
