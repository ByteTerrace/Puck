# The withdrawal chokepoint

A screen or a portal shows another world through a session that world admitted,
and the world decides what that session may see. When what the session may see
shrinks, the engine must stop showing what it drew before. This memo checks an
adversarial security review's nine findings against the code, states the
property as one invariant, and designs the structural door that holds it, in
place of the per-path guards the review kept finding ways around. A design
review of the first draft found sixteen flaws in that door; this revision
closes each at its cause and [answers them one by one](#the-design-reviews-findings-answered).
Step 1 of [the build order](#the-build-order) is built; the rest is a design.

## Summary

- **The property.** No picture, and nothing derived from one, that was made
  from content a viewer has since lost the right to read reaches that viewer,
  or whoever views in its place.
- **What the security review got right.** Of nine findings, four hold as
  stated, four hold in part, and one cannot happen for a session screen on the
  current tree. The worst is the only one that shows content that was never
  delivered: a session with a restricted row is classed as seeing everything
  and is drawn from the authority's whole replica.
- **Why fixing each one does not converge.** Three mechanisms each work out
  after the fact that access shrank: the viewer's side asks the authority's
  live policy, the screen binder compares two counters to guess what a mirror
  holds, and the render graph follows the read edges of whichever instances
  still exist. Every finding is one of those inferences missing a case.
- **The door.** The authority decides a cut once, where the stream is written,
  from what that stream was delivered and can no longer read. The cut travels
  to the viewer in order with the content. Content and the right to show it
  travel as one value that cannot be taken apart, and a reader is given the
  content only while the right is current. Retained values live only as long
  as the generation that made them.
- **What it costs.** A cut rebuilds the cut view's residency and restarts the
  temporal history of every view that showed it.
- **One rule the owner may want to overrule.** A cut is the loss of something
  the recipient was delivered from a part of the world that can withhold from
  it. A card turned face down is a cut. A body that leaves an interest radius
  is not, and neither is a deletion in a row the recipient reads whole
  ([the reasoning](#what-counts-as-a-cut)). The first draft ruled that the
  card was not a cut; the design review showed why that does not hold.
- **What stays review-only** is listed in [its own section](#what-stays-review-only).

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

## What the security review found, checked against the code

Each finding was reopened at its file and symbol on the current integration
tip. A fenced quote below was pasted from the file it cites. Where a step was
reasoned and not read or run, the text says so. The ranking orders the findings
that hold by what a viewer can be shown.

| Rank | Finding | Verdict |
|---|---|---|
| 1 | A session with restricted rows is drawn from the whole replica (finding 1) | Confirmed. Shows content never delivered |
| 2 | A view that retires skips the withdrawal (finding 2) | Confirmed |
| 3 | A session replaced at the same level keeps the previous session's residency (finding 3, second sequence) | Confirmed in the code; what it then shows is partly reasoned |
| 4 | A node's own history survives a withdrawal (finding 5) | Confirmed |
| 5 | Retained copies: an infinity layer's source, an export ring, a comparison hold (finding 9, parts 1 to 3) | Confirmed |
| 6 | Lighting reduced from a withdrawn screen (finding 7) | Partly true. The producer has no forget hook and views bind its buffer directly; the probes never held it |
| 7 | A withdrawn root leaves the old frame on the display (finding 6) | Partly true as stated, and wider than stated: a present can be skipped for a second reason |
| 8 | The mirror reused across sessions (finding 3, first sequence) | Partly true. Membership is replaced; an earlier pose can survive |
| 9 | Combining epochs by their maximum (finding 8) | True of the code; no reachable case shown |
| — | The delivery and settle race (finding 4) | Not reachable for a session screen on this tree. Real for a remote observer |
| — | Baked meshes stay in the shared library and store (finding 9, part 4) | True. Exact reuse is outside the guarantee; reuse by form is not |

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

`src/Puck.World.Client/WorldViewGraphHost.cs:629-632`

```csharp
        Reconcile(views: views);
        if (m_runtime is { } runtime) {
            BindAccess(runtime: runtime);
        }
```

`Reconcile` reconfigures the runtime in the same call when the screens' views
changed (`src/Puck.World.Client/WorldViewGraphHost.cs:1297`). A session view
that ended is by then absent from the set, and `BindAccess` only forgets its
epoch:

`src/Puck.World.Client/WorldViewGraphHost.cs:649-651`

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
revision counters, not traced.

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
(`src/Puck.Shaders/ShaderPipelineRenderNode.Tracker.cs:415-433`). An authored
pane or post graph with a feedback image therefore blends the old picture into
its next render. The SDF views restart their own temporal resolve through
`SdfWorldPasses.OnOutputsForgotten`; an authored graph has no such hook.

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
  in a withdrawal reaches it. A probe that reads the ring holds its own
  acquisition and feeds its result back into the graph as a screen source.
- A comparison hold is a CPU copy of a captured display frame
  (`WorldFrameComparison.Hold`, `src/Puck.World.Client/WorldFrameComparison.cs:70-72`)
  that the root draws again, and `WorldFrameComparison.Measure` reads it with
  no graph bind at all.

### Lighting reduced from a withdrawn screen

Partly true. The default hook does nothing, and the environment producer does
not override it:

`src/Puck.Shaders/Graph/RenderGraphPackageRecorders.cs:330`

```csharp
    void OnOutputsForgotten(string instance) { }
```

The reduced screen light is also not only a graph output. Every world set binds
the residency's buffer directly:

`src/Puck.SdfVm/SdfWorldTables.Bindings.cs:88`

```csharp
        WriteWorldBuffer(buffer: ScreenEmission, member: SdfWorldPackage.ScreenLights, set: set);
```

So forgetting a graph record does not stop a view sampling the earlier
reduction while the next one is held, pending or incomplete. On the ordinary
withdrawal path the producer is scheduled again and its watch
(`ScreenSourceWatch`) owes a new reduction; that was read and not run.

The claim that the irradiance probes hold screen light does not stand. No probe
kernel reads a screen, their sky row is a table the host computes
(`src/Puck.SdfVm/SdfMeshIrradiance.Gpu.cs:233`), and the camera sample's own
contract says "An anchored light and the screens are no part of the sample"
(`SdfMeshIrradiance`).

### A withdrawn root leaves the old frame on the display

Partly true as stated. The root's image is read from its latest output, which a
withdrawal clears,

`src/Puck.Shaders/Graph/RenderGraphRuntime.cs:1615-1618`

```csharp
    private Surface RootImage() {
        if (m_producers[m_root] is not { } producer) {
            return LatestOf(index: m_root).Image;
        }
```

and both compositors treat an empty surface as "no new frame"
(`src/Puck.DirectX.Presentation/DirectXSurfaceCompositor.cs:345-348`,
`src/Puck.Vulkan.Presentation/SurfaceCompositor.cs:309-316`). That needs a root
that does not render in the withdrawal's own frame. The design review added a
second way to the same result that needs nothing unusual: a present is also
skipped while the display encode's pipeline is building, whatever surface it is
handed:

`src/Puck.DirectX.Presentation/DirectXSurfaceCompositor.cs:349-354`

```csharp
        if (!EnsureEncode(deviceContext: deviceContext)) {
            // Counted as presentation.skipped, which world.counters reads.
            m_presentation.RecordSkip();

            return;
        }
```

### The mirror reused across sessions

Partly true. A destination that ends a session admits the next one into the
same mirror (`src/Puck.World.Server/WorldInstanceHost.ScreenSessions.cs:608-612`),
and a delivered definition clears no body. Admission delivers a primer snapshot
in the same call, and a snapshot retires every body it does not name
(`WorldPoseTable.Complete`, `src/Puck.World.Protocol/Protocol/WorldPoseTable.cs:101-109`).
That replaces membership. It does not replace the pose of a body both sessions
hold at the same index: an admitted body that was already active takes its
earlier endpoint from what the table held,

`src/Puck.World.Protocol/Protocol/WorldPoseTable.cs:224-225`

```csharp
            m_previousPosition[index] = priorPosition;
            m_previousOrientation[index] = priorOrientation;
```

and the emitter interpolates between the two endpoints. The first draft called
this sequence refuted; the design review corrected it.

### Combining epochs by their maximum

True of the code (`WorldViewAccess.Join`,
`src/Puck.World.Client/WorldViewerAccess.cs:71-74`): the maximum of a chain
does not change when one member is swapped for an older one under a newer
parent. No sequence that does this was found or ruled out.

### The delivery and settle race

Not reachable for a session screen on this tree. A session is only ever
admitted by a world this process hosts, hosted worlds step on the pump thread,
and frames are produced on that same thread:

`src/Puck.Launcher/HostLoop.cs:117`

```csharp
    /// <summary>Runs inline on the hosted service's existing pump thread. No simulation thread is started here.</summary>
```

The weakness is real. The guard infers what a mirror holds from two counters
read without the mirror's gate, and one of them is the cursor's delivery count
(`src/Puck.World.Server/WorldSessionSink.cs:347`), which does not move for a
snapshot that changes only bodies. And a stream that delivers from another task
already exists: `WorldRemoteAuthority.AttachSink` runs its observer on a task
of its own, and keeps what it last delivered across a lost connection:

`src/Puck.World.Server/WorldRemoteAuthority.cs:890-891`

```csharp
    // Reconnect the same held lease until its owner releases it; the mirror keeps the last delivered revision while
    // disconnected and the authored unavailable policy remains the crossing-side safety net.
```

The door therefore has to order a cut against frames and against a connection
that fails before the cut arrives.

## Why guarding each path does not converge

The three mechanisms share one flaw: each works out, after the fact and from
the outside, that access shrank.

- The viewer's side asks the authority's live objects what the session may see.
  That is a second, smaller copy of the disclosure policy, and it disagrees
  with the real one.
- The binder guesses what a mirror's contents were delivered under from
  counters.
- The render graph is told an instance name and follows the edges that exist at
  that moment. Edges that have just been removed, state that is not an output,
  and copies that left through a side door are outside what it can follow.

A fix at each cause adds a tenth, eleventh and twelfth guard of the same kind.
The next view kind, cache or export is written by someone who does not know a
guard is owed.

## The invariant

Three terms first.

- A **stream** is one recipient's delivery from one authority: one
  `WorldDeliveryCursor`. A new session is a new stream, even behind the same
  screen.
- A **cut** is the moment a stream loses the right to read something it was
  delivered. [The next section](#what-counts-as-a-cut) defines it.
- A **span** is the part of a stream's life between two cuts. A view's
  **route** is every stream its picture depends on, from the leaf it draws up
  through each session or sky layer it is seen through.

The invariant:

> Content and the spans it was made from travel as one value. A reader is given
> the content only while every span of the value is current for the route that
> reads it, as latched for the frame that reads it.

A picture a viewer may no longer see is then not something a guard must find.
It is a value whose span has ended, and no reader can take the content out of
it.

### What counts as a cut

The authority decides a cut at the cursor, from two things the recipient itself
could see: what the stream last delivered, and what the recipient's view
discloses now.

A cut happens when something the stream delivered, from a part of the world
that can withhold from this recipient, is no longer readable by it:

1. a cell the stream held in a row that can withhold (the row's own policy, a
   cell policy, or a containing zone) is not in the row's image now;
2. a placement, field region or solid size the stream held from a section that
   can withhold is not in the recipient's projection now;
3. the stream stops observing, or its tier lowers, which loses everything the
   lower state does not carry;
4. the body policy changes to one that redacts.

The first draft compared policy answers instead, and that misses a real loss.
A cell is unreadable when it is a member of a zone whose policy denies the
recipient, and that changes while every policy and every answer stays fixed:

`src/Puck.World.Schema/WorldStateDisclosure.cs:833-848`

```csharp
                if (!m_arena.TryCellSlot(
                    key: key,
                    rowOrdinal: zoneOrdinal,
                    slot: out _
                )) {
                    continue;
                }
                if (
                    !Allows(policy: zone.Visibility) ||
                    !Allows(policy: m_arena.Visibility(
                    key: key,
                    rowOrdinal: zoneOrdinal
                ))
                ) {
                    return false;
                }
```

A card that moves into such a zone is therefore a cut, and a held pane or an
export must not keep showing its face. This reverses the first draft's ruling.

Three losses are deliberately not cuts, and each has one reason.

- **A deletion in a part the recipient reads whole.** Nothing there can be
  withheld from it, so the only explanation for a cell leaving is that it is
  gone, and the earlier picture showed what the recipient may still know.
- **Body membership under an unchanged body policy.** A body that leaves an
  interest radius and a body that despawns both simply stop arriving. At the
  scale of a populated zone that happens on nearly every tick, and restarting a
  view's history each time would leave it never converged. The view renders
  again without the body, as it does for any scene change.
- **A changed value in a cell the recipient still reads.**

The first two rules treat a hidden cell and a removed cell alike on purpose. In
a part that can withhold, the recipient must not be able to tell the two apart
([the disclosure guarantee](../disclosure.md#what-is-guaranteed-and-to-whom)),
so both cut. Under the body rule neither cuts. No rule lets a cut happen in one
case and not the other.

Because a cut is computed only from what the recipient was delivered and what
it is disclosed now, it cannot be moved by a change that was hidden throughout:
a hidden install, a policy the recipient never passed being added or removed, a
write to a row it never read. That is the property the timing guarantee needs,
and the build checks it on whole transcripts.

The cost is a restart of the view's temporal history and a rebuild of its
residency each time a spectator loses a card it could read. Whether interest
losses should cut as well is the one product rule here an owner might want
otherwise; the predicate is one function in the cursor.

## The door

The door has seven parts. Each one is where a class of the findings is closed
at its cause.

### 1. The stream decides a cut, and says only that

The cursor is the one place that knows what a stream was delivered, so it is
the one place that decides a cut. It keeps the last delivered image of every
part that can withhold (it already keeps their identities), applies the rules
above when it composes the next delivery, and advances the stream's generation
on a cut.

Two values go on the wire with every record, and nothing else about access:

- an **opaque stream identity**, minted at random per cursor and derived from
  no principal, policy or table;
- the **generation**, a counter.

The first draft put an access key on the wire, built from
`WorldDisclosureImages.Audience`. That key has one position per declared
policy, so its length alone tells a recipient that a policy it fails was added.
The key stays inside the authority and is never sent, compared across
processes, or used to decide a cut.

Every `IClientSink` member that carries content takes the two values. A session
sink that begins to withhold, or whose observation ends, sends a record of the
new generation with no content; today it sends nothing, which is why the
viewer's side has to ask.

### 2. A cut is ordered against frames, connections and late work

A value under a gate does not order a cut. Four rules do.

**Ingress accepts only what follows.** A mirror is bound to the one admitted
connection and stream that feeds it. Every record carries the stream identity,
the generation and an ordinal that only rises. Ingress drops a record whose
stream is not the bound one, whose generation is behind the mirror's, or whose
ordinal is not ahead, so a late record of an earlier generation cannot bring
that generation back. A different stream reaches a mirror only through a new
admission on its channel, which closes the old span first.

**A span that can no longer hear a cut closes.** A terminal record closes it,
a replacement closes it, and a lost connection closes it: a viewer that cannot
be told of a cut must not go on showing what it has. An authority may grant a
stream a lease, the time its last delivery stays showable after a loss, as part
of the admission that created the stream. The engine's default is none. This
replaces the remote observer keeping its last revision across a disconnect.

**Every generation can be primed alone.** The first records of a generation are
the whole disclosed document and then a primer snapshot, sent even when the
ordinary difference or snapshot cadence would send nothing. A new span's mirror
starts empty, with a new pose table whose first snapshot seeds both pose
endpoints, so no earlier pose survives into it.

**A frame latches once, and a cut takes effect at the next latch.** Before a
frame settles its routes or reconfigures the graph, it latches, for every
mirror it will read, one coherent pair of scene and span, and one snapshot of
which spans are current. Every decision in that frame (planning, binding,
recording, what the frame returns) reads the latched snapshot and never a
live flag, so one frame cannot decide two ways. A cut that ingress accepts
after the latch takes effect at the next frame's latch; the latched frame
completes and is presented. That one frame is where the guarantee stops in
time. A stronger barrier, dropping the latched frame at present, was weighed
and not taken: it would make every present wait on every stream.

Work that finishes later than the frame that started it carries that frame's
latch: a GPU job, an asynchronous build, a readback, a deferred submission or
hand-over. When it completes, its result is published only if every span it
was recorded under is still current at the latch of the frame that takes it.
Otherwise it is dropped.

### 3. What a scene makes is owned by its whole route

On the viewer's side a span is an object (proposed name `DeliverySpan`, in
`Puck.Abstractions` because the render graph must name it without knowing what
a world is). It is minted only by a mirror's ingress, for the authenticated
stream that mirror is bound to.

Everything the binder makes from a mirror's scene is made through one holder:
the frame source, the emitter, the residency and the envelope registration of a
session, and the same four for an infinity layer. The holder is keyed by the
view's whole route, not by its leaf. A world layer of the sky brings its own
hosted mirror and its own child cameras and screens, and a nested session is
seen through its parents; the binder already joins the parents' access for
exactly this reason (`WorldScreenBinder.AccessOf`). So a cut anywhere on the
route replaces what the holder made, and a child view cannot outlive a parent's
cut by checking only its own stream.

The same rule governs reuse. A scene assembled for one route may contain child
screens that were admitted on that route and never on another. A picture or a
scene made for one route is therefore given to another only after the receiving
route's own check: the underlying scene data is shared when it is proved equal
([part 7](#7-two-viewers-share-only-on-the-authoritys-proof)), and each view
composes its own child inputs.

A frame source states the route it composes from through a member with no
default, so every frame source, the boot world's included, says so explicitly.
The boot world's is the authority's own stream.

### 4. Content and its spans are one value

The first draft added a label argument to the methods that bind an image. That
compiles when a caller passes an empty label, and it does nothing about a
handle a node already holds. The label has to be inseparable from the content.

**One carrying type.** The values that move an image or a buffer between owners
(an external producer's output, a node's binding, a hold, a lease handed to a
package, a capture's source, an export's slot) carry the content and its label
together, and expose the content only through one read that takes the frame's
latch. There is no way to obtain the handle and drop the label.

**Unclassified content is refused.** The label has three states. *Unclassified*
is the default value and is refused by every read. *Public* says the content
derives from no stream; only a source can mint it, through the context the
runtime issues to that source, and the sources that may are a short list
([review-only](#what-stays-review-only)). *Spans* names the routes the content
was made from. A forwarded handle with a forgotten label is therefore refused,
not shown.

**One writer.** An output record is written in one place. Today there are two:
`RenderGraphRuntime.RememberOutput`, and the conversion that serves a capture
of an uploaded source, which writes a record as untainted
(`src/Puck.Shaders/Graph/RenderGraphRuntime.Sources.cs:359`). Both go through
the one writer, which records the union of what the node read in that
recording.

**One read, used again every time.** The output records move into a type whose
only read answers "none" for a record that carries an ended span.
`RenderGraphRuntime.Resolve`, the display, captures, standing outputs, external
roots and the holds a reconfiguration plans all read through it. A node also
validates every binding it still holds each time it records, not only when the
binding was made, so a retained binding or a hold that outlived its record is
replaced by the stand-in before the pass runs.

This is the path the render graph already has for one fact, whether an image
holds external content a capture has not filled:

`src/Puck.Shaders/Graph/RenderGraphRuntime.Taint.cs:6-9`

```csharp
// Taint: an image holding external content the capture gate did not fill, and every output rendered from one. An
// external producer says so of each image it hands out; a graph instance's output is tainted when its render bound a
// tainted input. A capture frame renders every tainted instance the captured instance reads again, so a slow instance
// never carries external pixels into a capture, and a capture moves to its instance only over untainted inputs.
```

That plumbing reaches every bind and every previous-frame read, which is why
the label rides it. The design review is right that it proves less than the
first draft said: it is a flag a caller sets, and it has a second writer. The
carrying type and the one writer are what turn the plumbing into a door.

### 5. A cut resets an installed graph as one generation

Marking a node's storage uninitialized is not enough. A pass whose cadence
signature still matches can stand without recording, and a pass after it then
publishes the old storage under a clean label
(`src/Puck.Shaders/ShaderPipelineRenderNode.Cadence.cs:92` is the test a
standing pass must fail). A checkpoint taken for a deferred submission before
the cut can also restore what the cut cleared.

Each instance has a **generation**, a number the runtime advances when a span
in the instance's label or retained set has ended. Advancing it is one
operation on the node, a withdrawal reset, that does all of this together:

- unpublishes the image and every buffer;
- marks every stored content uninitialized, history included;
- clears every pass's cadence signature and input generations, and every alias
  and standing output, so nothing stands on a pre-cut recording;
- keeps the frame counter, the frame slots and the attachment indexing as they
  are, which `ShaderPipelineRenderNode.Reset` does not (its storage helper
  assumes the counter is zeroed);
- marks every checkpoint and deferred hand-over taken under the earlier
  generation as stale, so settling one after the cut restores nothing.

The instance then renders again before anything shows it, as a withdrawn
instance does today.

### 6. Retained values live as long as their generation

The first draft had the runtime replace one object it issued to a package, and
claimed that state kept elsewhere would fail closed. It does not: a value kept
in a factory, a residency or a callback survives, the next recording reads it,
and the new output carries only current spans. Two mechanisms replace that
claim.

**Generation-owned cells.** A value a package keeps for an instance from one
recording to the next lives in a cell the recorder's context issues for the
instance's current generation. A cell of an earlier generation cannot be
reached from the context. A callback that completes later (a readback, a
publication, `RenderGraphSlotResults`) captures the generation it was recorded
under, and the runtime drops a completion whose generation is not the
instance's current one, so a late result cannot publish into the replacement.

**Labelled publications for what several views share.** A residency's lighting
is not per instance: the screen emission, the sky environment map and its
coefficients are buffers in `SdfWorldTables` that every view of the residency
binds. Each such publication carries the union of the spans of the inputs it
was reduced from. The tables bind it through one accessor that checks that
label against the frame's latch, the direct world-set binding included, and
binds neutral lighting (the zero buffer the tables already keep for an unset
member) until a reduction recorded under the current generation has been
submitted ahead of its readers on the queue.

The owners the build moves, by name:

| Owner | What it keeps | Where it goes |
|---|---|---|
| `ShaderPipelineRenderNode` | Stored contents, cadence state, checkpoints | The withdrawal reset (part 5) |
| `SdfWorldPasses` per instance | The temporal resolve, the picks, the capture taint flags | A generation-owned cell |
| `SdfViewExposures` | A view's metered exposure | A generation-owned cell |
| `SdfWorldTables` | Screen emission, sky environment map and coefficients, their watches and publications | Labelled publications |
| `RenderGraphSlotResults` users | Readbacks and publications waiting on a slot | The generation on the completion |
| A view's export ring and its probes | The exported image, a probe's acquisition, a probe's output | Label on the exact slot (below) |
| `WorldFrameComparison` | The held crop, its difference counts | Label on the hold (below) |

**Copies are checked where they are read, not where they come back.** An
export's label belongs to the exact submission and slot that was written. A
probe checks it when it acquires a slot, its output carries the label of the
slot it sampled and never the camera's current one, and a cut on the route
tears down the probe's binding to the ring. A comparison hold carries the label
of the frame it cropped; the root's draw of it, the CPU measure and the cached
echo of its counts all read through the same check, and a capture that
completes after a cut is refused as other late work is.

Any other state a package keeps that can change what it records is
[review-only](#what-stays-review-only).

### 7. Two viewers share only on the authority's proof

The first draft compared two streams' access keys. Those keys are cache keys
for one policy table: the same positions name different policies after a table
change, and they say nothing about body redaction or inputs filtered for one
owner. Sharing needs the authority to say so, about the actual content.

When the authority composes a session's delivery it holds both the unfiltered
input and the filtered result. It issues an **equivalence proof** when, for
that delivery, every input a scene reads is identical in the two: the disclosed
document's scene sections, the body table after redaction, the solid sizes, the
field cells, and no input filtered for one viewer among them. The proof is an
object inside the authority's process, bound to the server's activation and to
the policy table it was computed under, and it names the range of the
authority's own deliveries it covers. It is never sent to a recipient.

A window may draw from its destination's own residency only on a frame whose
latched scene was composed from deliveries inside a range its stream holds a
proof for. The check is made for the frame actually bound and again when a job
recorded under it installs, as
[the rendering plan asks](../rendering-research.md#14-treat-a-nested-view-as-the-same-view-at-another-size).
Its pictures carry both routes. Without a proof (a stream that is filtered at
all, a table change in flight, a destination in another process) the window
keeps a residency of its own.

A window that loses its proof keeps its last picture, which was drawn under
one, until its own residency draws. That pause is a wall-clock effect of hidden
content appearing, and [it is named as a limit](#where-the-guarantee-stops).

### What a cut viewer is shown

The view renders again in the first frame latched after the cut. While its new
residency is not ready, or when the stream no longer observes, the view has no
output, and the screen that shows it draws the session source's authored
`Fallback` colour, which is what a screen at the nesting limit shows today. How
a withheld screen looks beyond a colour is the author's.

**The display is told.** Returning a stand-in image from the runtime does not
put a frame on the display, because a present can be skipped. The runtime's
frame result therefore says when the frame must replace what is shown, and the
presenter owes one of two things at the next presentation: the authorised frame
through the encode, or a clear of the back buffer, which needs no pipeline and
so cannot wait on the encoder. Until one of them has been presented the old
surface is not presented again.

**Meshes.** A mesh found by its exact geometry is a function of content the new
delivery names, and reusing it shows nothing the viewer was not delivered. Two
other paths draw a mesh that is not that: `SdfMeshLibrary.TryTakeForm` answers
a dynamic form with a mesh baked at another pose, possibly for another route,
and a group whose geometry changed draws its old mesh while the new one bakes.
Each such mesh is fenced to the route it was baked for. A new span draws the
floor or the fallback until its own bake lands. Whether a form can match across
different hidden topology was not shown either way; the fence does not depend
on the answer.

**Sound.** The mixer plays the operator's presentation. Its sources are wired
to the boot client, and no session mirror feeds it, so no session's sound is
cut today. Before a session's sound is accepted, its source, scheduled cues,
history and queued samples must be owned by the stream's generation, with the
cut taking effect at the next mixer block after ingress accepts it.

### Where each kind of product sits

| Product | What holds it |
|---|---|
| A view's picture, and every picture that showed it | The carrying value and the one filtered read |
| Stored contents, cadence and checkpoints of a node | The withdrawal reset |
| Per-instance package state and late completions | Generation-owned cells |
| Screen light and environment light | Labelled publications, neutral until re-reduced |
| Irradiance probes | They hold no screen light. A session's own probe volume belongs to its residency |
| A residency, its frame source and the jobs they started | The holder keyed by the route; late work is dropped at the latch |
| A baked mesh | Exact reuse is by content; form and stand-in reuse are fenced to the route |
| An export, a probe's acquisition, a comparison hold | The label of the exact slot or frame, checked at each read |
| The frame on the display | The presenter's owed replacement or clear |

## What enforces each part

| Part | Enforced by | A new caller that forgets |
|---|---|---|
| A record names its stream and generation | The compiler: required on every `IClientSink` content member | Does not compile |
| A cut is decided once | One door: the cursor is the only writer of the generation | Has no other place to write it |
| A stale or foreign record cannot move a mirror | One door: ingress is the only writer of a mirror's span | Has no other place to write it |
| A frame decides one way | A type: decisions take the latch, and a span exposes no live answer outside ingress | Cannot ask a span directly |
| What is made from a scene dies with its route | One door: the holder is the only maker of a frame source and a residency over a mirror | Has nothing to construct them from |
| Content is not separated from its label | A type: the carrying value exposes content only through the latched read | Cannot obtain the handle |
| A forgotten label is refused | A type: the default label is unclassified | Is shown the stand-in |
| An output is read through the filter | A type boundary: the records are private to the filtering type | Cannot index the records |
| A node's retained state is reset as a whole | One door: the withdrawal reset | Is not asked |
| A package's per-instance state ends with its generation | A type: the cell is issued per generation | Gets an empty cell |
| A late completion cannot publish | One door: the runtime compares the completion's generation | Is dropped |
| Shared lighting is read through its label | One door: the tables' accessor is the only binder of those buffers | Has no buffer to bind |
| Sharing needs a proof | A type: a shared view is constructed from a proof and nothing else | Does not compile |
| The display replaces a cut frame | One door: the presenter's owed replacement | Is not asked |

## What stays review-only

These hold by review and nothing else, and the build names each in the code it
touches.

1. **A source that mints a public label for content that is not public.** The
   sources that may mint one are listed in one place: devices, uploads, the
   desktop and camera captures, the stand-ins.
2. **State a package keeps outside a generation-owned cell or a labelled
   publication that can change what it records:** a field of a factory or a
   residency, a captured closure. The recorder API cannot see it.
3. **The authority's comparison for an equivalence proof covering every input a
   scene reads.** A new scene input owes a term in the comparison. Until the
   scene a mirror hands out is one type whose members the comparison walks,
   this is a list kept by hand.
4. **The cursor's loss rules covering every filtered channel.** A new filter in
   a sink owes a loss rule.
5. **Engine code that stores a raw handle it was legitimately given in a field
   of its own.**
6. **What the fence around form and stand-in meshes must cover,** until the
   question of a form matching across hidden topology is settled.

## Where the guarantee stops

- **Pixels that have left the engine.** A screenshot file, a recorded clip, the
  operating system's compositor, a capture device, and a remote client's own
  memory. A remote viewer was sent the content; a hostile client keeps it.
- **One frame.** A cut that ingress accepts after a frame latched takes effect
  at the next latch. The latched frame is presented.
- **A cut the viewer's process was not told of.** A lost connection closes the
  span at once, or after the lease the authority granted. Inside a lease the
  viewer may show content the authority has since cut.
- **What the stream is delivered.** That is [the disclosure
  plan](../disclosure.md)'s. Body disclosure has not joined the recipient view
  ([its open item](../disclosure.md#open-items)); until it does, bodies follow
  the body rule above and a body-policy change cuts.
- **The shared display.** The boot world's own stream is the operator's and is
  unfiltered by design. Its span does not end.
- **Wall time.** A window that loses its proof holds its last picture while its
  own residency builds, and a cut view shows its fallback while its residency
  rebuilds. How long either takes is a timing channel of the kind the
  disclosure plan already places outside its guarantee. The first one is
  driven by hidden content appearing. Closing it means a window never shares,
  which costs a residency per window and the seamless crossing.
- **The exact-content mesh cache.** That a mesh is already baked is a timing
  channel. Who may fetch a shared product by name is the disclosure plan's
  [second boundary](../disclosure.md#presentation-and-derived-product-disclosure).
- **Sound,** until a session can be heard.

## The design review's findings, answered

| # | Finding | Closed by |
|---|---|---|
| 1 | Sharing compares cache keys across disclosure contexts | Part 7: sharing only on an authority-issued proof about the actual scene inputs, bound to the activation and policy table, checked for the frame bound and at install; separate residencies without one |
| 2 | Effective access can shrink under fixed policy answers | [What counts as a cut](#what-counts-as-a-cut): a cut is the loss of delivered content from a part that can withhold, zone membership included. The earlier ruling on the card is reversed; deletion in a whole-read part and body membership are stated as the losses that keep history |
| 3 | Replacing the issued object cannot make outside state fail closed | Part 6: generation-owned cells, the generation carried by late completions, labelled publications. The fail-closed claim is withdrawn, and state outside these is review-only item 2 |
| 4 | The stamp discloses hidden policy structure | Part 1: an opaque stream identity and a generation on the wire, the generation computed from delivered content the recipient could see. The proof and the cache key stay in the authority. Step 1 compares whole transcripts |
| 5 | A mirror gate does not order remote cutover or frame publication | Part 2: ingress bound to the admitted stream and rejecting stale records, a span closed on terminal, loss or replacement, every generation primable alone, one latch per frame with a stated effect at the next latch, late work carrying its latch |
| 6 | A required label argument accepts missing provenance | Part 4: content and label as one value, unclassified refused, one writer including the capture conversion, bindings and holds validated at each use, a span minted only by its mirror's ingress |
| 7 | Screen lighting lives outside the view's package object | Part 6: each lighting publication carries its inputs' spans, the tables' one accessor checks it for the direct binding too, neutral lighting until a current reduction is queued ahead of its readers |
| 8 | Exports and comparisons are consumed before re-entry | Part 6: the label on the exact slot and frame, checked at a probe's acquisition and at every comparison read including the measure and the echo; a cut tears down probe bindings; late captures refused |
| 9 | A stand-in does not guarantee a new swapchain frame | [The display is told](#what-a-cut-viewer-is-shown): the presenter owes a replacement or a clear that does not wait on the encoder, and holds the old surface back until one is presented. Step 8 checks every presentation opportunity |
| 10 | The migration removes guards before their replacements own the paths | [The build order](#the-build-order): every step adds its protection beside the old guard, and a guard is deleted only in the step whose replacement covers all its paths; mirror and holder switch together per route; sharing is conservative until the proof exists |
| 11 | Infinity and nested views need the receiving route's whole scope | Part 3: the holder and every label are keyed by the whole route, latched with the scene; reuse on another route needs that route's check; child inputs are composed per view |
| 12 | Mesh reuse is broader than exact geometry | [Meshes](#what-a-cut-viewer-is-shown): exact reuse kept; form and stand-in reuse fenced to the route. The cross-topology question stays unconfirmed and is review-only item 6 |
| 13 | A primer can retain an earlier pose | Part 2: a new span starts with a new pose table whose first snapshot seeds both endpoints. The verdict on the security review's finding is corrected above |
| 14 | Audio needs a stated boundary | [Sound](#what-a-cut-viewer-is-shown): the mixer is operator presentation today; the rule a session's sound must meet is stated, and it is a limit until then |
| 15 | Clearing history flags can leave a cadence-held pass valid | Part 5: the withdrawal reset invalidates storage, cadence, aliases and checkpoints as one generation change and keeps slot and attachment indexing |
| 16 | Headless checks cannot observe session residency | The build order: stamp and graph checks are device-free; residency and bind checks run on the offscreen renderer with a positive control; the display check uses a window |

## The security review's findings, answered

| Finding | What closes it |
|---|---|
| 1. Whole-replica test reads only the body policy | Step 1 tightens the test at once; step 6 replaces it with the proof (part 7) |
| 2. A retiring view skips the withdrawal | The label is in the consumer's own record and in its retained bindings, not on an edge (part 4) |
| 3. Mirror reuse; a replaced session keeps the residency | A new session is a new stream: the mirror and its pose table start empty and the holder remakes the residency (parts 2 and 3) |
| 4. Delivery and settle race | The counters and the live policy read are deleted; cuts are ordered by ingress and the frame latch (part 2) |
| 5. Node history survives | The withdrawal reset (part 5) |
| 6. The old frame stays on the display | The presenter's owed replacement (what a cut viewer is shown) |
| 7. Lighting reduced from a withdrawn screen | Labelled publications behind one accessor (part 6) |
| 8. Maximum of epochs | Deleted. A label is a set of routes, each with its own spans |
| 9. Infinity source, export ring, comparison hold, meshes | The route's holder (part 3), labels on slots and holds (part 6), the mesh fence |

## Options weighed

- **Fix each finding at its cause.** The security review gives nine smallest
  fixes. Each is correct, and each is one more guard of the kind that failed.
  Their intents are all present in the steps below.
- **Put the generation in the view's name.** A cut view would become a new
  instance. This covers the view and nothing that read it.
- **Render a restricted viewer in a separate runtime.** No image would be
  shared, so nothing could cross. This is the strongest boundary and the right
  shape if pictures are ever rendered on a server for a remote viewer, where
  the frames are the delivery. For screens inside one presentation it gives up
  sharing a residency between a window and the world beyond it. The carrying
  value does not block it: a picture from another runtime enters as an external
  output with its label.
- **One carrying value on the graph's existing propagation (chosen).**

At the scale Puck aims for, each zone and each server is a world and a client
shows several through portals at once. Streams are per viewer and world, and a
label holds as many routes as one picture has streams: a handful. The cut rules
matter most there. Interest management changes what a stream holds on almost
every tick and cuts nothing, and remote streams are the ordinary case, which is
why the ordering rules of part 2 are part of the door and not a later addition.

## The build order

Every step adds its protection beside the guard it will replace. A guard is
deleted only in the step whose replacement owns every path the guard covered,
and each step lists the withdrawal cases it must still pass before it lands.
Three kinds of check are used, because a headless World marks session mirrors
unread and installs no renderer: device-free checks (transcripts, laws),
offscreen-renderer checks for anything about a residency or a bind, each with a
positive control that the same route draws before the cut, and windowed checks
for the display. The second and third kinds are GPU legs the lead schedules.

1. **The stream identity, the generation and ordered ingress; sharing made
   conservative.** The cursor keeps delivered images and decides cuts by the
   rules above. Every content record carries the opaque identity, the
   generation and an ordinal. Ingress applies its rules and a lost connection
   closes the span. The whole-replica test is tightened now, so that the worst
   finding does not wait: a window shares only when its view reads every row
   whole and its field, placement and size projections are unfiltered. Nothing
   is deleted.
   *Check, device-free:* the disclosure harness (`WorldDisclosureRun`) compares
   whole transcripts, with record counts, lengths and control records, across
   variants that differ only in hidden state, including a hidden policy added
   and one removed: equal. On `worlds/debug/shedding-disclosure.puck` a card a
   spectator read moves to a zone it cannot read: one generation step. A body
   leaves an interest radius: none. A law replays a record of an earlier
   generation and a record of another stream into a mirror: both rejected, the
   mirror unchanged. A law drops the connection: the span closes.
   *Built,* except the authored lease: `WorldDeliveryCursor` decides cuts and
   issues `WorldStreamStamp`s, `IClientSink` carries them and `DeliverCut`,
   `WorldStreamIngress` orders a mirror's records, a remote observer closes
   its span when its session ends, and `WorldRecipientView.ReadsEverything`
   tightens the whole-replica test. The card and ingress checks are
   `StreamGenerationLawTests`; the hidden-policy variants are
   `disclosure-probe`'s `one` (added) and `removed`. The engine's default
   lease of none is in force; a lease an admission grants is
   [an open question](#open-questions-and-their-owners).
2. **The carrying value, the one writer and the filtered read, beside the
   existing withdrawal.** The taint flag becomes the label; content and label
   become one value at external outputs, bindings, holds and package reads; the
   capture conversion writes through the one writer; the frame latch exists and
   decisions take it; a node validates held bindings when it records. A
   session's route comes from today's `WorldViewerAccess`, which replaces the
   span wherever its epoch moves now. `IRenderGraphInstances.Withdraw`, its
   edge walk and `IRenderGraphPackageFactory.OnOutputsForgotten` stay in
   force.
   *Check, device-free, beside `RenderGraphRuntimeLawTests`:* a producer cut
   and removed by a reconfiguration in the same frame leaves no reader its
   image, with a consumer whose replacement graph is still building; an
   unclassified image is refused at a bind; a held binding whose span ended is
   replaced when its node next records; a cut accepted between the latch and
   the return changes nothing in that frame and everything in the next. Every
   law the withdrawn lane added still passes.
3. **The withdrawal reset, generation-owned cells and labelled publications.**
   The reset of part 5; the owners of part 6 moved as its table lists; exports,
   probes and comparison holds labelled and checked at each read. Only now are
   `IRenderGraphInstances.Withdraw`, the edge walk,
   `ShaderPipelineRenderNode.WithdrawPublication` and
   `IRenderGraphPackageFactory.OnOutputsForgotten` deleted, with the epoch
   table in `WorldViewGraphHost.BindAccess`, `WorldViewAccess.Join` and
   `WorldViewAccess.Retired`.
   *Check, device-free:* a feedback image written before a cut reads
   uninitialized after it; a pass held by its cadence over an intermediate
   written before the cut records again; a deferred hand-over settled after the
   cut restores nothing; a readback recorded before the cut and completed after
   publishes nothing. *Check, offscreen:* a residency that outlives a child
   screen's cut, with the old reduction still in flight, lights its room
   neutrally until the new reduction is submitted; a probe attached to a
   camera's export ring samples nothing of the pre-cut slot; a comparison
   measure of a hold taken before the cut is refused.
4. **The mirror per span and the holder per route, switched together, one route
   kind at a time.** First session screens, then infinity layers and their
   children. For each kind the mirror's scene, its new pose table and the
   holder land in one change, and a route whose replacement residency is not
   ready shows its fallback. When both kinds have switched, `WorldViewerAccess`,
   `WorldSessionRoute.SettleAccess`, the binder's `ContentAccess` and delivery
   mark, `WorldScreenBinder.WithdrawOwnSource`,
   `WorldScreenBinder.WithdrawInfinityResidency` and the eager source in
   `WorldScreenBinder.CreateInfinity` are deleted.
   *Check, device-free, beside `WorldScreenBinderLawTests`:* re-admit into the
   same mirror with a body at an index the earlier session held active, with an
   index it withheld, and with a seeded route at a low tick: neither pose
   endpoint of the new span comes from the old one. *Check, offscreen:* a
   parent session is cut while its child still observes: the child's view is
   remade; two equal base scenes with different child routes each draw their
   own children.
5. **The presenter's owed replacement.**
   *Check, windowed, both backends:* with the encode forced to rebuild at the
   cut, every presentation opportunity from the cut until the replacement
   either presents the replacement or the clear; the skipped-present counter
   and a capture of the back buffer agree.
6. **Sharing by proof.** The authority issues proofs; a shared view is
   constructed from one; the frame bound and each install check it. Deletes
   `WorldSessionObservation.DisclosesEverything` and step 1's tightened test.
   *Check, offscreen:* in the debug world the painting wing's windows still
   share (the residency count is what it was), with the positive control that
   each draws. A restricted placement added to the destination by
   `world.reload` while a window is shown: the window's residency count rises
   by one and its capture does not show the placement. A policy table replaced
   while a delivery is queued: no frame is drawn from the shared residency
   under the old proof.
7. **The mesh fence.**
   *Check, offscreen:* a cut while an old bake completes and a new dynamic form
   is requested draws the floor or the fallback, never the earlier mesh.
8. **The picture checks the disclosure plan names.** Narrow a full view to a
   restricted one on a frame its refresh is held, and cut one viewer while a
   second keeps the shared residency, each with a detail only the full view
   shows and a GPU job submitted before the cut finishing after it.
   *Check, windowed, both backends:* read at the display, at every presentation
   opportunity from the cut, since a capture of graph output cannot say what
   the swapchain held.

Steps 1 and 2 are independent. Step 3 needs step 2, step 4 needs steps 1 and
3, step 5 needs step 2, step 6 needs steps 1 and 4, and step 7 needs step 4.

## What it deletes

Each item is deleted in the step named for it above, never before.

- On the authority's side: `WorldSessionObservation.DisclosesEverything` and
  the second policy copy it stands for.
- On the viewer's side: `WorldViewerAccess` and `WorldSessionAccess`,
  `WorldViewAccess` with its join and its retired value, the generated-name
  grammar in `WorldScreenBinder.AccessOf`, the binder's `ContentAccess` and
  delivery mark, `WorldScreenBinder.WithdrawOwnSource`,
  `WorldScreenBinder.WithdrawInfinityResidency`, and the epoch table and
  withdrawal call in `WorldViewGraphHost.BindAccess`.
- In the render graph: `IRenderGraphInstances.Withdraw`, the withdrawal list
  and edge walk, `IRenderGraphPackageFactory.OnOutputsForgotten` and its one
  implementation, and the separate capture-taint bookkeeping, which becomes one
  state of the label.

## Systems considered

Searched before proposing a generation or a key.

- `WorldDeliveryCursor.Sequence` counts a stream's deliveries and keeps
  counting across a withheld span, so it cannot say that access shrank. The
  cursor is the right owner for the generation, and its row identities are the
  start of the delivered images a cut is computed from.
- `WorldDocumentVersion` names an install by activation and ordinal. Its
  activation bounds what an equivalence proof may be compared within.
- `WorldSessionMirror.DefinitionRevision` and `WorldSessionMirror.SnapshotRevision`
  are the viewer-side counters the binder's guard reads now.
- `WorldDisclosureImages.Audience` is a cache key for one policy table. The
  first draft reused it as an access key on the wire; the design review showed
  that it leaks the table's size and does not survive a table change, so it is
  kept as what it is.
- `StateArena.DisclosureRevision` is one number for every recipient and moves
  for hidden changes, so a recipient must not be shown it.
- The render graph's taint (`RenderGraphExternalOutput.Tainted`,
  `IRenderGraphPackageFactory.TaintOf`, `SdfWorldTables.ProjectTaintedSourcesAgain`)
  is the plumbing the label rides, and the capture repair is the precedent for
  re-reducing lighting from its sources.
- `RenderGraphSlotResults` is the precedent for a result that waits on a slot,
  and gains the generation.
- The session source's `Fallback` colour is the authored answer to "what does a
  screen with nothing to show draw".

No per-stream disclosure generation existed on either side; step 1 added it
as `WorldDeliveryCursor.Generation` and `WorldStreamIngress`.

## Open questions and their owners

- Whether a form can match across different hidden topology in the mesh
  library. The step 7 lane settles it with a constructed pair and either keeps
  or narrows the fence.
- Whether the retained set should lose a route that the node's stored contents
  can no longer hold, so that a view which stopped showing a screen long ago is
  not restarted when that screen's session is cut. The step 3 lane measures how
  often that restart happens on the benchmark route before adding it.
- The lease an admission grants a stream after a lost connection. The default
  of none is built: a remote observer's span closes as its session ends, which
  blanks a neighbour's view on a network hiccup. The authored surface is not:
  a `lease` member on an admission row, carried to the observer when the
  stream is admitted, holding the closed span showable that long, and closed
  at once when the next session's first record arrives. Its first consumer is
  a remote neighbour, which nothing in the debug world runs yet; the lane
  that next works on remote observation builds it.
- Whether the scene a mirror hands out can become one type that the proof's
  comparison walks, which would retire review-only item 3. The step 6 lane
  decides when it sees the comparison's size.

---

[Disclosure](../disclosure.md#presentation-and-derived-product-disclosure) · [Plans](../README.md) · [Open items](../open-items.md#security-review) · [Rendering research](../rendering-research.md)
