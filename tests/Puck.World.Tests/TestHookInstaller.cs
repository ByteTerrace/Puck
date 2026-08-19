using System.Runtime.CompilerServices;
using Puck.Input.Devices;

namespace Puck.World.Tests;

/// <summary>
/// This project's OWN wiring of <c>Puck.World.Schema</c>'s composition-root injection seams
/// (<see cref="BindingVocabularyHook"/>, <see cref="WorldExtensionVocabularyHook"/>,
/// <see cref="GamepadButtonVocabularyHook"/>) — the same shape <c>src/Puck.World/WorldDataHookInstaller.cs</c>
/// installs for the real engine, minimal here because this suite never boots a machine or resolves a live command
/// registry. <see cref="BindingVocabularyHook.VocabularyCheck"/> is left unset — its own contract is absent-tolerant
/// (a command-registry lint that no-ops until a composition root exists); nothing here needs it.
/// <see cref="GamepadButtonVocabularyHook.IsKnownButtonName"/> is wired to the real
/// <see cref="GamepadButtons"/> catalog (available transitively via the <c>Puck.Overlays</c> reference) so a
/// binding-bar slot-set law can exercise the real refusal.
/// </summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        // Permissive rather than a real registry: this suite exercises the DOCUMENT/AUTHORITY substrate, never
        // machine boot, so every engine id a shipped world names is accepted rather than checked against
        // Puck.HumbleGamingBrick/Puck.AdvancedGamingBrick's real registrations (out of scope; see README.md).
        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = static _ => true;
        GamepadButtonVocabularyHook.IsKnownButtonName = GamepadButtonCatalog.IsKnownName;
        Protocol.MutationKindVocabularyHook.Describe = Protocol.WorldMutationKindCatalog.DescribeMask;
        Protocol.MutationKindVocabularyHook.TryParse = Protocol.WorldMutationKindCatalog.TryParseMask;
    }}
