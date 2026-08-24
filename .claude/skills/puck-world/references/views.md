# Views — authored cameras and seat control

The `views` section owns camera structure. `playerDefaults.seatLook` owns only
portable human input preference. The split is strict: old
`seatLook.minPitch`, `seatLook.maxPitch`, and `seatLook.worldAxes` members are
unmapped and refuse at parse time.

Primary code:

- `Puck.World.Schema/WorldViews.cs` — `WorldViewDefaults`,
  `WorldSeatViewControl`, layouts and slots.
- `Puck.World.Schema/WorldCameraProgram.cs` — the authored op vocabulary and
  its subject union. Authoring only: it parses and validates, and knows nothing
  about how a frame resolves.
- `Puck.SdfVm/Views/SdfCameraProgram.cs` — the compiled IR, the per-frame
  evaluator, the `ISdfCameraRig` adapter, and `SdfCameraBoomFollower` (the
  second-order boom ease, over `Puck.SdfVm/Views/SecondOrderFollower.cs`'s
  float twin). Parses no document and references no `Puck.World*` project.
- `Puck.World.Client/WorldCameraRigCompiler.cs` — the translation: authored ops
  to IR, authored subjects and `state.<row>[.<key>]` bindings to per-frame
  slots this rig refills from the live document.
- `Puck.World.Schema/WorldSeatCameraFeel.cs` — portable input preference.
- `Puck.World.Client/WorldSeatViewState.cs` — the one live state per occupied
  seat, including yaw/pitch, the cached compiled rig, and the boom follower.
- `Puck.World/WorldSeatViewInput.cs` — stateless pointer adapter.
- `Puck.World.Client/WorldFramePresenter.cs`, `WorldAdjacencySceneEmitter.cs`, and
  `WorldContinuum.cs` — local and neighbouring-authority render callers of the
  same seat state and generation-addressed continuum.
- `Puck.World/WorldViewCommandModule.cs` — read-back and composition verbs.

## Document shape

The engine declares no rig of its own: `views` is REQUIRED exactly when the
census implies a body (`population.capacity > 0`), the same derived refusal
`kits` carries, and a seatless document may author none. The standard chase
framing below is AUTHORED, in `src/Puck.World/Assets/worlds/standard.world.json`
— a world inherits it by naming that document as its `basis` (`null.world.json`
does, authoring only its own `layouts` and other prototype-specific sections), or
states its own:

```json
"views": {
  "seatControl": {
    "yawReference": "World",
    "minPitch": -0.35,
    "maxPitch": 1.2
  },
  "seatRig": {
    "name": "seatChase",
    "version": "puck.camera.v1",
    "operations": [
      { "$type": "orbit", "distance": 5.4626001, "yaw": "state.look.behind", "pitch": 0.4145069, "pivotOffset": [0, 0, 0] },
      { "$type": "lookAt", "subject": { "$type": "reference" }, "targetOffset": [0, 1, 0], "worldAxes": false },
      { "$type": "fov", "fieldOfViewRadians": 0.9599311 },
      { "$type": "dynamics", "row": "chase" }
    ]
  },
  "layouts": []
},
"dynamics": [
  { "name": "chase", "f": 0.9549, "zeta": 1, "r": 1 }
],
"playerDefaults": {
  "seatLook": {
    "yawSensitivity": 0.001,
    "pitchSensitivity": 0.001,
    "invertYaw": false,
    "invertPitch": false,
    "stickLookRate": 2.6,
    "gyro": {
      "scale": 1.0,
      "deadZone": [0.02, 0.02, 0.02],
      "invertX": false,
      "invertY": false,
      "invertZ": false,
      "yaw": [0, -1, -1],
      "pitch": [1, 0, 0]
    }
  }
}
```

What arms a pointer drag is a binding, not a feel: `player.orbit` (held —
pointer motion orbits the camera) and `player.steer` (held — orbits AND the body
faces where the camera looks) are bound on both edges like any held command
(a press row plus an `activateOn: Completed` row on the same source). The engine
default binds `mouse.button2 → player.orbit`. `player.steer` writes the camera's
facing into channels claiming the `FaceX`/`FaceY`/`FaceZ` roles and the sim's
facing snap turns the body, so binding it needs those three channels declared and
`seatControl.yawReference: World` (the validator refuses otherwise).

`seatControl.yawReference` is `World` for standard camera-relative movement or
`Body` for an explicitly body-relative camera. Pitch values are radians,
finite, ordered, and within `[-pi/2, pi/2]`.

`seatLook` carries pointer radians-per-pixel, right-stick radians-per-second,
gyro projection, and inversion. `gyro.deadZone` and `invertX/Y/Z` act on each
physical axis independently; the dot products against the full 3D `yaw` and
`pitch` weight vectors then produce semantic look-right/look-up rates. Thus all
three axes can participate, be combined, or be remapped without code changes.
A joined identity's preference travels; otherwise the routed world's default
applies. Neither can override the routed world's `seatControl`.

Motion input is a separate, generic toggled mode. The standard profile binds
`LT → North` to `body.motion.controls`, and `gamepad.gyro` to
`body.motion.angular`. Each North press toggles the mode; it remains active
after the buttons release. The command is intentionally not gyro-named so a
later orientation/tilt-to-move adapter can share it. `LT → RB → LB` explicitly
submits the same `body.state.cell.toggle look behind 0 3.14159265` line as
`LT → LB`, toggling the `state.look.behind` yaw bound above; the shorter `LT + RB`
chord holds `player.look.free`: right-stick
yaw/pitch continues to orbit the camera, but yaw does not write body heading
and left-stick movement resolves against authoritative character heading until
either button releases. The hold also suppresses automatic camera follow.
Keyboard `LT + Left Ctrl` retains held recenter.

Binding contexts can derive a complete control group from gameplay state. A
family named `state:<row>` reads the routed world's declared state row after a
delivered revision: scalar rows publish one value to every seat, while keyed
rows read the controlled body's entity-index cell. Numeric states use their
canonical author-facing spelling (`0`, `1`, `12.5`, `true`, `false`); text uses
its exact value. Continuously advancing rows are refused as control contexts,
so a mapping changes only through an explicit, journaled state write. World
rules can therefore select controls with ordinary `setState`/`addState`
effects, and `player.bindings` reports the resulting family/state/group winner.
If a portable profile reaches a world that does not declare its referenced state
row, that family contributes no match and normal context precedence continues.
Changing the winning group is a complete input boundary: held commands, toggles,
chord arms, and page latches from the previous group are cleared before the new
group can accept input.

## Runtime ownership and order

`SeatController` constructs its `WorldSeatViewState`, so leaving a seat drops
the state and a later occupant cannot inherit it. There is no process-global
orbit table, binding-side feel cache, or renderer-local orbit/smoothing copy.

For each fixed tick:

1. Routed stick look and toggled gyro angular velocity are combined into one
   semantic look-rate sample and integrated once into the seat state. Gyro
   dead-zone, physical-axis inversion, and 3D projection happen before the
   existing final semantic yaw/pitch inversion.
2. `WorldClient` rotates left-stick movement through that logical yaw when the
   selected kit authors `moveFrame: World`.
3. Standard `player.move.strafe` preserves heading, and the kit disables
   movement-facing attitude snap, so lateral left-stick input is a true strafe.
   It resolves against live look yaw every tick, so forward travel turns with
   the right stick; `player.move` remains the latched movement-facing alternative.
4. `player.look.steer` writes camera yaw to `FaceX`/`FaceZ` after movement
   composition, turning an upright body while vertical input remains camera
   pitch; `player.look` remains camera-only free orbit.
5. The ordinary intent roles cross the wire; camera state never does.
6. Local or traveler rendering resolves the authored rig through the same
   seat state. Visual smoothing and collision clearance never feed movement.

The standard right stick binds `player.look.steer`: horizontal input writes
`FaceX`/`FaceZ`, vertical input only changes camera pitch, and neither writes
`Turn`. Authors may bind `player.look` for free orbit, explicit bindings may
still write `Turn`, and vehicle kits may interpret their left-stick roles
according to their own motion program. The standard left-stick press toggles
the `run` channel; West and Left Shift remain ordinary hold-to-run sources.

## Camera programs and layouts

A camera rig is an authored PROGRAM — `{ name, version, operations }`, an
ordered op list, the same shape `bodyMotionPrograms` uses for sim-side movement.
There is no motion/aim/lens kind union: a new framing is a different op list,
never a new engine type. `version` is `puck.camera.v1`; the op-count ceiling is
`WorldCameraProgram.MaxOperations`.

| op | does |
|---|---|
| `anchor` | sets the current SUBJECT and re-seeds the eye at it. At most one, and it must lead. Subject is `reference` (the pose the caller hands in), `placement` (a stamped placement transform, position only), or `worldPoint`. |
| `offset` | places the eye at `value` from the subject — in the subject's own axes unless `worldAxes`. `spreadPullback` widens it by the group spread (only meaningful when the caller's reference is a `group` anchor). |
| `lookAt` | aims. `subject: null` looks along the current subject's forward at `focusDistance`; a subject aims at its pose plus `targetOffset`. |
| `orbit` | places the eye by orbiting the subject at `distance`/`yaw`/`pitch` about `pivotOffset`. At most one. On `views.seatRig` the seat's live look adds to yaw/pitch; everywhere else the authored angles render unchanged. |
| `clampPitch` | bounds the pitch a later `orbit` resolves with, live delta included. At most one, and it must precede the orbit. |
| `fov` | the rendered vertical FOV, radians. Every program needs one (or a `blend` that reaches ones that do). Bindable: a literal, or `state.<row>[.<key>]`. |
| `dynamics` | names a `dynamics` row (see documents.md); the resolver REPORTS the response, the caller applies it as a second-order boom ease (`SdfCameraBoomFollower`). No op is no ease — the boom passes through untouched. At most one. |
| `blend` | lerps two other programs by NAME (eye, target, fov, and dynamics — component-wise when both sides are live, otherwise whichever side is live) at `weight`, itself bindable. At most one. |

The blend namespace is the whole document's program table: every
`cameras[].rig`, plus `views.seatRig` and `views.cameraRig`. A dangling name, a
reference cycle (carrying its trail), and a program name declared twice in that
namespace are all refused by name.

`views.seatRig` must contain an `orbit` op: `seatControl` declares a live
yaw/pitch band, and only an orbit can express it. `views.cameraRig` — the
first-person framing a camera control application resolves through — must author
neither `orbit` nor `offset`, because it sits exactly at the possessed camera
body's own pose.

Named `cameras` resolve through authored anchors independently of the seat
state. `views.layouts` maps normalized slots to joined seats or named cameras.
An empty list uses the built-in one-to-four-seat ladder. Layout transition
duration and render scale remain authored on each layout. A layout whose
`seatCount` no joined-seat count can reach (5+) is selectable only through
`view.override layout <name>` — the authoring shape for an override-only view.
Under a camera-only layout, a joined seat the layout binds no seat slot to
falls back to the first camera-bearing slot's region and camera
(`WorldFramePresenter`), so the cursor, the radial wheel, and pointer
unprojection ride the view the player is looking at. The composer's active
selection also publishes the `layout` context family every tick, so a world
can flip a seat's binding group with the view (see documents.md, context
rows).

## Pointer, cursor, Free Cam

`WorldPointerSink` is the one window observer. `WorldSeatViewInput` drains
motion only for camera steering and asks the active preference whether the
pointer is armed. `WorldCursorFeed` asks that same adapter whether steering is
active, so pointer consumption and cursor visibility cannot disagree. This is
the presentation projection only: the same relative motion, wheel, and button
events independently enter `Puck.Commands` through `InputSources.Mouse` while
absolute cursor position remains observer-only.

Free Cam is a POSSESSION, not a second camera integrator. A `seatModes` state
whose `target` is `"camera"` makes the seat possess its own authored
`camera-seat-<0-based slot>` inhabited placement through the ordinary Engage
door: the seat's own body intent diverts to Idle, and the camera body's pose
becomes what the seat perceives, sees, and hears through. Its view resolves
through `views.cameraRig` — the same compiler and evaluator every other program
uses — and leaving the state disengages, restoring the seat's own body. A
document authoring a camera-targeting state without a `views.cameraRig` or
without an inhabited `camera-seat-` placement is refused by name.

`player.camera [seat]` is the bindable no-token toggle a wheel sector or a pad
chord fires: it resolves the seat's own camera-targeting family/state from the
routed document and flips between it and the family's default, running the same
authority check and Engage/Disengage path `player.mode` does. Named cameras and
Free Cam do not alter the logical movement basis.

## Verbs

- `world.view.camera [player]` — reads the routed structure, portable
  preference, held free-look state, motion-control gate/sample, and the exact
  live yaw/pitch state used by movement/rendering.
- `world.view.state` — reads active layout, selection reason, transition, and
  slot occupants.
- `world.view.pointer` — reads pointer position, viewport mapping, visibility,
  arming reason, buttons, hover, and system-release generation.
- `view.override camera|layout <name|auto>` — live composition override. It is
  bindable: a bound dispatch (wheel sector / chord row, no tokens) selects the
  LAYOUT override by its constant Axis1D value — 0 or less clears to auto, n
  selects the nth authored `views.layouts` row (document order, 1-based).
- `player.camera [seat]` — toggles Free Cam (bindable, no tokens).
- `world.row.set views.seatRig <json>` — replace seat framing (a whole camera
  program).
- `world.row.set views.seatControl <json>` — replace yaw reference/pitch band.
- `world.row.set playerDefaults.seatLook <json>` — replace world-floor input
  preference.
- `world.row.set views.layouts <json>` / `world.row.remove views.layouts <name>`
  — mutate layouts.

All mutations run through the ordinary authority, tick-boundary, validation,
and replay paths. Keyless rows cannot be removed.

## Verification

Use a writable state directory when booting locally:

```text
world.view.camera 1
world.view.state
world.view.pointer
```

The camera read-back reports pitch limits and live angles in degrees; authored
payloads remain radians. A discriminating control must move right-stick X and
observe camera yaw plus upright body facing change while `Turn` remains zero;
right-stick Y must change pitch without tilting the body. Then move left-stick X
and prove lateral translation without a facing change; while holding left-stick
forward, move right-stick X and prove the trajectory turns with heading. Repeat across
a traveler crossing to prove the same seat state and destination structure are
used. Refusal controls: omit `views.seatControl`, submit the old mixed
`seatLook` members, invert the pitch interval, or name an unknown yaw reference.

For a camera program: author an unknown op `$type`, put `anchor` anywhere but
first, put `clampPitch` after its `orbit`, omit `fov`, name an undeclared
program from a `blend`, or point two programs' blends at each other. Each is
refused by name at load, the cycle carrying its trail. `world.view.camera`
reports `cameraApplication=true` while a seat is in Free Cam.
