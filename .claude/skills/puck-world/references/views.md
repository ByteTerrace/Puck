# Views — authored cameras and seat control

The `views` section owns camera structure. `playerDefaults.seatLook` owns only
portable human input preference. The split is strict: old
`seatLook.minPitch`, `seatLook.maxPitch`, and `seatLook.worldAxes` members are
unmapped and refuse at parse time.

Primary code:

- `Puck.World.Data/WorldViews.cs` — `WorldViewDefaults`,
  `WorldSeatViewControl`, layouts and slots.
- `Puck.World.Data/WorldCameraRig.cs` — motion/aim/lens unions.
- `Puck.World.Data/WorldSeatLook.cs` — portable input preference.
- `Puck.World/Client/WorldSeatViewState.cs` — the one live state per occupied
  seat, including yaw/pitch, live rig cache, and smoothing.
- `Puck.World/WorldSeatViewInput.cs` — stateless pointer adapter.
- `Puck.World/Client/WorldFrameSource.cs`, `WorldAdjacencySceneEmitter.cs`, and
  `WorldContinuum.cs` — local and neighbouring-authority render callers of the
  same seat state and generation-addressed continuum.
- `Puck.World/WorldViewCommandModule.cs` — read-back and composition verbs.

## Document shape

Every world requires:

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
    "arming": "RightButton",
    "stickLookRate": 2.6
  }
}
```

`seatControl.yawReference` is `World` for standard camera-relative movement or
`Body` for an explicitly body-relative camera. Pitch values are radians,
finite, ordered, and within `[-pi/2, pi/2]`.

`seatLook` carries pointer radians-per-pixel, right-stick radians-per-second,
inversion, and pointer arming (`None`, `Always`, `LeftButton`, `RightButton`,
`MiddleButton`). A joined identity's preference travels; otherwise the routed
world's default applies. Neither can override the routed world's
`seatControl`.

## Runtime ownership and order

`SeatController` constructs its `WorldSeatViewState`, so leaving a seat drops
the state and a later occupant cannot inherit it. There is no process-global
orbit table, binding-side feel cache, or renderer-local orbit/smoothing copy.

For each fixed tick:

1. Routed look input is integrated once into the seat state. Device convention
   is `+X = look right`, `+Y = look up`; inversion is applied once there.
2. `WorldClient` rotates left-stick movement through that logical yaw when the
   selected kit authors `moveFrame: World`.
3. The ordinary intent roles cross the wire; camera state never does.
4. Local or traveler rendering resolves the authored rig through the same
   seat state. Visual smoothing and collision clearance never feed movement.

The right stick never writes `Turn`. Explicit bindings may still write `Turn`,
and vehicle kits may interpret their left-stick roles according to their own
motion program.

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
duration and render scale remain authored on each layout.

## Pointer, cursor, editor

`WorldPointerSink` is the one window observer. `WorldSeatViewInput` drains
motion only for camera steering and asks the active preference whether the
pointer is armed. `WorldCursorFeed` asks that same adapter whether steering is
active, so pointer consumption and cursor visibility cannot disagree.

`WorldEditorSession` remains a mode coordinator and supplies an explicit
editor rig while editing; the play chase state stays on the seat and resumes
after exit. Named cameras and editor rigs do not alter the logical movement
basis.

## Verbs

- `world.view.camera [player]` — reads the routed structure, portable
  preference, and the exact live yaw/pitch state used by movement/rendering.
- `world.view.state` — reads active layout, selection reason, transition, and
  slot occupants.
- `world.view.pointer` — reads pointer position, viewport mapping, visibility,
  arming reason, buttons, hover, and system-release generation.
- `view.override camera|layout <name|auto>` — live composition override.
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
payloads remain radians. A discriminating control must move the right stick,
observe yaw/pitch change, then move the left stick and prove the body advances
along the new logical camera forward while `Turn` remains zero. Repeat across
a traveler crossing to prove the same seat state and destination structure are
used. Refusal controls: omit `views.seatControl`, submit the old mixed
`seatLook` members, invert the pitch interval, or name an unknown yaw reference.
