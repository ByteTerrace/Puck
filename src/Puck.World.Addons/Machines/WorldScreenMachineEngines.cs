using System.Diagnostics.CodeAnalysis;
using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.AdvancedGamingBrick.Forge;
using Puck.GamingBricks.Forge;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.HumbleGamingBrick.Forge;
using Puck.HumbleGamingBrick;
using Puck.World.Server;

namespace Puck.World;

/// <summary>
/// The single source of truth for which <see cref="IScreenMachineEngine"/>s this build ships — read by
/// <c>Puck.World</c>'s <c>WorldBootComposition</c> (the DI registration <see cref="IWorldMachineHost"/> callers
/// resolve against on the desktop) and by both composition roots' pre-container
/// <see cref="WorldExtensionVocabularyHook"/> wiring, so a document-declared engine key validates identically in
/// <c>Puck.World</c> and <c>Puck.World.Silo</c> — an engine missing from this list is missing everywhere, rather
/// than registered for DI while unrecognized at load, or recognized at load and then unresolvable.
/// </summary>
public static class WorldScreenMachineEngines {
    /// <summary>Every screen-machine engine this build ships. Adding a THIRD engine is exactly one line here — no
    /// other file names a concrete engine type.</summary>
    /// <remarks>These INSTANCES are shared: the same objects answer the pre-container vocabulary check and, on the
    /// desktop, are handed to the container by instance. An <see cref="IScreenMachineEngine"/> is a factory —
    /// per-machine state belongs to the <see cref="IScreenMachine"/> it creates — so an engine listed here must be
    /// stateless and must not be <see cref="IDisposable"/>: a container never disposes an instance it did not
    /// construct, and a second host in this process would share these rather than get its own.</remarks>
    public static IReadOnlyList<IScreenMachineEngine> All { get; } = [
        new GamingBrickEngine(),
        new AdvancedGamingBrickEngine(),
        new TuneInstrumentEngine(),
    ];

    /// <summary>The forge an engine compiles a <c>puck.cartridge.v1</c> document through when a machine source's
    /// content path names one (<see cref="WorldScreenSource.Machine.NamesCartridgeDocument"/>), keyed by engine
    /// id. An engine absent here boots only a ROM file; a compiler listed here is the brick's own forge, so the
    /// bytes a cabinet runs are exactly the bytes <c>forge.export</c> would have written from the same document.
    /// Same sharing rule as <see cref="All"/>: a compiler is stateless and owns no machine.</summary>
    public static IReadOnlyDictionary<string, ICartridgeCompiler> CartridgeCompilers { get; } = new Dictionary<string, ICartridgeCompiler>(comparer: StringComparer.Ordinal) {
        [new GamingBrickEngine().Id] = new HgbCartridgeCompiler(),
        [new AdvancedGamingBrickEngine().Id] = new AgbCartridgeCompiler(),
    };

    private static readonly WorldExtensionRegistry<IScreenMachineEngine> Registry = new(
        extensions: All,
        keyOf: static engine => engine.Id
    );

    /// <summary>Returns whether a document-declared engine key names an engine this build ships — the ONE registry
    /// every composition root's <see cref="WorldExtensionVocabularyHook.ScreenMachineEngineCheck"/> wiring reads, so
    /// no root builds a second one of its own.</summary>
    /// <param name="key">The document-declared engine key.</param>
    public static bool IsRegistered(string key) => Registry.IsRegistered(key: key);
    /// <summary>Returns whether the engine under <paramref name="key"/> compiles authored cartridge documents —
    /// the predicate <see cref="WorldExtensionVocabularyHook.ScreenMachineCartridgeCheck"/> reads, so a document
    /// pointing a non-compiling engine at a cartridge document refuses at load rather than faulting a slot at
    /// boot.</summary>
    /// <param name="key">The document-declared engine key.</param>
    public static bool CompilesCartridges(string key) => CartridgeCompilers.ContainsKey(key: key);
    /// <summary>Resolves the forge the engine under <paramref name="engineId"/> compiles a cartridge document
    /// through.</summary>
    /// <param name="engineId">The engine id a machine resolved to.</param>
    /// <param name="compiler">The engine's forge, on success.</param>
    /// <returns><see langword="true"/> when the engine compiles cartridge documents.</returns>
    public static bool TryCartridgeCompiler(string engineId, [NotNullWhen(returnValue: true)] out ICartridgeCompiler? compiler) => CartridgeCompilers.TryGetValue(
        key: engineId,
        value: out compiler
    );
}
