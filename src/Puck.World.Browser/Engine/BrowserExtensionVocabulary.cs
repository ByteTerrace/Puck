using System.Runtime.CompilerServices;

namespace Puck.World.Browser.Engine;

/// <summary>Wires the injection seams <see cref="WorldDefinitionValidator"/> requires installed before any document
/// parses — a composition root's job everywhere else (<c>Puck.World.Client.WorldSchemaVocabularyHooks</c> is the
/// desktop client's, <c>Puck.World.Silo</c>'s own installer the other). Puck.World.Browser references neither
/// <c>Puck.World.Protocol</c> nor any extension-owning assembly (Architecture.props' exact-closure profile), so
/// every predicate here honestly answers <see langword="null"/> — this host carries no catalog at all for a
/// post-render extension or a probe kind, rather than a real <see langword="false"/>
/// refusal a Schema-only host WITH a (empty) catalog would give. The validator routes a <see langword="null"/>
/// answer into its deferred collection instead of refusing. Machine checks are deferred by passing no catalog
/// to document validation; loading this assembly cannot replace another host's machine vocabulary.</summary>
internal static class BrowserExtensionVocabulary {
    /// <summary>Installs every REQUIRED (never absent-tolerant) hook the validator throws on when unset.</summary>
    [ModuleInitializer]
    public static void Install() {
        WorldExtensionVocabularyHook.PostRenderExtensionCheck = static _ => null;
        WorldProbeVocabularyHook.ProbeKindCheck = static _ => null;
    }
}
