# Motion and views

Puck stores camera poses as presentation data, separate from the moving world
and its simulation state. An in-world screen shows another render, including a
screen that contains a render, as an instance of the render graph, which reads
itself at its previous frame rather than creating a render-loop paradox. The
render graph's scheduler renders each view once a frame at most and only while
something shows it, while explicit screen seams distinguish image content from
the SDF surface that displays it.

## Anchors name poses instead of holding references

A camera needs to know where its subject is. The naive way to give it that
is a reference: hand the camera a pointer to the player object, and let it
read `player.Position` every frame. That naive way breaks the moment the
"player" changes identity—a companion despawns and a different one takes
over the name, a creation gets rebuilt mid-edit, a hosted guest's on-screen
avatar isn't a first-class engine object at all. The camera would need to
know about all of that.

Puck's answer is to make **the pose the only thing that crosses the
boundary**. `SdfAnchor` is nothing but a position and an orientation—a
snapshot, not a link back to whatever produced it:

```csharp
public readonly record struct SdfAnchor(Vector3 Position, Quaternion Orientation);
```

A consumer holds an integer anchor id, never the thing that moved, and
re-reads the pose every frame through `ISdfAnchorSource`:

```csharp
bool TryResolveAnchor(int anchorId, out SdfAnchor anchor);
```

`Puck.World.Client`'s `WorldClient` is the main source: an anchor id is an
entity slot, and it resolves to that entity's interpolated render pose.
`FixedAnchorSource` (in `WorldCameraRigCompiler.cs`) always returns one pose,
and `WorldScreenBinder.CameraViews.cs` adds sources for an entity part and a
ranked camera candidate. `SdfAnchorTable`, a name-keyed source, has no users.

The rule this produces is simple and central: **an id whose subject is gone
simply stops resolving.** `TryResolveAnchor` returns `false`, and the
consumer's own fallback—park the camera, skip the frame—is its call. The
camera never needs to understand entities, only poses.

`SdfAnchorKind` classifies *what kind of thing* an anchor id is drawn from —
`World` (nothing; the pose is authored directly and only moves via an
authoring verb), `Body` (a live-animated shape with its own pose stream—an
avatar, a creation), or `Instance` (a placed/stamped occurrence—a dragged
prop). This is pure bookkeeping for a host's own id spaces; an anchor source
itself doesn't care.

**The presentation-only rule.** An anchor is resolved *from* an
already-computed simulation pose, and its only consumers are presentation:
camera rigs, lights, and screen bindings. Nothing reads an anchor back into simulation
state. That one-way flow is what makes anchors safe to keep in ordinary
floats even under a determinism regime that forbids float in simulation
state: by the time a pose becomes an anchor, the simulation has already
finished deciding where things are. The anchor is downstream evidence of a
decision, not part of making one.

## Five camera rigs

A camera rig answers one question every frame: given a subject's anchor,
where is the eye, where is it looking, and at what field of view? The rig also
receives the presentation clocks (`SdfCameraClock`) for camera-only motion;
none of the five rigs below reads them.

```text
(Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, in SdfCameraClock clock);
```

Every rig in the engine is a small, named shape covering one way a camera
has ever needed to relate to a subject in this codebase—not a
general-purpose camera-scripting language, a short vocabulary extracted from
what the earlier hand-rolled camera code was already computing.

| Rig | Shape | When to reach for it |
|---|---|---|
| `OrbitRig` | Yaw/pitch/distance around the subject | A controller-driven or scripted orbit—a debug camera, a creation preview |
| `FollowRig` | Fixed offset in **world** axes | A chase camera for a subject whose own "up" is always world-up (a biped walking on a flat floor) |
| `OrientedFollowRig` | Fixed offset in the **subject's own** axes | A chase camera for a subject whose "up" can point anywhere (a walker on a curved planetoid, where "over the shoulder" must rotate with the walker, not the world) |
| `FirstPersonRig` | Eye at the anchor, looking along its facing | A first-person view |
| `FixedRig` | A pose that ignores the anchor entirely | A static security-camera eye, a backdrop establishing shot |

Two of these are worth dwelling on because the difference between them is a
recurring lesson, not a coincidence. `FollowRig` keeps its offset in world
axes deliberately—"up and back" should read the same regardless of which
way a biped standing on a flat floor is facing. `OrientedFollowRig` rotates
*both* of its offsets by the subject's orientation before adding them—its
motivating case is a walker on a planetoid, where on the far side the
walker's own "up" is the world's "down," and a world-axis chase offset would
frame the shot from beneath the walker's feet. The choice of rig is a
statement about what "up" means for that subject. Get it backwards and the
symptom is a chase camera that quietly flips upside-down the moment a
subject's frame diverges from the world's.

`OrientedFollowRig` also subsumes `FirstPersonRig` at zero pullback: a
depth-free eye offset with a small forward step in the target offset
reproduces the first-person shape exactly. `FirstPersonRig` stays a
separate type anyway because its own framing—an eye height and a forward
focus distance—reads more directly for that one case; reach for
`OrientedFollowRig` the moment you need any pullback at all.

`OrbitRig`'s pure trig—`Offset(yaw, pitch, distance)`—is exposed as a
static method precisely so a caller that only wants the vector, not a full
anchor-driven resolve, doesn't have to duplicate the formula. Every
object-intent camera that has ever existed in this codebase reduces to this
one function.

## Camera matrices share the march's convention

The SDF march needs no matrices: it builds each pixel's ray from the camera
basis. Rasterized geometry does, and `ViewProjection`
(`src/Puck.Abstractions/Cameras`) derives them from the same
`CameraSnapshot` and frustum offset so both agree on every pixel. World space
is right-handed and view space looks down −Z. The projection is reversed-Z
with an infinite far plane: depth is `near / d` for forward distance `d`, 1 on
the near plane and falling toward 0, so a nearer surface always has the
greater depth. The SDF engine's near plane is `SdfWorldEngine.ConeNear`, the
distance where every camera cone starts. Normalized device coordinates put +Y
up, a view's UV origin is its top-left corner, and each sample is a pixel
center with no jitter. `RayParameter` turns a depth back into the distance
the march records, measured along the normalized ray through the sample. The
previous frame's transforms ride beside the current ones; a view without a
usable history carries its own matrices as the previous ones and so reports
no motion. Nothing renders through these matrices yet.

## Views are render-graph instances

A diegetic screen—a booted cabinet's CRT, a security monitor, a creation's
preview easel—needs *something* to show. That something might be a posed
camera looking at the room, a hosted guest machine's framebuffer, or an
entirely separate SDF world rendered offscreen. Each is an instance of the
render graph (`RenderGraphInstance`), and a screen reads the instance it
shows: a guest machine or a producer as a source instance, a camera or
another world as a view instance (`WorldViewInstances`).

- **A camera view** is an external `sdf.world` instance named by its camera's
  registration, rendered by an `SdfEngineNode` of its own
  (`SdfCameraFrameSource`). It films the frame the world renders
  (`SdfEngineNode.HostFrame`)—the same program, transforms, clock and
  levers—from a rig posed against a live anchor. It films an already-lit world
  and contributes no light of its own.
- **A session view** is an `sdf.world` instance too, rendering another world's
  own frame source. A screen showing it shows a **world inside the world**.

**The scheduler decides what renders.** A view renders only while something
shows it: a screen, through the footprint of its declared extent inside the
world's view, or a HUD frame or a probe export, which the display shows
directly. It renders at most once a frame however many screens show it, at the
extent its footprint asks, and at the refresh `world.view-refresh` sets (a
window session on every frame); between refreshes every consumer reads its
latest completed image. A view nothing shows renders nothing and keeps its
last image. The cost of every view is priced in the schedule and
`world.budget` like any instance's, with no fixed view count.

### The self-reference rule

A view filming the world reads every view, itself included, at its previous
frame, and the world reads every view within the frame. **Inside view V's own
render, a screen showing V samples V's previous image**: the engine renders
the frame into another output when one of its own screens samples the current
one, so a view never samples the image it is in the middle of writing. A mirror
facing itself therefore shows the frame before, and two cameras filming each
other's screens each show the other's previous frame, never a same-frame loop.

```text
     world renders   ──> screen showing view A  ──> A's image of this frame
     view A renders  ──> screen showing view A  ──> A's previous image
     view A renders  ──> screen showing view B  ──> B's previous image
```

A hit on a screen showing a view continues through that view's camera into
the world it films (`RenderGraphHitWalk`), up to the graph's nesting depth.
## View transitions move regions and switch content

A `ViewLayout` is a snapshot of which view occupies which
normalized screen region, slot by slot. A `ViewTransition` eases between two
layouts over time—but it eases only the *region*: the *view occupying*
that region is a hard cut at the eased midpoint (progress 0.5), not a
cross-fade.

The reasoning is architectural, not aesthetic: two arbitrary content
sources cannot be cross-faded pixel-for-pixel without real alpha
compositing, which this primitive deliberately does not attempt. A
continuous region with a discontinuous content-swap at the midpoint reads
as an honest camera move—a pullback, a cut—rather than a technically
impossible dissolve between unrelated images. This is the same shape a
fourth-wall reveal wants: a fullscreen guest view (the player "inside" a
booted machine) becomes a camera view of the room, framed on the very
surface that hosts that same guest, and the cut reads as the camera pulling
back rather than a channel changing.

Slots pair by *index*, not by matching view identity—a caller who wants a
particular view to persist across a transition places it at the same slot
index in both layouts. A layout with fewer slots than its counterpart pads
the short side by holding its own view collapsed to the other layout's
target region's center point—a slot appearing grows from nothing; one
disappearing shrinks to nothing.

## Diegetic screens use two separate content seams

This is the distinction that is easiest to blur and most important not to.
A screen surface in the world touches **two entirely separate systems**,
and they answer two different questions:

**A pane** is a render this frame **places over the world**: a
`views.graphs` instance that a layout slot names. It is a render-graph
instance with its own shader, not an SDF view, so it takes none of the SDF
engine's viewports. The root graph's `place` pass draws its finished image
into the slot's rect over the SDF world. This is about **layout**—how many
things this frame renders and where each one's pixels land.

**A screen source** is a program-declared `ScreenSlab` shape's **material**:
its lit face samples a bound image, drawn from the screen's published mapping,
through a CRT glass treatment (bezel, scanlines, vignette, glint, bloom), and separately, that same
bound image's average color is summed into the room as colored light. This
is about **shading**—what a particular surface in the world *looks like*
and what it *contributes to the room's lighting*, independent of anything
about frame layout.

A render-graph instance is the thing that *produces* the image a screen
source samples—the instance and the screen surface are two ends of a wire,
not one object. `SetScreenSource(index, 0)`—a screen reading no instance—unbinds
that wire: the face shades as dark glass, lit faintly by the sun, the look of a
display with nothing behind it.

The conflation to watch for: treating a screen surface as if it needs a
*layout slot* to show something, or treating a *pane* as if it needs a
`ScreenSlab` material to appear. Neither is true. A pane is pure layout; a
screen source is pure shading fed by a wire. A booted cabinet's CRT is a
`ScreenSlab` sampling an instance's image—never a pane of the room's own frame.

---

## Related resources

- [.claude/skills/rendering/SKILL.md](../../../../.claude/skills/rendering/SKILL.md)
  —"Views" and "Composition, anchors, views, and queries" sections; the two
  content seams under "Engine semantics."
- Source: `src/Puck.SdfVm/SdfAnchor.cs`, `src/Puck.SdfVm/Views/SdfCameraRig.cs`,
  `src/Puck.SdfVm/Views/SdfCameraFrameSource.cs`, `src/Puck.SdfVm/Views/ViewTransition.cs`,
  `src/Puck.World.Client/Sources/WorldViewInstances.cs`,
  `src/Puck.World/WorldScreenBinder.{CameraViews,Session,Views}.cs`.
