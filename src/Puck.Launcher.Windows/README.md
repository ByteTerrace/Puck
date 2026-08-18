# Puck.Launcher.Windows

The Windows GPU-host block a composition root shares across windowed entry
points (`Puck.World`).

## `WindowsPresentationRegistration.AddWindowsHostedPresentation(services, hostsOnDirectX)`

Registers, in order:

1. `Puck.Platform.AddPlatformWindowing` — the neutral display probe,
   platform support, and window factory.
2. `Puck.Platform.Windows.AddWindowsPlatformWindowing` — the Win32
   clipboard and window backend.
3. `Puck.Memory.AddPuckAllocator` — the unmanaged allocator behind the
   Vulkan backend's `IAllocator` dependency (harmless on Direct3D 12).
4. The launch-selected presenter: `AddDirectXPresenter` or
   `AddVulkanPresenter`, plus its `SurfacePresenterDescriptor`. Only the
   selected backend enters the service provider, so the neutral compute
   services, device, presenter, and shader format can never disagree.

Does **not** call `AddLauncherTerminal` or `AddBackendSwitcher` — those stay
Engine-services calls the composition root makes itself, since this
Presentation-row (`build/Architecture.props` rank 3) project cannot
reference `Puck.Launcher` (rank 2) without the upward edge `PUCKARCH001`
refuses. The composition root calls `AddLauncherTerminal()`, then this
method, then `AddBackendSwitcher(...)` — three calls where a composition
root previously duplicated one private block per entry point.
