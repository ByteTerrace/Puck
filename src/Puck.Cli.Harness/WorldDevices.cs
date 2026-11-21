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
    /// <summary>Resolves the directory of the baked-mesh store a World booted with <c>--cache-dir</c> reads and fills.</summary>
    /// <param name="cacheRoot">The <c>--cache-dir</c> value, or <see langword="null"/> for the per-user store.</param>
    /// <returns>The store's full path.</returns>
    public static string MeshStore(string? cacheRoot) => Path.GetFullPath(path: ((cacheRoot is null)
        ? Puck.Abstractions.PuckUserDirectory.Resolve(name: "meshes")
        : Path.Join(path1: cacheRoot, path2: "meshes")));
    /// <summary>Predicts whether the mesh store a World boots with is warm: the store is the <c>meshes</c> folder of its
    /// <c>--cache-dir</c> (the per-user device-cache root when it names none), and a store with no entry in it bakes every
    /// mesh. The prediction is made once, at launch, and the World's own report of baking corrects it
    /// (<see cref="WorldReadiness.Observe"/>).</summary>
    /// <param name="arguments">The World's command-line arguments.</param>
    /// <param name="start">The index of the first argument to read.</param>
    /// <returns><see cref="StoreWarmth.Cold"/> when the store holds nothing, otherwise <see cref="StoreWarmth.Warm"/>.</returns>
    public static StoreWarmth StoreOf(IReadOnlyList<string> arguments, int start = 0) {
        string? cacheRoot = null;

        for (var index = start; (index < arguments.Count); index++) {
            if (string.Equals(a: arguments[index], b: "--cache-dir", comparisonType: StringComparison.OrdinalIgnoreCase) && ((index + 1) < arguments.Count)) { cacheRoot = arguments[index + 1]; }
            else if (arguments[index].StartsWith(value: "--cache-dir=", comparisonType: StringComparison.OrdinalIgnoreCase)) { cacheRoot = arguments[index]["--cache-dir=".Length..]; }
        }

        var meshes = MeshStore(cacheRoot: cacheRoot);

        try {
            return (Directory.Exists(path: meshes) && Directory.EnumerateFileSystemEntries(path: meshes).Any() ? StoreWarmth.Warm : StoreWarmth.Cold);
        } catch (Exception exception) when ((exception is IOException or UnauthorizedAccessException)) {
            return StoreWarmth.Warm;
        }
    }
}
