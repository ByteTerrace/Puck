# Puck.Commands.Tests

This suite checks the command registry, input routing, command values and
wire arguments, binding profile compilation and JSON, guided sessions, wheel
gestures, text command sessions, and packaging surface. The law tests cover
the binding scale and channel contracts alongside the ordinary boundary and
regression tests.

## Running

```powershell
dotnet test tests/Puck.Commands.Tests/Puck.Commands.Tests.csproj -c Release
```

## Coverage

Each file pins one area, so a failure names the contract that moved.

Concurrent producer tests await worker failures on the test thread and perform
one final drain after every producer finishes. A missing signal fails the
exactly-once assertions immediately; it does not trigger a long empty drain loop.
Their deadlines are failure bounds, with cancellation shared by the producers.

- **Dispatch and text.** `CommandRegistryTests` covers the text-dispatch
  surface, the wire-native fast path, and its rejection wording;
  `CommandRegistryBoundaryTests` covers the per-entry exception boundary
  `ApplySnapshot` promises. `TextCommandSourceTests` drives the drain and its
  hold gate, `TextCommandSessionTests` the per-session read-after-write barrier.
  `CommandSettlementTests` covers final verdict ordering, quiet mode, cancellation,
  observer failures, input overflow, and router disposal.
  `CommandArgsTests` and `WireArgsTests` pin argument parsing and the zero-copy
  trailing-token view; `CommandEchoTests` pins the echo grammar's quoting.
- **The router.** `InputRouterTests` covers held-command edge logic over
  physical signals, `CommandModalityTests` per-slot maps, `HeldOrderTrackerTests`
  and `HeldCommandReleaseLawTests` press ordering and a held verb's two edges.
  `InputRouterHardeningTests` pins the behaviors an audit found unproven,
  `InputRouterFocusExemptTests` what a focus-exempt signal may and may not do,
  `InputRouterMomentaryReleaseTests` the one-release-per-command rule, and
  `InputRouterReleaseOrderTests` the total order of every synthesized release.
  `InputRouterConcurrencyTests` drives the headline thread-safety claim rather
  than asserting it. `CommandBufferTests` covers the borrowed per-tick view.
- **Bindings.** `PagedInputBindingsTests` covers pages, chords, modifiers, and
  wheels end to end; `BindingProfileCompilationLawTests`,
  `BindingProfileValidationTests`, and `BindingRowMemberLawTests` the compiler's
  laws and structural refusals; `BindingChannelLoweringLawTests` and
  `BindingChannelScaleLawTests` the channel path; `BindingVocabularyCheckTests`
  the vocabulary gate, including its tolerance for documents it exists to
  refuse; `BindingProfileJsonTests` the one wire shape, from a whole document's
  lossless round trip down to an enum refusing a numeric token.
  `BindingSessionTests` and `BindingSessionPlanReservationTests` cover guided
  rebinding and what a plan must reserve. `BindingWheelGeometryTests`,
  `BindingWheelGestureStateTests`, `BindingWheelGraceTests`, and
  `BindingWheelSectorTextTests` cover radial geometry, gesture state, the
  grace window, and a sector's authored text.
- **Source mapping.** `SourceMappingLawTests` maps known points on a tilted
  screen and an off-centre pane to known source pixels through every layout,
  fit, crop and warp, refuses a warp with no inverse as an input path and a
  document's passthrough source, and holds a hit bit-identical across
  evaluations and threads. `SourcePointerCommandLawTests` carries a pointer
  ray through the router's snapshot to the same pixel on every replay.
- **The published surface itself.** `ApiSurfaceTests` and `PackagingTests` pin
  that the snapshot shapes stay internal to construct, that no public member
  carries `[Obsolete]`, a retired-shape name, or a mutable field, and that the
  package identity and the shipped XML documentation survive a pack.

## Documentation

📚 [Commands and input](../../docs/reference/commands.md) · 🛠️ [Contributing to Puck](../../docs/development/contributing.md)
