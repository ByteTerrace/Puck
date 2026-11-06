namespace Puck.Cli;

/// <summary>
/// Reads what a running World's command line says about its devices, for the host's view of the processes already
/// running. A World this CLI starts passes <c>--devices none</c> unless the owner asked to play in it
/// (<see cref="WorldLaunchRequest.CommandLine"/>), so a press on the owner's pad never moves a test run.
/// </summary>
public static class WorldDevices {
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
