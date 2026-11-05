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

    /// <summary>Whether a World started with these arguments opens a GPU device: it does unless it boots headless
    /// (<c>--headless</c> alone or followed by a value other than <c>false</c>) or runs a test batch
    /// (<c>--test-batch</c>), which boots every scenario headless.</summary>
    /// <param name="arguments">The World's command-line arguments.</param>
    /// <param name="start">The index of the first argument to read.</param>
    /// <returns><see langword="true"/> when the World opens a device.</returns>
    public static bool OpensDevice(IReadOnlyList<string> arguments, int start = 0) {
        var opens = true;
        var decided = false;

        for (var index = start; (index < arguments.Count); index++) {
            if (string.Equals(a: arguments[index], b: "--test-batch", comparisonType: StringComparison.OrdinalIgnoreCase)) { return false; }
            if (!decided && string.Equals(a: arguments[index], b: "--headless", comparisonType: StringComparison.OrdinalIgnoreCase)) {
                decided = true;
                opens = (((index + 1) < arguments.Count) && string.Equals(a: arguments[index + 1], b: "false", comparisonType: StringComparison.OrdinalIgnoreCase));
            }
        }

        return opens;
    }
    /// <summary>What a World started with these arguments is, for the host admission: a test batch (<c>--test-batch</c>) is
    /// a <see cref="HostStartKind.ReproduceReplica"/>, any other World that opens a device
    /// (<see cref="OpensDevice"/>) a <see cref="HostStartKind.WindowedWorld"/>, and the rest a
    /// <see cref="HostStartKind.HeadlessWorld"/>.</summary>
    /// <param name="arguments">The World's command-line arguments.</param>
    /// <param name="start">The index of the first argument to read.</param>
    /// <returns>The World's kind.</returns>
    public static HostStartKind StartKind(IReadOnlyList<string> arguments, int start = 0) {
        for (var index = start; (index < arguments.Count); index++) {
            if (string.Equals(a: arguments[index], b: "--test-batch", comparisonType: StringComparison.OrdinalIgnoreCase)) { return HostStartKind.ReproduceReplica; }
        }

        return (OpensDevice(arguments: arguments, start: start) ? HostStartKind.WindowedWorld : HostStartKind.HeadlessWorld);
    }
}
