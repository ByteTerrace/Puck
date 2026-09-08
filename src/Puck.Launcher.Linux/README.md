# Puck.Launcher.Linux

The Linux GPU-host block, the same shape as `Puck.Launcher.Windows` for the
one backend Linux can serve.

## `LinuxPresentationRegistration.AddLinuxHostedPresentation(services)`

Registers, in order: `Puck.Platform.AddPlatformWindowing` (neutral),
`Puck.Platform.Linux.AddLinuxPlatformWindowing` (the null clipboard and the
Wayland/Xcb window backends), `Puck.Memory.AddPuckAllocator`, and
`Puck.Vulkan.Presentation.AddVulkanHostedPresentation` (presenter, neutral
`IGpuDeviceContext` alias, and `SurfacePresenterDescriptor`). No
`hostsOnDirectX` parameter — this project's closure cannot name
`Puck.DirectX`/`Puck.DirectX.Presentation` by construction, enforced by the
`Puck.Launcher.Linux` exact-equality lane profile in
`build/Architecture.props`. Does not call `AddLauncherTerminal`/
`AddBackendSwitcher` — see `Puck.Launcher.Windows/README.md`'s remarks on
why those stay the composition root's own calls.

## Not yet wired to a shipping composition root

`Puck.World` references this project (part of staying one universal build
for every host OS) and branches to it under `OperatingSystem.IsWindows() ==
false`, but nobody has run that path on real Linux hardware. The property
this project's own build proves — a Linux closure with zero Windows/DirectX
— is a project-boundary proof, not a product-run proof.

## Verifying a build on Linux (WSL2)

The owner has WSL2 available. This proves COMPILATION only — WSL2's `dzn`
(Mesa's D3D12-on-WSL translation layer) is not Vulkan and would prove
nothing about the Wayland/Xcb window paths in `Puck.Platform.Linux`.
Clone under a native Linux filesystem path (`~/src`, never `/mnt/d` or
another Windows-drive mount — cross-filesystem WSL2 I/O is measurably
slower and occasionally denies permissions NTFS never would):

```bash
git clone <repo-url> ~/src/Puck && cd ~/src/Puck
# .NET 10 SDK installed inside the WSL2 distro (not the Windows host install).
dotnet restore Puck.slnx
dotnet build Puck.slnx -c Release
dotnet build src/Puck.Platform.Linux/Puck.Platform.Linux.csproj -c Release
dotnet run --project src/Puck.World -c Release -- --backend vulkan --headless --exit-after-seconds 2
```

The last command is the honest ceiling of WSL2 verification here: a
headless boot, no window. Windowed Linux — `dotnet run --project src/Puck.World
-c Release -- --backend vulkan --exit-after-seconds 2` opening a real Wayland
or Xcb window — is verified only on real Linux hardware (Steam Deck/Steam
Machine, per `docs/agent-guide.md`'s supported GPU floor). That has not
happened; do not claim otherwise.
