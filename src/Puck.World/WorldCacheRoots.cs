namespace Puck.World;

/// <summary>
/// The device caches a boot reads and fills: the compiled worlds it derives, the creation bakes its presentation makes,
/// the <c>.puck</c> compiles its sources produce, and the meshes its presentation bakes. Each holds a pure function of its inputs and the build, never of
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
    /// <param name="meshes">The directory the baked-mesh store keeps its entries in, made absolute here.</param>
    /// <exception cref="ArgumentException"><paramref name="compiledWorlds"/>, <paramref name="bakes"/>,
    /// <paramref name="compilations"/> or <paramref name="meshes"/> is <see langword="null"/>, empty or white
    /// space.</exception>
    public WorldCacheRoots(string compiledWorlds, string bakes, string compilations, string meshes) {
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: compiledWorlds);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: bakes);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: compilations);
        ArgumentException.ThrowIfNullOrWhiteSpace(argument: meshes);

        Bakes = Path.GetFullPath(path: bakes);
        Compilations = Path.GetFullPath(path: compilations);
        CompiledWorlds = Path.GetFullPath(path: compiledWorlds);
        Meshes = Path.GetFullPath(path: meshes);
    }

    /// <summary>Gets the absolute directory creation bakes are kept in.</summary>
    public string Bakes { get; }
    /// <summary>Gets the absolute directory held <c>.puck</c> compiles persist in.</summary>
    public string Compilations { get; }
    /// <summary>Gets the absolute directory derived compiled worlds are written to.</summary>
    public string CompiledWorlds { get; }
    /// <summary>Gets the absolute directory the baked-mesh store (<see cref="Puck.SdfVm.SdfMeshStore"/>) keeps its
    /// entries in.</summary>
    public string Meshes { get; }
}
