using System.Runtime.CompilerServices;

namespace Puck.World.Browser.Engine;

/// <summary>Wires the injection seams <see cref="WorldDefinitionValidator"/> requires installed before any document
/// parses — a composition root's job everywhere else (<c>Puck.World.Client.WorldSchemaVocabularyHooks</c> is the
/// desktop client's, <c>Puck.World.Silo</c>'s own installer the other). Puck.World.Browser references neither
/// <c>Puck.World.Protocol</c> nor any extension-owning assembly (Architecture.props' exact-closure profile), so
/// every predicate here honestly answers "not registered" rather than reaching for a catalog this build does not
/// ship — a document naming a screen-machine engine, a post-render extension, or a probe kind refuses by name here,
/// the same refusal a Schema-only host with no engine cores wired would give.</summary>
internal static class BrowserExtensionVocabulary {
    /// <summary>Installs every REQUIRED (never absent-tolerant) hook the validator throws on when unset.</summary>
    [ModuleInitializer]
    public static void Install() {
        WorldExtensionVocabularyHook.PostRenderExtensionCheck = static _ => false;
        WorldExtensionVocabularyHook.ScreenMachineCartridgeCheck = static _ => false;
        WorldExtensionVocabularyHook.ScreenMachineEngineCheck = static _ => false;
        WorldProbeVocabularyHook.ProbeKindCheck = static _ => false;
    }
}
