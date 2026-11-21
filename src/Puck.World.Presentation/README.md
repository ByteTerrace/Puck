# Puck.World.Presentation

Puck.World.Presentation holds the frame's side of the World: the types a
produced frame runs, and the values the authority publishes for them. Its
architecture profile (`build/Architecture.props`) forbids `Puck.World.Server`
and `Puck.World.Machines`, so a frame type that names the authority
(`WorldServer`, `WorldPopulation`, `WorldBody`, a machine host) fails the build
with `PUCKARCH004` instead of keeping a reference that a tick on another thread
writes.

## What the frame reads

`WorldPresentationFeed` holds three immutable values, published together by
reference:

- `WorldTickRecord`: the boot world's completed tick, its installed document
  version, its body counts, its in-session history window, each local seat's
  place, and the live document's authored commands.
- `WorldMachineTable`: each running world's `WorldMachinePresentation`, the
  record its machine host captures whole (`IWorldMachineHost.CapturePresentation`):
  its running instances' video outputs and faults, its instance revision and its
  presentation clock (`WorldMachinePresentation.PresentedTicks`).
- `WorldDiagnosticCopy`: the diagnostic view, how it is drawn, and the report
  the overlay draws.

[`Puck.World`](../Puck.World/README.md) composes the feed and copies the values
in `WorldPresentationCollect`, which the seat post-step runs once a host step,
after every instance has stepped and on the pump inside the boundary window. It
also runs as the World boots and after a timeline is restored outside a step.
Each value is reused while what it copies has not moved.

## What lives here

The frame-side readers that read the feed rather than the authority:
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

The screen binder, the render root and the audio director still live in
`Puck.World`; each moves here once what it reads of the authority is published.
