using Puck.Abstractions.Presentation;
namespace Puck.Launcher;

/// <summary>
/// A named graphics-backend presenter the composition root contributes so the generic <see cref="BackendSwitcher"/> can
/// enumerate the available backends without naming any of them. Each backend (e.g. Vulkan, Direct3D 12) registers one;
/// <see cref="LauncherServiceRegistration.AddBackendSwitcher"/> picks the preferred one and fronts the rest.
/// </summary>
/// <param name="Name">The backend's display name (e.g. <c>"vulkan"</c>, <c>"directx"</c>).</param>
/// <param name="Presenter">The backend's surface presenter.</param>
public sealed record SurfacePresenterDescriptor(string Name, ISurfacePresenter Presenter);
