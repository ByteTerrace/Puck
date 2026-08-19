# Views — authored cameras and seat control

The `views` section owns camera structure. `playerDefaults.seatLook` owns only
portable human input preference. The split is strict: old
`seatLook.minPitch`, `seatLook.maxPitch`, and `seatLook.worldAxes` members are
unmapped and refuse at parse time.

Primary code:

- `Puck.World.Schema/WorldViews.cs` — `WorldViewDefaults`,
  `WorldSeatViewControl`, layouts and slots.
- `Puck.World.Schema/WorldCameraRig.cs` — motion/aim/lens unions.
- `Puck.World.Schema/WorldSeatLook.cs` — portable input preference.
- `Puck.World/Client/WorldSeatViewState.cs` — the one live state per occupied
  seat, including yaw/pitch, live rig cache, and smoothing.
- `Puck.World/WorldSeatViewInput.cs` — stateless pointer adapter.
- `Puck.World/Client/WorldFrameSource.cs`, `WorldAdjacencySceneEmitter.cs`, and
  `WorldContinuum.cs` — local and neighbouring-authority render callers of the
  same seat state and generation-addressed continuum.
- `Puck.World/WorldViewCommandModule.cs` — read-back and composition verbs.

## Document shape

The engine declares no rig of its own: `views` is REQUIRED exactly when the
census implies a body (`population.capacity > 0`), the same derived refusal
`kits` carries, and a seatless document may author none. The standard chase
framing below is AUTHORED, in `src/Puck.World/Assets/worlds/standard.world.json`
— a world inherits it by naming that document as its `basis` (`null.world.json`
does, authoring only its own `layouts` and `seatControl.swapRate` delta), or
states its own:

```json
"views": {
  "seatControl": {
    "yawReference": "World",
    "minPitch": -0.35,
    "maxPitch": 1.2
  },
  "seatRig": {
    "motion": {
      "$type": "orbit",
      "distance": 5.4626,
      "yaw": 0,
      "pitch": 0.4145069,
      "pivotOffset": [0, 0, 0]
    },
    "aim": { "$type": "anchor", "offset": [0, 1, 0], "worldAxes": false },
    "lens": { "fieldOfViewRadians": 0.9599311 },
    "smoothRate": 6
  },
  "layouts": []
},
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
`LT → North` to `player.motion.controls`, and `gamepad.gyro` to
`player.motion.angular`. Each North press toggles the mode; it remains active
after the buttons release. The command is intentionally not gyro-named so a
later orientation/tilt-to-move adapter can share it. `LT → RB → LB` explicitly
fires the same `player.look.swap` action as `LT → LB`; the shorter `LT + RB`
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

## Rigs and layouts

`WorldCameraRig(Motion, Aim, Lens, SmoothRate)` composes:

- motion: `Follow`, `Orbit`, `Static`, or `Track`;
- aim: `Anchor`, `Forward`, or `WorldPoint`;
- lens: vertical FOV radians;
- `smoothRate`: non-negative exponential response; zero disables it.

The interactive `views.seatRig.motion` must be `Orbit`; validation refuses a
non-interactive arm there because accepting right-stick yaw while rendering a
rig that cannot express it would split movement from the visible camera. Use
named `cameras` for `Follow`, `Static`, and `Track` views.

Named `cameras` resolve through authored anchors independently of the seat
state. `views.layouts` maps normalized slots to joined seats or named cameras.
An empty list uses the built-in one-to-four-seat ladder. Layout transition
duration and render scale remain authored on each layout. A layout whose
`seatCount` no joined-seat count can reach (5+) is selectable only through
`view.override layout <name>` — the authoring shape for an override-only view.
Under a camera-only layout, a joined seat the layout binds no seat slot to
falls back to the first camera-bearing slot's region and camera
(`WorldFrameSource`), so the cursor, the radial wheel, and pointer
unprojection ride the view the player is looking at. The composer's active
selection also publishes the `layout` context family every tick, so a world
can flip a seat's binding group with the view (see documents.md, context
rows).

## Pointer, cursor, fly camera

`WorldPointerSink` is the one window observer. `WorldSeatViewInput` drains
motion only for camera steering and asks the active preference whether the
pointer is armed. `WorldCursorFeed` asks that same adapter whether steering is
active, so pointer consumption and cursor visibility cannot disagree. This is
the presentation projection only: the same relative motion, wheel, and button
events independently enter `Puck.Commands` through `InputSources.Mouse` while
absolute cursor position remains observer-only.

`WorldSeatFlyRig` is the seat's fly control application: `player.mode`
activates it (a mode state whose `target` is `"camera"`), seeding the fly
camera from the seat's current chase framing (no pose pop) and resolving it
against the world-authored `views.flyRig` each frame; the play chase state
stays on the seat and resumes when the mode flips back. Named cameras and the
fly rig do not alter the logical movement basis.

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
- `world.row.set views.seatRig <json>` — replace seat framing.
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
