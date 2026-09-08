using System.Runtime.CompilerServices;

namespace Puck.World.Browser.Engine;

/// <summary>Wires the injection seams <see cref="WorldDefinitionValidator"/> requires installed before any document
/// parses — a composition root's job everywhere else (<c>Puck.World.Client.WorldSchemaVocabularyHooks</c> is the
/// desktop client's, <c>Puck.World.Silo</c>'s own installer the other). Puck.World.Browser references neither
/// <c>Puck.World.Protocol</c> nor any extension-owning assembly (Architecture.props' exact-closure profile), so
/// every predicate here honestly answers <see langword="null"/> — this host carries no catalog at all for a
/// screen-machine engine, a post-render extension, or a probe kind, rather than a real <see langword="false"/>
/// refusal a Schema-only host WITH a (empty) catalog would give. The validator routes a <see langword="null"/>
/// answer into its deferred collection instead of refusing.</summary>
internal static class BrowserExtensionVocabulary {
    /// <summary>Installs every REQUIRED (never absent-tolerant) hook the validator throws on when unset.</summary>
    [ModuleInitializer]
    public static void Install() {
        WorldExtensionVocabularyHook.PostRenderExtensionCheck = static _ => null;
        WorldExtensionVocabularyHook.ScreenMachineCartridgeCheck = static _ => null;
        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = static _ => null;
        WorldProbeVocabularyHook.ProbeKindCheck = static _ => null;
    }
}
