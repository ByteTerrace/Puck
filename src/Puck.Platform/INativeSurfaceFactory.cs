namespace Puck.Platform;

public interface INativeSurfaceFactory {
    NativeDisplayKind DisplayKind { get; }

    NativeSurfaceBinding CreateSurfaceBinding();
}
