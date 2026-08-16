namespace Puck.Abstractions.Presentation;

/// <summary>
/// A named graphics-backend presenter a composition root contributes so a generic backend switch can enumerate the
/// available backends without naming any of them. Each backend (e.g. Vulkan, Direct3D 12) registers one; the switch
/// picks the preferred one and fronts the rest.
/// </summary>
/// <param name="Name">The backend's display name (e.g. <c>"vulkan"</c>, <c>"directx"</c>).</param>
/// <param name="Presenter">The backend's surface presenter.</param>
public sealed record SurfacePresenterDescriptor(string Name, ISurfacePresenter Presenter);
