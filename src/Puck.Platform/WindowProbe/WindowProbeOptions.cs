namespace Puck.Platform.WindowProbe;

public sealed class WindowProbeOptions {
    public bool AutoCloseAfterFirstPaint { get; set; }
    public uint Height { get; set; } = 600;
    public int MaxPumpIterations { get; set; } = 600;
    public int PollDelayMilliseconds { get; set; } = 16;
    public string Title { get; set; } = "Puck Window Probe";
    public uint Width { get; set; } = 800;
    public NativeWindowMode WindowMode { get; set; } = NativeWindowMode.Headless;
}
