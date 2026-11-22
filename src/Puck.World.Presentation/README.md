# Puck.World.Presentation

Puck.World.Presentation holds the frame's side of the World: the types a
produced frame runs, and the values the authority publishes for them. Its
architecture profile (`build/Architecture.props`) forbids `Puck.World.Server`
and `Puck.World.Machines`, so a frame type that names the authority
(`WorldServer`, `WorldPopulation`, `WorldBody`, a machine host) fails the build
with `PUCKARCH004` instead of keeping a reference that a tick on another thread
writes.

## What the frame reads

`WorldPresentationFeed` holds four immutable values, published together by
reference:

- `WorldTickRecord`: the boot world's completed tick, its installed document
  version, its body counts, its in-session history window, each local seat's
  place, and the live document's authored commands.
- `WorldMachineTable`: each running world's `WorldMachinePresentation`, the
  record its machine host captures whole (`IWorldMachineHost.CapturePresentation`):
  its running instances' video outputs, audio streams and faults, its instance
  revision and its presentation clock (`WorldMachinePresentation.PresentedTicks`).
- `WorldObservationTable`: every session a world's screens and infinity layers
  observe (`WorldObservedSession`: its source, destination instance, refusal,
  mirror and current admission, with the scene proof checked against the
  instance running under its name and the window endpoint it may render), each
  running instance's render envelope, and the boot endpoint. A record keeps its
  identity token across collects, so a screen tells a re-pointed session from a
  re-admitted one by token, never by reference to an authority object.
- `WorldDiagnosticCopy`: the diagnostic view, how it is drawn, and the report
  the overlay draws.

[`Puck.World`](../Puck.World/README.md) composes the feed and copies the values
in `WorldPresentationCollect`, which the seat post-step runs once a host step,
after every instance has stepped and on the pump inside the boundary window. It
also runs as the World boots and after a timeline is restored outside a step.
Each value is reused while what it copies has not moved.

## What lives here

The screen binder (`WorldScreenBinder`), the render root (`WorldRenderRoot`),
the audio director (`WorldAudioDirector`), the device profile state
(`WorldDeviceProfileState`) and their helpers (captures, pixel conversion, GPU
timing, the inspector, host settings) read the feed and nothing else of the
authority. A view registers its render measurer with the destination's
`WorldRenderEnvelope` from the table; the envelope keeps its measurers as one
immutable array, so a registration from the frame never races the tick that
fits a candidate against it. The admission's projection a measurer applies
(`WorldObservationAdmission.Disclose`) is built by the composition root and run
only by that capacity check. Shared surfaces export through
`IGpuSurfaceExportProvider`, which the root supplies from Direct3D 12, so the
binder names no backend. A tune's synthesizer (`WorldAudioDirector.TuneSources`)
is supplied by the root, which holds the Gaming Brick core.

The other frame-side readers that read the feed rather than the authority:
`WorldIterationClock` (the edit loops' clock), `WorldHudBindingResolver` (the
HUD's bindings), `WorldRenderProbe` (the live render objects and engine
readiness), `WorldDiagnosticOverlay` (which projects the published report and
never starts one), the pointer (`WorldPointer`, `WorldPointerSink`,
`WorldDragMotion`, `WorldSeatViewInput`), the overlay feeds (`WorldOverlayFeed`,
`WorldCursorFeed`, `WorldWheelFeed`, `WorldOverlayFacts`,
`WorldBindingBarControl`, `WorldIconTable`, `WorldExplainPick`),
`WorldSeatCameraCommandModule`, and the seat argument helpers
(`SeatCommandArgs`, `WorldArgs`). The document framing a seat is the one its
claim routes it to (`WorldSeatAuthorityRouter.DefinitionOf`).

The console verbs that read machine state (`screen.state`, `screen.links`,
`screen.peek`, a magazine's selector, `body.engage`'s auto-insert) read the boot
authority's machine host directly, in the control phase.
