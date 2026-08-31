using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.HumbleGamingBrick.Forge.Tune;
using Puck.HumbleGamingBrick;

namespace Puck.World;

/// <summary>
/// The single source of truth for which <see cref="IScreenMachineEngine"/>s this build ships — read by
/// <c>Puck.World</c>'s <c>WorldBootComposition</c> (the DI registration <see cref="Server.WorldMachineHost"/>
/// resolves against on the desktop) and by both composition roots' pre-container
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

    private static readonly WorldExtensionRegistry<IScreenMachineEngine> Registry = new(
        extensions: All,
        keyOf: static engine => engine.Id
    );

    /// <summary>Returns whether a document-declared engine key names an engine this build ships — the ONE registry
    /// every composition root's <see cref="WorldExtensionVocabularyHook.ScreenMachineEngineCheck"/> wiring reads, so
    /// no root builds a second one of its own.</summary>
    /// <param name="key">The document-declared engine key.</param>
    public static bool IsRegistered(string key) => Registry.IsRegistered(key: key);
}
