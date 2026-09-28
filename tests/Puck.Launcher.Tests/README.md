# Puck.Launcher.Tests

This xUnit suite checks [Puck.Launcher](../../src/Puck.Launcher/README.md) host
controls and release handling. It covers fixed-step pumping, backend switching,
console sessions, release manifests and signatures, content staging, update
application, rollout selection, and the
[stub's](../../src/Puck.Launcher.Stub/README.md) rollback policy.

`OverlayGlyphPackTests` also checks the fixed-grid text consumer: equal-sized
cells pack unchanged, half-integer texel-center bounds include both endpoints,
and variable-size, unsupported fractional, and out-of-image cells are refused,
including appended glyphs. `OverlayGlyphCacheTests` covers persistence across
fresh loaders, alternating icon repertoires, source invalidation, malformed
cache recovery, and admission of the shipped atlas.

`WindowPumpFaultLawTests` and `LauncherHostRunLawTests` run a real windowed
launcher host over fakes: a window that is already closed, a render node that
produces nothing, and a presenter whose `Activate` throws a chosen failure. No
GPU or display is needed. They check four things. A throwing activation faults
the window loop's task with `PresenterActivationException`. A presenter that
never activated is not asked to drain its device. `GpuDeviceUnavailableException`
exits 2 with the exact unsupported line. Any other loop failure is rethrown
instead of exiting 0. `TeardownAfterFaultLawTests` build the shipped overlay
package, drawn by a graph node, against a device context that behaves like a
renderer that never initialized. Its activation fails with `GpuDeviceUnavailableException`,
and the laws check that the failure still exits 2 with the exact line through
that real teardown. An overlay that never produced a frame never reads the
device handle, and a throwing teardown step neither replaces the fault nor
skips the steps after it. A hosted service whose construction finds no device
also exits 2.

## Verification

From the repository root, run in PowerShell or another shell:

```powershell
dotnet test tests/Puck.Launcher.Tests/Puck.Launcher.Tests.csproj -c Release
```

A successful run reports passing tests and exits with code zero. Release
fixtures use temporary staging state and test signing material. This evidence
covers the exercised library and installation policies, not a complete
platform windowing or deployed-release verification.

## Documentation

📚 [Puck.Launcher](../../src/Puck.Launcher/README.md) · 🛠️ [Development](../../docs/development/README.md)
