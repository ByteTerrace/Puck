using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Machines;
using Puck.GamingBricks.Forge;

namespace Puck.World;

/// <summary>
/// Registry and vocabulary check for screen-machine engines (<see cref="IScreenMachineEngine"/>) and their
/// companion cartridge forge compilers (<see cref="ICartridgeCompiler"/>).
/// <para>
/// Individual machine engines and forge compilers register dynamically at composition/initialization time.
/// Document-declared engine keys validate identically across all composition roots via
/// <see cref="IsRegistered(string)"/> and <see cref="CompilesCartridges(string)"/>.
/// </para>
/// </summary>
public static class WorldScreenMachineEngines {
    private static readonly List<IScreenMachineEngine> s_engines = [];
    private static readonly Dictionary<string, ICartridgeCompiler> s_compilers = new(comparer: StringComparer.Ordinal);
    private static readonly Lock s_gate = new();

    /// <summary>
    /// Gets all registered screen-machine engines.
    /// </summary>
    public static IReadOnlyList<IScreenMachineEngine> All {
        get {
            lock (s_gate) {
                return s_engines.ToArray();
            }
        }
    }

    /// <summary>
    /// Gets all registered cartridge forge compilers, keyed by engine id.
    /// </summary>
    public static IReadOnlyDictionary<string, ICartridgeCompiler> CartridgeCompilers {
        get {
            lock (s_gate) {
                return new Dictionary<string, ICartridgeCompiler>(dictionary: s_compilers, comparer: StringComparer.Ordinal);
            }
        }
    }

    /// <summary>
    /// Registers a screen-machine engine and an optional companion cartridge forge compiler.
    /// </summary>
    /// <param name="engine">The engine to register.</param>
    /// <param name="compiler">The optional compiler associated with this engine.</param>
    public static void Register(IScreenMachineEngine engine, ICartridgeCompiler? compiler = null) {
        ArgumentNullException.ThrowIfNull(argument: engine);

        lock (s_gate) {
            var existingIndex = s_engines.FindIndex(match: e => string.Equals(e.Id, engine.Id, StringComparison.Ordinal));

            if (existingIndex >= 0) {
                s_engines[existingIndex] = engine;
            } else {
                s_engines.Add(item: engine);
            }

            if (compiler is not null) {
                s_compilers[engine.Id] = compiler;
            }
        }
    }

    /// <summary>
    /// Registers a cartridge forge compiler by its target engine id.
    /// </summary>
    /// <param name="compiler">The compiler to register.</param>
    public static void RegisterCompiler(ICartridgeCompiler compiler) {
        ArgumentNullException.ThrowIfNull(argument: compiler);

        lock (s_gate) {
            s_compilers[compiler.EngineId] = compiler;
        }
    }

    /// <summary>
    /// Returns whether a document-declared engine key names a registered screen-machine engine.
    /// </summary>
    /// <param name="key">The document-declared engine key.</param>
    public static bool IsRegistered(string key) {
        if (string.IsNullOrEmpty(value: key)) {
            return false;
        }

        lock (s_gate) {
            return s_engines.Exists(match: e => string.Equals(e.Id, key, StringComparison.Ordinal));
        }
    }

    /// <summary>
    /// Returns whether the engine under <paramref name="key"/> compiles authored cartridge documents.
    /// </summary>
    /// <param name="key">The document-declared engine key.</param>
    public static bool CompilesCartridges(string key) {
        if (string.IsNullOrEmpty(value: key)) {
            return false;
        }

        lock (s_gate) {
            return s_compilers.ContainsKey(key: key);
        }
    }

    /// <summary>
    /// Resolves the forge compiler associated with the given <paramref name="engineId"/>.
    /// </summary>
    /// <param name="engineId">The engine id to resolve.</param>
    /// <param name="compiler">The resolved compiler, or <see langword="null"/>.</param>
    /// <returns><see langword="true"/> when a compiler is registered for this engine.</returns>
    public static bool TryCartridgeCompiler(string engineId, [NotNullWhen(returnValue: true)] out ICartridgeCompiler? compiler) {
        if (string.IsNullOrEmpty(value: engineId)) {
            compiler = null;

            return false;
        }

        lock (s_gate) {
            return s_compilers.TryGetValue(key: engineId, value: out compiler);
        }
    }
}
