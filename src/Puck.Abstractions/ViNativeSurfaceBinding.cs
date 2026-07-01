namespace Puck.Abstractions;

/// <summary>
/// The Vi windowing-system payload of a <see cref="NativeSurfaceBinding"/>.
/// </summary>
/// <param name="WindowHandle">The native Vi window handle.</param>
public readonly record struct ViNativeSurfaceBinding(
    nint WindowHandle
);
