# Motion and views

After this chapter you will understand how Puck poses a camera against a
moving world without ever letting a camera become simulation state; how one
small primitive (the `ViewStack`) lets any screen in the world show any other
render — including a render of a screen showing a render — without a
render-loop paradox; and the one rule that keeps a wall of a hundred
in-world monitors as cheap as four. You'll also learn the two different
things a "screen" can mean in an SDF world, and why confusing them is the
most common mistake a new contributor makes.

## Anchors: naming a pose instead of holding a reference

A camera needs to know where its subject is. The naive way to give it that
is a reference: hand the camera a pointer to the player object, and let it
read `player.Position` every frame. That naive way breaks the moment the
"player" changes identity — a companion despawns and a different one takes
over the name, a creation gets rebuilt mid-edit, a hosted guest's on-screen
avatar isn't a first-class engine object at all. The camera would need to
know about all of that.

Puck's answer is to make **the pose the only thing that crosses the
boundary**. `SdfAnchor` is nothing but a position and an orientation — a
snapshot, not a link back to whatever produced it:

```csharp
public readonly record struct SdfAnchor(Vector3 Position, Quaternion Orientation);
```

A sim-side registry, `SdfAnchorTable`, publishes these by **name**, once per
tick, for every pose something might want to ride:

- `BeginTick()` marks every previously-assigned id as not-live.
- `Publish("player.0", pose)` republishes that tick's pose. The first
  publish under a name allocates a stable id; every later publish — this
  tick or any future one — reuses it.
- A consumer resolves the name to an id once (`TryResolveId`), then re-reads
  the pose every frame through `TryResolveAnchor(id, out anchor)`.

The rule this produces is simple and load-bearing: **a name that stops
publishing simply stops resolving.** It is never reassigned to a new
occupant, and a consumer never notices anything but the pose vanishing (its
own fallback — park the camera, skip the frame — is its call). This is what
lets a camera bind to `"player.0"` at setup and keep working correctly no
matter what happens to the entity behind that name, without the anchor
system ever needing to understand entities, only poses.

`SdfAnchorKind` classifies *what kind of thing* an anchor id is drawn from —
`World` (nothing; the pose is authored directly and only moves via an
authoring verb), `Body` (a live-animated shape with its own pose stream — an
avatar, a creation), or `Instance` (a placed/stamped occurrence — a dragged
prop). This is pure bookkeeping for a host's own id spaces; the anchor table
itself doesn't care.

**The presentation-only rule.** An anchor is published *from* an
already-computed simulation pose — a fixed-point position converted to a
float once, at the moment of publishing — and its only consumers are camera
rigs and view transitions. Nothing reads an anchor back into simulation
state. That one-way flow is what makes anchors safe to keep in ordinary
floats even under a determinism regime that forbids float in simulation
state: by the time a pose becomes an anchor, the simulation has already
finished deciding where things are. The anchor is downstream evidence of a
decision, not part of making one.

## The six camera rigs

A camera rig answers one question every frame: given a subject's anchor
(and, for one rig, a presentation clock), where is the eye, where is it
looking, and at what field of view?

```
(Vector3 Eye, Vector3 Target, float FovRadians) Resolve(in SdfAnchor anchor, float time);
```

Every rig in the engine is a small, named shape covering one way a camera
has ever needed to relate to a subject in this codebase — not a
general-purpose camera-scripting language, a short vocabulary extracted from
what every hand-rolled camera in the demo was already computing.

| Rig | Shape | When to reach for it |
|---|---|---|
| `OrbitRig` | Yaw/pitch/distance around the subject | A controller-driven or scripted orbit — a debug camera, a creation preview |
| `FollowRig` | Fixed offset in **world** axes | A chase camera for a subject whose own "up" is always world-up (a biped walking on a flat floor) |
| `OrientedFollowRig` | Fixed offset in the **subject's own** axes | A chase camera for a subject whose "up" can point anywhere (a walker on a curved planetoid, where "over the shoulder" must rotate with the walker, not the world) |
| `FirstPersonRig` | Eye at the anchor, looking along its facing | A first-person view |
| `FixedRig` | A pose that ignores the anchor entirely | A static security-camera eye, a backdrop establishing shot |
| `DollyRig` | A scripted sweep between two points over time, looking at the anchor | A cinematic establishing move |

Two of these are worth dwelling on because the difference between them is a
recurring lesson, not a coincidence. `FollowRig` keeps its offset in world
axes deliberately — "up and back" should read the same regardless of which
way a biped standing on a flat floor is facing. `OrientedFollowRig` rotates
*both* of its offsets by the subject's orientation before adding them — its
motivating case is a walker on a planetoid, where on the far side the
walker's own "up" is the world's "down," and a world-axis chase offset would
frame the shot from beneath the walker's feet. The choice of rig is a
statement about what "up" means for that subject. Get it backwards and the
symptom is a chase camera that quietly flips upside-down the moment a
subject's frame diverges from the world's.

`OrientedFollowRig` also subsumes `FirstPersonRig` at zero pullback: a
depth-free eye offset with a small forward step in the target offset
reproduces the first-person shape exactly. `FirstPersonRig` stays a
separate type anyway because its own framing — an eye height and a forward
focus distance — reads more directly for that one case; reach for
`OrientedFollowRig` the moment you need any pullback at all.

`OrbitRig`'s pure trig — `Offset(yaw, pitch, distance)` — is exposed as a
static method precisely so a caller that only wants the vector, not a full
anchor-driven resolve, doesn't have to duplicate the formula. Every
object-intent camera that has ever existed in this codebase reduces to this
one function.

## ViewStack: the hypervisor idea

A diegetic screen — a booted cabinet's CRT, a security monitor, a creation's
preview easel — needs *something* to show. That something might be a posed
camera looking at the room, a hosted guest machine's raw framebuffer, or an
entirely separate SDF world rendered offscreen. `ViewStack` is the one
vocabulary all three speak, and the reason it exists as a single primitive
rather than three bespoke pools is the idea worth understanding: **a view is
content registered by name, and anything that resolves to an image handle
qualifies** — the stack does not care whether that handle came from a camera,
a guest, or another whole world.

```csharp
public interface IViewContent {
    nint Resolve(in ViewRenderContext context);  // 0 = no signal
    Vector3 RoomGlow { get; }                     // light this content emits into the room
    bool IsBudgeted => true;                      // does resolving cost a real render pass?
}
```

Three shapes implement it today:

- **`SdfCameraView`** — a tiny offscreen `SdfWorldEngine`, posed each resolve
  by a rig against a live anchor. This is a camera: it films an
  already-lit world and contributes no light of its own.
- **`GuestSurfaceView`** — wraps a delegate that returns *someone else's*
  already-current image handle. It costs nothing to resolve (no render
  pass — whatever owns the producer already keeps it current), so it is
  **unbudgeted**: it resolves every frame regardless of the round-robin
  below.
- **`NestedWorldView`** — a fully independent SDF world, its own frame
  source and its own emitters, rendered offscreen exactly like a camera view
  renders the host world. This is the hypervisor proof: a screen wired to
  this view shows a **world inside the world**, and if that inner world
  itself wires a screen to yet another nested view, the chain composes.

**Registration is cheap; refreshing is not — so they are budgeted
separately.** Up to `MaxRegisteredViews` (64) views may be live at once;
holding a registration costs only a small amount of state. But only
`RefreshBudget` (4) of the *budgeted* views actually pay a real render pass
on any one produced frame. Views beyond the budget share it round-robin: an
unrefreshed view keeps showing its last resolved image until the cursor
reaches it again. This is why a wall of a hundred security monitors costs
the same per-frame render budget as four — the wall is diegetically honest
(most monitors show a slightly stale frame, exactly like a real bank of
CRTs fed by a shared switcher) without ever costing a hundred render passes.

**Withdrawal is the registrant's job, not the stack's.** A registered
budgeted view keeps rendering every round-robin turn for as long as it stays
registered, whether or not any screen currently samples it — the stack
deliberately has no "is anyone watching" gate, because it cannot see every
legitimate reason to keep a view alive (a view transition might be sampling
it by name without ever wiring it to a screen). A registrant that wants a
view to stop costing a render pass releases it itself, the moment its own
notion of "wanted" turns false.

### The self-reference rule

The one rule that keeps this from becoming an infinite hall of mirrors:
**inside view V's own render, any screen surface currently wired to V binds
to nothing (the flat fallback material).** A view never samples the image
it is itself in the middle of writing — doing so would compound the
previous frame's picture into itself, frame after frame, drifting toward a
runaway feedback loop. Every *other* screen, including one wired to a
*different* view, resolves normally. That asymmetry is what makes
one-frame-lag TV-in-TV legal and useful: a view showing a screen that shows
a different view composes fine; a view showing a screen that shows itself
does not, and is the one case the stack actively prevents rather than
merely discouraging.

```
     view A renders  ──> screen wired to view A  ──> BLOCKED (binds 0, self-reference)
     view A renders  ──> screen wired to view B  ──> fine (B's last resolved frame, 1-frame lag)
     view B renders  ──> screen wired to view A  ──> fine
```

## View transitions: continuous region, discontinuous content

A `ViewLayout` is a snapshot of which registered view occupies which
normalized screen region, slot by slot. A `ViewTransition` eases between two
layouts over time — but it eases only the *region*: the *view occupying*
that region is a hard cut at the eased midpoint (progress 0.5), not a
cross-fade.

The reasoning is architectural, not aesthetic: two arbitrary content
sources cannot be cross-faded pixel-for-pixel without real alpha
compositing, which this primitive deliberately does not attempt. A
continuous region with a discontinuous content-swap at the midpoint reads
as an honest camera move — a pullback, a cut — rather than a technically
impossible dissolve between unrelated images. This is the same shape a
fourth-wall reveal wants: a fullscreen guest view (the player "inside" a
booted machine) becomes a camera view of the room, framed on the very
surface that hosts that same guest, and the cut reads as the camera pulling
back rather than a channel changing.

Slots pair by *index*, not by matching view identity — a caller who wants a
particular view to persist across a transition places it at the same slot
index in both layouts. A layout with fewer slots than its counterpart pads
the short side by holding its own view collapsed to the other layout's
target region's center point — a slot appearing grows from nothing; one
disappearing shrinks to nothing.

## Diegetic screens: the two content seams, kept apart

This is the distinction that is easiest to blur and most important not to.
A screen surface in the world touches **two entirely separate systems**,
and they answer two different questions:

**A child** is a render this frame **composites in**: it occupies one of
the frame's viewport slots, gets skipped by the cone-march beam and the
first render stage the way any other viewport does, and the compositor
copies its finished surface into place. This is about **layout** — how many
things this frame renders and where each one's pixels land.

**A screen source** is a program-declared `ScreenSlab` shape's **material**:
its lit face samples a bound image through a CRT glass treatment (barrel
curve, bezel, scanlines, vignette, glint, bloom), and separately, that same
bound image's average color is summed into the room as colored light. This
is about **shading** — what a particular surface in the world *looks like*
and what it *contributes to the room's lighting*, independent of anything
about frame layout.

A `ViewStack` entry is the thing that *produces* the image handle a screen
source samples — the view and the screen surface are two ends of a wire,
not one object. `SetScreenSource(index, 0)` — a provider that returns no
handle — unbinds that wire: the face falls back to its flat/procedural "no
signal" material, never simply goes black. A screen reading solid black is
a *different* bug (a dead image or a zeroed room-light entry), never the
correct look for "nothing is wired here."

The conflation to watch for: treating a screen surface as if it needs a
*viewport slot* to show something, or treating a *child* render as if it
needs a `ScreenSlab` material to appear. Neither is true. A child is pure
layout; a screen source is pure shading fed by a wire. A booted cabinet's
CRT is a `ScreenSlab` sampling a view's handle — never a viewport child of
the room's own frame.

---

## Related resources

- [.agents/skills/sdf-world/SKILL.md](../../.agents/skills/sdf-world/SKILL.md)
  — "Views" and "Composition, anchors, views, and queries" sections; the two
  content seams under "Engine semantics."
- Source: `src/Puck.SdfVm/SdfAnchor.cs`, `src/Puck.SdfVm/Views/SdfCameraRig.cs`,
  `src/Puck.SdfVm/Views/ViewStack.cs`, `src/Puck.SdfVm/Views/ViewTransition.cs`,
  `src/Puck.SdfVm/Views/{SdfCameraView,GuestSurfaceView,NestedWorldView}.cs`.
