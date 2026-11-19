# The withdrawal chokepoint

A screen or a portal shows another world through a session that world admitted,
and the world decides what that session may see. When the session's access
narrows or ends, the engine must stop showing what it drew before. This memo
checks an adversarial review's nine findings against the code, states the
property as one invariant, and designs the one structural door that holds it,
in place of the per-path guards the review kept finding ways around. It is a
design for review. Nothing here is built.

## Summary

- **The property.** No picture, and nothing derived from one, that was made
  from what a viewer was delivered under wider access reaches that viewer, or
  whoever views in its place, after the access narrows or ends.
- **What the review got right.** Of nine findings, four hold as stated (1, 2,
  5 and 9), three hold in part (3, 6 and 7), one is true of the code with no
  reachable case shown (8), and one cannot happen on the current tree (4). The
  worst is the only one that shows content that was never delivered: a session
  with a restricted row is classed as seeing everything and is drawn from the
  authority's whole replica (finding 1).
- **Why fixing each one does not converge.** Today three separate mechanisms
  each infer a narrowing: the viewer's side asks the authority's live policy
  from the frame thread, the screen binder compares two revision counters to
  guess what a mirror holds, and the render graph follows the read edges of
  whichever instances still exist. Every finding is one of those inferences
  missing a case.
- **The door.** A narrowing is decided once, where the stream is written, and
  travels with the content as a value. Everything made from the content is
  owned by that value or labelled with it, and the render graph's one existing
  propagation path (the capture taint) carries the label to every output. A
  reader is handed an output only while every label on it is still current.
  Nothing on the viewer's side asks a policy question again.
- **What it costs.** A narrowing rebuilds the narrowed view's residency and
  restarts the temporal history of every view that showed it. A narrowing is a
  policy event (a grant, a tier, an audience), not gameplay, so this is rare.
- **One call made here that the owner may want to overrule.** A value change
  under unchanged policies is not a narrowing: a card turned face down, or a
  body that leaves an interest radius, re-renders the view as any scene change
  does and withdraws nothing ([the reasoning](#what-counts-as-a-narrowing)).

## The pipeline this is about

Four stages carry a world to a viewer's picture, and each has an owner today.

1. **The authority's stream.** `WorldSessionSink` delivers a session what its
   `WorldRecipientView` discloses and records what it delivered in a
   `WorldDeliveryCursor`.
2. **The viewer's mirror.** `WorldSessionMirror` holds the delivered document,
   bodies and fields.
3. **The viewer's scene.** `WorldScreenBinder` makes a frame source, an emitter
   and an `SdfWorldResidency` over the mirror, or joins the destination's own
   scene when the session is judged to see everything
   (`WorldInstanceHost.TryWindowEndpoint`).
4. **The render graph.** `RenderGraphRuntime` renders the view as an instance,
   and other instances read its output: the world view that shows it on a
   screen, the display root, an environment producer, a capture, an export.

The withdrawn lane added a guard at stages 3 and 4: `WorldViewerAccess` (an
access level and an epoch), `WorldViewGraphHost.BindAccess` calling
`IRenderGraphInstances.Withdraw`, and `WorldScreenBinder.ContentAccess`
releasing a residency that holds wider content.

## What the review found, checked against the code

Each finding was reopened at its file and symbol on the current integration
tip. A fenced quote below was pasted from the file it cites. "Reachable" means
a sequence the current tree can run; where a step was reasoned and not read,
the text says so. The ranking orders the findings that hold by what a viewer
can be shown.

| Rank | Finding | Verdict |
|---|---|---|
| 1 | A session with restricted rows is drawn from the whole replica (finding 1) | Confirmed. Shows content never delivered |
| 2 | A view that retires skips the withdrawal (finding 2) | Confirmed |
| 3 | A session replaced at the same level keeps the previous session's residency (finding 3, second sequence) | Confirmed in the code; what it then shows is partly reasoned |
| 4 | A node's own history survives a withdrawal (finding 5) | Confirmed |
| 5 | Retained copies: an infinity layer's source, an export ring, a comparison hold (finding 9, parts 1 to 3) | Confirmed |
| 6 | An environment producer that never forgets (finding 7) | Partly true. The screen light path re-derives; the probes never held it |
| 7 | A withdrawn root leaves the old frame on the display (finding 6) | Partly true. Only when the root cannot render that frame |
| 8 | Combining epochs by their maximum (finding 8) | True of the code; no reachable case shown |
| 9 | The mirror reused across sessions (finding 3, first sequence) | Partly refuted. The body table is replaced; the reuse is real |
| — | The delivery and settle race (finding 4) | Refuted on this tree. Latent |
| — | Baked meshes stay in the shared library and store (finding 9, part 4) | True, and outside the guarantee |

### A session with restricted rows is drawn from the whole replica

Confirmed. The test that lets a window join its destination's own scene reads
the tier and the body policy and nothing else:

`src/Puck.World.Server/WorldSessionObservation.cs:55-62`

```csharp
    public bool DisclosesEverything => (
        CanObserve &&
        (Tier == WorldDisclosureTier.Replica) &&
        new WorldSinkDisclosure(
            ObserverBodyIndex: -1,
            Policy: m_server.Definition.Population.ObserverDisclosure
        ).IsFull
    );
```

The same session's own stream is filtered by more than that. Its document comes
from the cursor's disclosed images, and each snapshot's fields, solid sizes and
inputs are filtered for the recipient:

`src/Puck.World.Server/WorldSessionSink.cs:343-345`

```csharp
                FieldCells = m_rows.Advance(m_view, m_cursor, redacted.FieldCells, (Tier == WorldDisclosureTier.Replica)),
                SolidScales = m_scales.Filter(m_view, redacted.SolidScales),
                Inputs = WorldInputPublication.ForOwner(redacted.Inputs, observation.Session, ref m_inputDisclosure),
```

The scene the window joins is fed by the unfiltered attach
(`src/Puck.World.Server/WorldDocument.Delivery.cs:57-61` passes
`WorldSinkDisclosure.Full`). So a replica-tier session in a world with a
restricted row, cell or field region is shown placements, fields and sizes its
own stream withholds. A later loss of an audience changes none of the three
things the test reads, so it is never noticed either.

### A view that retires skips the withdrawal

Confirmed. The frame reconciles the instance set before it binds access:

`src/Puck.World.Client/WorldViewGraphHost.cs:642-649`

```csharp
        Reconcile(
            sharpens: sharpens,
            tonemap: tonemap,
            views: views
        );
        if (m_runtime is { } runtime) {
            BindAccess(runtime: runtime);
        }
```

`Reconcile` reconfigures the runtime in the same call when the screens' views
changed (`src/Puck.World.Client/WorldViewGraphHost.cs:1369-1374`). A session
view that ended is by then absent from the set, and `BindAccess` only forgets
its epoch:

`src/Puck.World.Client/WorldViewGraphHost.cs:666-668`

```csharp
            foreach (var name in m_accessEpochs.Keys.ToArray()) {
                if (set.IndexOf(name: name) < 0) { _ = m_accessEpochs.Remove(key: name); }
            }
```

The reconfiguration keeps every surviving consumer's own outputs as they are
(`src/Puck.Shaders/Graph/RenderGraphRuntime.Standing.cs:228-230` returns an
output that stands for nothing unchanged), and it holds the retired view's
image for a consumer whose replacement graph is still building
(`RenderGraphRuntime.PlanHolds`,
`src/Puck.Shaders/Graph/RenderGraphRuntime.Reconfigure.cs:585-602`). No
package is told to forget. A world view that showed the session on a screen
keeps that output and its temporal history, and a paused or rate-held consumer
keeps showing it.

### A session replaced at the same level keeps the previous residency

Confirmed in the code. The epoch moves when the observation object changes, but
the residency's guard is not told about the observation at all:

`src/Puck.World/WorldScreenBinder.Availability.cs:119`

```csharp
            return ((access != WorldSessionAccess.None) && (access < Source));
```

A second session at the same level compares equal, so the frame source, the
residency and the jobs they started stay. What the second session is then shown
depends on the emitter composing again from the mirror's new document, which
the new session's first delivery triggers. That last step is reasoned from the
revision counters, not traced, and anything in the residency that is not keyed
by those counters is the exposure.

### A node's own history survives a withdrawal

Confirmed. A withdrawal unpublishes and stops there:

`src/Puck.Shaders/ShaderPipelineRenderNode.cs:1947-1954`

```csharp
    public void WithdrawPublication() {
        ObjectDisposedException.ThrowIf(
            condition: m_disposed,
            instance: this
        );
        Unpublish();
        m_publicationLost = true;
    }
```

Only `ShaderPipelineRenderNode.Reset` marks the node's storage uninitialized
(`DiscardInstances`, `src/Puck.Shaders/ShaderPipelineRenderNode.Tracker.cs:415-433`).
An authored pane or post graph with a feedback image therefore blends the old
picture into its next render. The SDF views are covered for their own temporal
resolve, because `SdfWorldPasses.OnOutputsForgotten` restarts it; an authored
graph has no such hook.

### Retained copies

Confirmed, three ways.

- An infinity layer's frame source is made when the layer is made
  (`src/Puck.World/WorldScreenBinder.Device.Infinity.cs:192-194`) and the
  release drops only the residency
  (`src/Puck.World/WorldScreenBinder.Device.Session.cs:145-150`). The settle
  then reports that nothing is held
  (`src/Puck.World/WorldScreenBinder.Device.Session.cs:95` passes
  `entry.Residency is not null`), so the guard forgets what the source holds.
- A view's export ring keeps the image a completed frame wrote
  (`src/Puck.World/WorldScreenBinder.Device.Probes.cs:271-275`), and nothing
  in a withdrawal reaches it. A probe kernel that reads the ring feeds its
  result back into the graph as a screen source.
- A comparison hold is a CPU copy of a captured display frame
  (`WorldFrameComparison.Hold`, `src/Puck.World.Client/WorldFrameComparison.cs:70-72`)
  that the root draws again.

### An environment producer that never forgets

Partly true. The default hook does nothing, and the environment producer does
not override it:

`src/Puck.Shaders/Graph/RenderGraphPackageRecorders.cs:330`

```csharp
    void OnOutputsForgotten(string instance) { }
```

So after a withdrawal the producer's watch still holds the publication it last
reduced. On the ordinary path that looks caught, though this was read and not
run: the withdrawal's edge walk reaches the producer, it is scheduled again,
and its watch (`ScreenSourceWatch`) compares the screens it reads now with the
ones it last reduced, which owes a new reduction that writes a zero record for
a screen with no image. The review's stronger claims do not hold as stated:

- The frozen reduction needs the retirement path above (no withdrawal at all)
  together with every screen unbound, when `SdfSkyEnvironmentPasses.IsUnchanged`
  does answer true. An unbound screen is outside the count the screen lights
  run to, so its stale record is storage and not a shown picture, until a
  screen binds again under the same index. This bullet is reasoned from the
  code, not run.
- The irradiance probes never held screen light. Their sky row is a table the
  host computes (`src/Puck.SdfVm/SdfMeshIrradiance.Gpu.cs:233`), and the
  camera sample's own contract says "An anchored light and the screens are no
  part of the sample" (`SdfMeshIrradiance`).

### A withdrawn root leaves the old frame on the display

Partly true. The mechanism is exact: the root's image is read from its latest
output, which a withdrawal clears,

`src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1615-1618`

```csharp
    private Surface RootImage() {
        if (m_producers[m_root] is not { } producer) {
            return LatestOf(index: m_root).Image;
        }
```

and both compositors treat an empty surface as "no new frame" and return
(`src/Puck.DirectX.Presentation/DirectXSurfaceCompositor.cs:345-348`,
`src/Puck.Vulkan.Presentation/SurfaceCompositor.cs:309-316`). It is reachable
only when the root does not render in the withdrawal's own frame: a root that
is itself the gated view and was just paused, or one whose node cannot render.
The synthesized root of the debug world renders again in that frame.

### Combining epochs by their maximum

True of the code (`WorldViewAccess.Join`,
`src/Puck.World.Client/WorldViewerAccess.cs:71-74`): the maximum of a chain
does not change when one member is swapped for an older one under a newer
parent. Every new tracker takes an epoch newer than all before it, so the case
needs a view name whose chain comes to resolve to an already existing tracker.
No such sequence was found or ruled out.

### The mirror reused across sessions

Partly refuted. A destination that ends a session admits the next one into the
same mirror (`src/Puck.World.Server/WorldInstanceHost.ScreenSessions.cs:608-612`),
and a delivered definition clears no body. But admission delivers a primer
snapshot in the same call, and a snapshot retires every body it does not name:

`src/Puck.World.Protocol/Protocol/WorldPoseTable.cs:101-109`

```csharp
        foreach (var index in m_live.Members) {
            if (!m_seen.Contains(index: index) && (index != preserved)) {
                Volatile.Write(
                    location: ref m_active[index],
                    value: false
                );
                m_placementId[index] = null;
            }
        }
```

The review's sequence needs the primer to be withheld, and a session whose
primer is withheld renders nothing. What survives the reuse is whatever no
delivery overwrites; that set was not enumerated.

### The delivery and settle race

Refuted on this tree, and worth keeping in mind. Both interleavings need the
destination to deliver on a thread other than the frame's. A session is only
ever admitted by a world this process hosts, hosted worlds step on the pump
thread, and frames are produced on that same thread:

`src/Puck.Launcher/HostLoop.cs:117`

```csharp
    /// <summary>Runs inline on the hosted service's existing pump thread. No simulation thread is started here.</summary>
```

The weakness is real all the same. The guard infers what a mirror holds from
two counters read without the mirror's gate, compared with a policy read live
from the authority. One of those counters is the cursor's delivery count
(`src/Puck.World.Server/WorldSessionSink.cs:347`), which does not move for a
snapshot that changes only bodies. The first destination that delivers from
another thread (a remote authority, a parallel tick) makes both races real.

## Why guarding each path does not converge

The three mechanisms share one flaw: each works out, after the fact and from
the outside, that a narrowing happened.

- The viewer's side asks the authority's live objects what the session may see.
  That is a second, smaller copy of the disclosure policy, and it disagrees
  with the real one (finding 1).
- The binder guesses what a mirror's contents were delivered under from
  counters (findings 3 and 4).
- The render graph is told an instance name and follows the edges that exist at
  that moment. Edges that have just been removed (finding 2), state that is not
  an output (findings 5 and 7), and copies that left through a side door
  (finding 9) are outside what it can follow.

A fix at each cause adds a tenth, eleventh and twelfth guard of the same kind.
The next view kind, cache or export is written by someone who does not know a
guard is owed.

## The invariant

Three terms first.

- A **stream** is one recipient's delivery from one authority: one
  `WorldDeliveryCursor`. A new session is a new stream, even behind the same
  screen.
- A stream's **access** is the answer the authority's policy gives for its
  recipient: whether it observes, its tier, which declared policies admit it,
  and the body policy. `WorldDisclosureImages.Audience` already computes
  exactly this as a key, and equal keys mean equal disclosed rows.
- A **span** is the part of a stream's life during which its access did not
  narrow. A narrowing ends the span and starts the next one.

The invariant:

> Every picture, and every product derived from one, carries the spans whose
> content it holds. A reader is handed a product only while every span it
> carries is its stream's current one.

A picture a viewer may no longer see is then not something a guard must find.
It is a product whose span has ended, and no reader can obtain it.

### What counts as a narrowing

A span ends when:

1. the stream stops observing (its grant is withheld, or its observation ends);
2. its tier lowers;
3. a policy that admitted the recipient no longer does, or is gone;
4. the body policy changes (until body disclosure joins the recipient view,
   when it becomes one more policy under rule 3).

A span does not end when the world changes under the same answers: a card that
moves to a zone the recipient cannot read, a body that leaves an interest
radius, a cell that is removed. The view renders again, as it does for any
scene change, and the temporal resolve treats the change as it treats any
other.

This line is drawn on purpose. The recipient was entitled to the earlier
picture when it was made, and it learns nothing from seeing it a moment longer.
The other choice, ending the span whenever the stream loses anything it held,
restarts a view's temporal history on every such play and on every body that
crosses an interest radius, which at the scale of a populated zone is every
frame. The choice is one predicate in the cursor, so the stricter reading is a
one-line change if it is wanted.

The rule is also the only one consistent with [the disclosure
guarantee](../disclosure.md#what-is-guaranteed-and-to-whom) about timing. A
span's end is something the recipient can observe, so it must be a function of
what the recipient may know. Whether a policy admits it is that. A declaration
that never admitted it is not, so adding or removing one must not move the
span: rule 3 compares the set of admitting policies, by value, and never the
whole policy list.

## The door

The door has two ends and one value that travels between them.

### One end: the stream says when a span ends

The cursor is the one place that knows what a stream was delivered, so it is
the one place that decides a narrowing. It gains the stream's identity, the
access it last delivered under, and a generation that moves by the rules above.
Every delivery carries the three as a stamp (proposed name
`WorldDeliveryStamp`: stream, generation, access key).

- Every `IClientSink` member that carries content takes the stamp. A sink or a
  caller without one does not compile.
- A session sink that begins to withhold, or whose observation ends, delivers
  the stamp of the new span with no content. Today it delivers nothing, which
  is why the viewer's side has to ask.
- A span's first delivery is whole: the document, then a primer snapshot. The
  sink already does this after a withheld span (`WorldDeliveryCursor.Release`
  and the replica primer).
- The authority's own unfiltered stream (the boot client, a destination's own
  scene, the tape) carries the authority's stamp. Its span ends only when the
  activation is replaced.

Nothing on the viewer's side reads `WorldSessionObservation` for policy again.
The local case and a remote authority become the same case: the stamp arrives
with the content, on whichever thread delivers it.

### The value in between: content is owned by its span

The mirror keeps what it was delivered per span. A delivery whose stamp names
another stream or generation ends the mirror's current span and starts an empty
one, so nothing delivered earlier is there to be read. The mirror hands out its
scene and the span together, published under its own gate as one image
(`WorldSessionMirror.PublishImage` already publishes the body table that way),
so a reader cannot pair one span's content with another's stamp.

On the viewer's side a span is an object (proposed name `DeliverySpan`, in
`Puck.Abstractions` because the render graph must name it without knowing what
a world is). It is live until its stream ends it. Everything the binder makes
from a mirror's scene is made through one holder keyed by the span: the frame
source, the emitter, the residency and the envelope registration of a session,
and the same four for an infinity layer. Asking the holder under a new span
disposes what the old span made and makes it again from the new scene. A job
the old residency submitted completes into objects nothing renders, which is
today's behavior for a released residency.

A frame source states the span it composes from. `ISdfFrameSource` gains a
member with no default, so every frame source, the boot world's included, says
so explicitly; the boot world's is the authority's span.

### The other end: one label on every output

The render graph already propagates one fact along every read: whether an
image holds external content a capture has not filled.

`src/Puck.Shaders/Graph/RenderGraphRuntime.Taint.cs:6-9`

```csharp
// Taint: an image holding external content the capture gate did not fill, and every output rendered from one. An
// external producer says so of each image it hands out; a graph instance's output is tainted when its render bound a
// tainted input. A capture frame renders every tainted instance the captured instance reads again, so a slow instance
// never carries external pixels into a capture, and a capture moves to its instance only over untainted inputs.
```

That path already reaches what the review found unreachable. It follows
previous-frame reads, it is noted at every bind (`RenderGraphRuntime.Bind`, the
external reads), it survives in retained package state through
`IRenderGraphPackageFactory.TaintOf` (a temporal history that read the content
earlier, a lighting source reduced from it), and it has a discard
(`IRenderGraphPackageFactory.DiscardTaintedState`). The withdrawal built a
second propagation beside it. The design is to carry the spans on the first
one. The flag becomes a small value: whether the content is unfilled, and the
set of spans it holds.

Four rules make it a door.

1. **Nothing reaches a pass without its label.** An image or a buffer reaches a
   node's shaders only through `ShaderPipelineRenderNode.BindImage`,
   `ShaderPipelineRenderNode.BindBuffer`, `ShaderPipelineRenderNode.HoldBinding`
   or an entry of the reads a package samples. Each takes the label as a
   required argument. An external producer's output and a view's frame source
   are the two places a label starts.
2. **An output's label is written in one place.** `RenderGraphRuntime.RememberOutput`
   is the only writer of an output record. It records the union of what the
   node has bound and what the instance retains. The retained set grows with
   every render and is emptied only by rule 4.
3. **An output is read in one place.** The two arrays of output records move
   into a small type whose only read answers "none" for a record that carries
   an ended span. `RenderGraphRuntime.RecordedAt`, `RenderGraphRuntime.Resolve`,
   the display, captures, standing outputs and the holds a reconfiguration
   plans all read through it, because the arrays are no longer visible to them.
4. **Retained state is the runtime's to discard.** At the start of a frame,
   every instance whose label or retained set holds an ended span is discarded
   as a whole: its output records, its node's publication, its node's stored
   contents (the part of `ShaderPipelineRenderNode.Reset` that marks storage
   uninitialized, without the wait), and its packages' per-instance state. Then
   it renders again before anything shows it, as a withdrawn instance does
   today.

Rule 3 is what makes the property hold by construction. Rule 4 is what keeps
the display from going blank: without it a consumer's stale output would be
refused and nothing would replace it.

Per-instance package state is the one part a compiler cannot see, so it is made
fail-closed. A package keeps what it carries from frame to frame for an
instance in an object the runtime asks it to create and hands back in the
recorder's context, as `RenderGraphSlotResults` already does for results that
wait on a slot. A discard drops that object and asks for a new one. A package
then cannot forget one field, and a package that keeps such state somewhere
else gains nothing from it, because its instance's retained set still names the
ended span and its next output is refused until the discard ran.

### What a narrowed viewer is shown

The view renders again in the same frame from its new span. While the new
residency is not ready, or when the stream no longer observes, the view has no
output, and the screen that shows it draws the session source's authored
`Fallback` colour, which is what a screen at the nesting limit shows today.
Baked meshes are found by content in the shared library, so a rebuilt residency
shows geometry the new delivery still names without baking it again.

The display root is the one reader that cannot fall back to a screen. When the
root's own record was discarded and the root did not render in that frame, the
runtime returns its stand-in image, and an empty surface keeps meaning only "no
new frame".

How a withheld screen looks beyond a colour is the author's. The session source
is the authored surface that carries it.

### Sharing between viewers

Two streams of one world whose access keys are equal are delivered the same
scene, so they may render from one residency. That is the rule the rendering
plan asks for ([decision 14, step 5](../rendering-research.md#14-treat-a-nested-view-as-the-same-view-at-another-size)),
and it replaces `WorldSessionObservation.DisclosesEverything` with a comparison
of two values the authority stamped. A window joins its destination's own scene
while its stream's key equals that scene's stream's key. For that comparison
the key leaves out the authority's bookkeeping bypass, which changes row
reservations and nothing a scene reads; the scene a mirror hands out exposes no
reservation.

A view of a shared residency states the key it may show, and renders only a
frame whose content carries that key. A frame that does not is answered as a
waiting source is today (`ISdfFrameSource.Waiting`): the view keeps its last
output and renders nothing, and the binder moves it to a residency of its own
at the next settle. Its earlier pictures stay valid, because they were rendered
while the keys were equal.

### Where each kind of product sits

| Product | What holds it behind the door |
|---|---|
| A view's picture, and every picture that showed it | The output record's label and the filtered read |
| Temporal history and any stored contents of a node | The runtime's discard |
| Screen light and environment light reduced from a picture | The producer's per-instance object is replaced, and it has no output until it reduces again |
| Irradiance probes | They hold no screen light. A session's own probe volume belongs to its residency |
| A residency, its frame source, and the jobs they started (program builds, bakes, probe traces, uploads) | The holder keyed by the span; a job finishes into objects nothing renders |
| A baked mesh | Found only by its geometry's content; outside the guarantee as a timing channel |
| An export ring and a comparison hold | The label of what was copied, given back at re-entry |
| The frame on the display | The stand-in, when the root was discarded and did not render |

## What enforces each part

| Part | Enforced by | A new caller that forgets |
|---|---|---|
| A delivery names its span | The compiler: a required argument on every `IClientSink` content member | Does not compile |
| A narrowing is decided once | One door: the cursor's stamp is the only writer of the generation | Has no other place to write it |
| A reader never pairs content with another span | A type: the mirror hands out scene and span as one value, with no other read of the scene | Cannot read the scene without the span |
| What is made from a scene dies with its span | One door: the holder keyed by span is the only maker of a session's or a layer's frame source and residency | Has nothing to construct them from |
| A frame source states its span | The compiler: an interface member with no default | Does not compile |
| An input brings its label | The compiler: required arguments on the bind methods and on an external output | Does not compile |
| An output is read through the filter | A type boundary: the records are private to the type that filters | Cannot index the records |
| Stored contents are discarded | One door: the runtime's discard, driven by the label | Is not asked |
| A package's per-instance state is discarded | Fail-closed: the runtime replaces the object it issued; state kept elsewhere leaves the output refused | Shows a fallback, never the old picture |

Two things stay review-only, and the memo says so plainly: a hop that passes an
empty label where it should pass one it was given (an export ring's reader, a
probe source), and a package that keeps a derived value for an instance outside
the object the runtime issues and also outside its outputs, such as a metered
exposure. The first is a short list of constructors that accept the empty
label. The second is named for the SDF views in step 5.

## Where the guarantee stops

- **Pixels that have left the engine.** A screenshot file, a recorded clip, the
  operating system's compositor, a capture device, and a remote client's own
  memory. A remote viewer was sent the content; a hostile client keeps it.
- **What the stream is delivered.** That is [the disclosure
  plan](../disclosure.md)'s. Body disclosure has not joined the recipient view
  yet ([its open item](../disclosure.md#open-items)), so until it does the body
  policy is stamped beside the access key.
- **The frame in flight.** A span that ends while a frame is being produced
  takes effect when the next frame begins.
- **The shared display.** The boot world's own stream is the operator's and is
  unfiltered by design. Its span does not end.
- **Wall time and machine resources.** A mesh baked from wider content stays in
  the content-addressed library and store, where only a program that names the
  same geometry can draw it; that it is there, and how long a rebuild takes,
  are timing channels the disclosure plan already places outside its guarantee.
  Who may fetch a shared product by name is [its second
  boundary](../disclosure.md#presentation-and-derived-product-disclosure).
- **One visible re-route.** A window that shared its destination's scene moves
  to its own residency when the destination first declares something the
  window's session cannot read. The window holds its last picture for the time
  the residency takes to build. That pause tells the session that such a
  declaration appeared. Declarations are document installs, not play, and a
  world that declares its restricted rows before its sessions exist never
  shares in the first place. Closing it fully means a window never shares,
  which costs a residency per window and the seamless crossing.
- **Deliberate code.** Engine code that stores a raw image handle in a field of
  its own is review's, as it is for the state boundary.

## Each finding, answered

| Finding | What closes it |
|---|---|
| 1. Whole-replica test reads only the body policy | The test is deleted. Sharing compares stamped access keys (step 1) |
| 2. A retiring view skips the withdrawal | The label is on the consumer's own record, not on an edge. A reconfiguration cannot remove it, and a planned hold reads through the filter (step 2) |
| 3. Mirror reuse; a replaced session keeps the residency | A new session is a new stream, so a new span: the mirror starts empty and the holder remakes the residency (steps 3 and 4) |
| 4. Delivery and settle race | The counters and the live policy read are deleted. The span arrives with the content under the mirror's gate (step 3) |
| 5. Node history survives | The runtime's discard marks stored contents uninitialized (step 5) |
| 6. The old frame stays on the display | The runtime returns its stand-in for a discarded root that did not render (step 2) |
| 7. The environment producer never forgets | Its watch and publication live in the object the runtime replaces; a fresh one owes a reduction, and until it completes the producer has no output (step 5) |
| 8. Maximum of epochs | Deleted. A label is a set of spans, and each span knows whether it is live |
| 9. Infinity source, export ring, comparison hold | The layer's source is made by the span's holder (step 4). An export and a hold carry the label of what they copied and give it back when they re-enter the graph (step 5). The mesh store is outside the guarantee |

## Options weighed

- **Fix each finding at its cause.** The review gives nine smallest fixes. Each
  is correct, and each is one more guard of the kind that failed. Taken alone
  this is rejected; the fixes' intents are all present in the steps below.
- **Put the generation in the view's name.** A narrowed view would become a new
  instance and its old outputs would be unreachable under the old name. This
  covers the view and nothing that read it: the consumer's record, history and
  lighting keep the old picture, which is most of the findings.
- **Render a restricted viewer in a separate runtime.** No image would be
  shared, so nothing could cross. This is the strongest boundary and the right
  shape if pictures are ever rendered on a server for a remote viewer, where
  the frames are the delivery. For screens inside one presentation it gives up
  sharing a residency between a window and the world beyond it, which is what
  makes a crossing cost nothing. The label design does not block it: a picture
  from another runtime enters as an external output with its label.
- **Carry the spans on the existing taint path (chosen).** One propagation,
  already at every bind and already reaching retained state.

At the scale Puck aims for, each zone and each server is a world and a client
shows several through portals at once. Streams are per viewer and world, spans
per stream, and a label holds as many spans as there are streams in one
picture: a handful. The definition of narrowing matters most there: interest
management changes what a stream holds on almost every tick, and none of it
ends a span.

## The build order

Each step lands on its own and removes the guard it replaces in the same
change. No step opens a path before its protection exists. Step 1 goes first
because finding 1 is the only one that shows content never delivered.

1. **The stamp, and sharing by key.** The cursor computes the access key and
   the generation; every content delivery carries the stamp; a session sink
   delivers a stamp when it withholds and when it ends; the window route
   compares keys. Deletes `WorldSessionObservation.DisclosesEverything` and the
   policy test in `WorldInstanceHost.TryWindowEndpoint`.
   *Check:* the disclosure harness (`WorldDisclosureRun`) records each
   recipient's stamps in its transcript. On `worlds/debug/deduction-disclosure.puck`,
   a recipient removed from an audience shows one generation step, and two
   variants that differ only in hidden rows, or in a declaration that never
   admitted it, show equal stamps. A fixture whose destination declares one
   restricted placement boots headless, and `world.residency` lists a residency
   of the window's own.
2. **The label on the render graph.** The taint flag becomes the carried value;
   the bind methods, external outputs and package reads require it; the output
   records move behind the filtered read; the frame begins with the discard by
   label; a discarded root that did not render returns the stand-in. Spans are
   supplied by today's `WorldViewerAccess` until step 3 (a new span where the
   epoch moves today). Deletes `IRenderGraphInstances.Withdraw`, the edge walk
   in `RenderGraphRuntime.ApplyWithdrawals`, the epoch table in
   `WorldViewGraphHost.BindAccess`, `WorldViewAccess.Join` and
   `WorldViewAccess.Retired` with the name grammar that feeds it.
   *Check:* device-free laws beside `RenderGraphRuntimeLawTests`, one per
   sequence: a producer whose span ends and which is removed by a
   reconfiguration in the same frame leaves no reader an image of it, with a
   consumer whose replacement graph is still building; a paused consumer
   presents nothing; a root that cannot render returns the stand-in and not an
   empty surface. `puck search -M 0 "m_current\["` finds the records only
   inside the filtering type.
3. **The mirror keeps its scene per span.** The mirror ends and starts spans
   from stamps and hands out scene and span as one value. Deletes
   `WorldViewerAccess`, `WorldSessionRoute.SettleAccess`, the binder's
   `ContentAccess` and its delivery mark, and `WorldScreenBinder.AccessOf`'s
   read of the observation.
   *Check:* a law beside `WorldScreenBinderLawTests`: deliver a document and
   two bodies under one stamp, then a document under the next generation; the
   scene holds no body until that generation's snapshot. Admit a second stream
   into the same mirror; the scene starts empty.
4. **One holder for what a scene makes.** A session's and an infinity layer's
   frame source, emitter, residency and envelope registration are made through
   the holder. Deletes `WorldScreenBinder.WithdrawOwnSource`,
   `WorldScreenBinder.WithdrawInfinityResidency` and the eager source in
   `WorldScreenBinder.CreateInfinity`.
   *Check:* a headless boot of the debug world, then on the step 1 fixture
   revoke the session's grant and grant it again: `world.residency` shows the
   session's residency gone and made again, and a counter of spans ended and
   residencies remade reads one each.
5. **Retained state under the discard.** The node's stored contents are
   discarded by the runtime; the SDF views' per-instance state (the temporal
   resolve, the picks, the metered exposure) and the environment producer's
   watch and publication move into the object the runtime issues; an export
   ring and a comparison hold carry their label. Deletes
   `IRenderGraphPackageFactory.OnOutputsForgotten` and
   `ShaderPipelineRenderNode.WithdrawPublication` as separate calls.
   *Check:* laws: a history image written under a span reads as uninitialized
   after the span ends; the environment producer has no completed output after
   a discard until it records, and `SdfSkyEnvironmentPasses.IsUnchanged`
   answers false until then; a comparison hold taken over a picture whose span
   ended is refused at the bind.
6. **Shared residencies by key.** Residencies of one world are found by access
   key, and a view of a shared one renders only frames that carry its key.
   *Check:* in the debug world the painting wing's windows still share their
   destinations' scenes: the residency count in `world.residency` is what it
   was before the change. With the step 1 fixture's restricted placement added
   by `world.reload` while the window is shown, the count rises by one and the
   window's capture does not show the placement.
7. **The picture checks the disclosure plan names.** These need a window and
   belong to the lead's batch on both backends: narrow a full view to a
   restricted one on a frame its refresh is held, and revoke a viewer while a
   second viewer keeps the shared residency, each with a detail only the full
   view shows and a GPU job submitted before the change finishing after it.
   *Check:* a presented burst (`world.screenshot <path> presented 8`) starting
   at the change shows the detail in no frame.

Steps 2 and 5 touch `Puck.Shaders` and `Puck.SdfVm`; steps 1, 3 and 4 touch the
world projects. Step 2 can run beside step 1. Steps 3 and 4 need step 1, step 5
needs step 2, and step 6 needs steps 1 and 4.

## What it deletes

- On the authority's side: `WorldSessionObservation.DisclosesEverything` and
  the second policy copy it stands for.
- On the viewer's side: `WorldViewerAccess` and `WorldSessionAccess`,
  `WorldViewAccess` with its join and its retired value, the generated-name
  grammar in `WorldScreenBinder.AccessOf`, the binder's `ContentAccess` and
  delivery mark, `WorldScreenBinder.WithdrawOwnSource`,
  `WorldScreenBinder.WithdrawInfinityResidency`, and the epoch table and
  withdrawal call in `WorldViewGraphHost.BindAccess` (the pause it applies
  stays, driven by whether the current span observes).
- In the render graph: `IRenderGraphInstances.Withdraw`, the withdrawal list
  and edge walk, `IRenderGraphPackageFactory.OnOutputsForgotten` and its one
  implementation, and the separate capture-taint bookkeeping, which becomes one
  field of the carried value.

## Systems considered

Searched before proposing a generation or a key, as rule 7 asks.

- `WorldDeliveryCursor.Sequence` counts a stream's deliveries and keeps
  counting across a withheld span, so it cannot say that access narrowed. It is
  the right owner for the generation, which is why the generation goes there.
- `WorldDocumentVersion` names an install by activation and ordinal. A session
  sink already replaces the ordinal with the stream's own count. Its activation
  is the natural root of a stream's identity.
- `WorldSessionMirror.DefinitionRevision` and `WorldSessionMirror.SnapshotRevision`
  are the viewer-side counters the binder's guard reads now. They count
  deliveries, and the second is assigned from the cursor's count.
- `WorldDisclosureImages.Audience` is the access key. It is reused, not
  rebuilt: the rows' images are already shared by it.
- The disclosed document's content identities in `WorldDisclosureImages` move
  with every value, so a held picture could never match them. They suit reuse
  of derived products by content, not validity.
- `StateArena.DisclosureRevision` is the authority's count of writes that can
  change policy. It is one number for every recipient and it moves for hidden
  changes, so a recipient must not be shown it.
- The render graph's taint (`RenderGraphExternalOutput.Tainted`,
  `IRenderGraphPackageFactory.TaintOf`, `SdfWorldTables.ProjectTaintedSourcesAgain`)
  is the propagation the label rides.
- `RenderGraphSlotResults` is the precedent for package state the runtime
  issues and takes back.
- The session source's `Fallback` colour is the authored answer to "what does a
  screen with nothing to show draw".

No per-stream disclosure generation exists anywhere in the tree, on either
side. That part is new.

## Open questions and their owners

- Whether the stamp rides inside `WorldDocumentVersion` and `WorldSnapshot` or
  beside them as its own argument. The step 1 lane decides; the requirement is
  only that no content member lacks it.
- Whether the retained set should lose a span that the node's stored contents
  can no longer hold, so that a view which stopped showing a screen long ago is
  not restarted when that screen's session narrows. The step 5 lane measures
  how often that restart happens on the benchmark route before adding it.
- Which state of a mirror no delivery overwrites today (finding 3's residue).
  Step 3 makes the question moot by starting each span empty; the step 3 lane
  lists what it found there in its commit.
- The Codex design review of this memo comes before any step is briefed.

---

[Disclosure](../disclosure.md#presentation-and-derived-product-disclosure) · [Plans](../README.md) · [Open items](../open-items.md#security-review) · [Rendering research](../rendering-research.md)
