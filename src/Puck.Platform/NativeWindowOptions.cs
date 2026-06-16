namespace Puck.Platform;

public sealed class NativeWindowOptions {
    public NativeDisplayKind DisplayKind { get; set; } = NativeDisplayKind.Auto;
    public uint Height { get; set; } = 600;
    public bool HideMouseCursor { get; set; }
    public NativeWindowMode Mode { get; set; } = NativeWindowMode.Headless;
    public bool StartFullscreen { get; set; }
    public string Title { get; set; } = "Puck";
    public uint Width { get; set; } = 800;
}
