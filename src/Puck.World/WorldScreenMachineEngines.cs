using Puck.Abstractions.Machines;
using Puck.AdvancedGamingBrick;
using Puck.HumbleGamingBrick;

namespace Puck.World;

/// <summary>
/// The single source of truth for which <see cref="IScreenMachineEngine"/>s this build ships — read by BOTH
/// <c>WorldBootComposition</c> (the DI registration <see cref="Server.WorldMachineHost"/> resolves against) and
/// <see cref="WorldDataHookInstaller"/> (the pre-container <see cref="WorldExtensionVocabularyHook"/> the validator
/// checks a declared engine key against), so the two can never drift — an engine missing from one is missing from
/// both by construction, rather than registered for DI while unrecognized at load, or recognized at load and then
/// unresolvable.
/// </summary>
internal static class WorldScreenMachineEngines {
    /// <summary>Every screen-machine engine this build ships. Adding a THIRD engine is exactly one line here — no
    /// other file names a concrete engine type.</summary>
    /// <remarks>These INSTANCES are shared: the same objects answer the pre-container vocabulary check and are handed
    /// to the container by instance. An <see cref="IScreenMachineEngine"/> is a factory — per-machine state belongs to
    /// the <see cref="IScreenMachine"/> it creates — so an engine listed here must be stateless and must not be
    /// <see cref="IDisposable"/>: a container never disposes an instance it did not construct, and a second host in
    /// this process would share these rather than get its own.</remarks>
    public static IReadOnlyList<IScreenMachineEngine> All { get; } = [
        new GamingBrickEngine(),
        new AdvancedGamingBrickEngine(),
    ];
}
