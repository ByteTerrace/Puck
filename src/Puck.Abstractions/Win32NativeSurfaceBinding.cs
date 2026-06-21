namespace Puck.Abstractions;

/// <summary>
/// The Win32 windowing-system payload of a <see cref="NativeSurfaceBinding"/>.
/// </summary>
/// <param name="InstanceHandle">The module instance handle (<c>HINSTANCE</c>).</param>
/// <param name="WindowHandle">The window handle (<c>HWND</c>).</param>
public readonly record struct Win32NativeSurfaceBinding(
    nint InstanceHandle,
    nint WindowHandle
);
