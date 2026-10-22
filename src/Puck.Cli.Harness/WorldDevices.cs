namespace Puck.Cli;

/// <summary>
/// How a verb keeps a World it boots off the machine's physical input devices. The owner's pads, keyboard and mouse
/// drive the owner's own World only: a World a verb starts for an agent, a gate or a benchmark opens none of them, so a
/// press on the owner's pad never moves a test run. Scripted input (stdin, <c>player.signal</c>, schedule rows) is
/// unaffected.
/// </summary>
public static class WorldDevices {
    /// <summary>The World arguments (<c>--devices none</c>) that open no gamepad and drop every OS key, text and pointer
    /// event before a seat sees it.</summary>
    public static string[] NoneArguments { get; } = ["--devices", "none"];
}
