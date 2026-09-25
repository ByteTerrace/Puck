namespace Puck.World;

/// <summary>
/// The device caches a boot reads and fills: the compiled worlds it derives, the creation bakes its presentation makes,
/// and the <c>.puck</c> compiles its sources produce. Each holds a pure function of its inputs and the build, never of
/// a run's state, so they live outside the state root and every boot that names the same directories shares them. The
/// desktop World's entry point names the per-user directories (<see cref="Puck.Abstractions.PuckUserDirectory"/>) and
/// nothing else does; the boot registers the roots it was handed and every consumer takes them from there, so a host
/// or test fixture that composes World services carries directories of its own. Immutable.
/// </summary>
public sealed class WorldCacheRoots {
    /// <summary>Initializes a new instance of the <see cref="WorldCacheRoots"/> class. Nothing is created until a
    /// cache writes.</summary>
    /// <param name="compiledWorlds">The directory derived compiled worlds are written to, made absolute here.</param>
    /// <param name="bakes">The directory creation bakes are kept in, made absolute here.</param>
    /// <param name="compilations">The directory held <c>.puck</c> compiles persist in, made absolute here.</param>
    /// <exception cref="ArgumentException"><paramref name="compiledWorlds"/>, <paramref name="bakes"/> or
    /// <paramref name="compilations"/> is <see langword="null"/>, empty or white space.</exception>
    public WorldCacheRoots(string compiledWorlds, string bakes, string compilations) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: compiledWorlds);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: bakes);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: compilations);

        Bakes = Path.GetFullPath(path: bakes);
        Compilations = Path.GetFullPath(path: compilations);
        CompiledWorlds = Path.GetFullPath(path: compiledWorlds);
    }

    /// <summary>Gets the absolute directory creation bakes are kept in.</summary>
    public string Bakes { get; }
    /// <summary>Gets the absolute directory held <c>.puck</c> compiles persist in.</summary>
    public string Compilations { get; }
    /// <summary>Gets the absolute directory derived compiled worlds are written to.</summary>
    public string CompiledWorlds { get; }
}
