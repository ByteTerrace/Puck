using System.Runtime.CompilerServices;
using Puck.World.Protocol;

namespace Puck.World.Testing;

/// <summary>
/// The wiring of <c>Puck.World.Schema</c>'s injection seams for a suite below the client, which cannot call
/// <c>WorldSchemaVocabularyHooks.Install</c>: the mutation-kind vocabulary from <see cref="WorldMutationKindCatalog"/>,
/// as every root installs it, and every probe kind admitted. Such a suite references no render graph package catalog,
/// so the post-process hooks answer <see langword="null"/> and the validator defers that check, as the browser host's
/// does. The input, binding and context-family hooks stay unset, which skips those checks. A law that needs a refusal
/// only the client's vocabulary gives belongs in a suite that links <c>VocabularyHooks.Client.cs</c>.
/// </summary>
internal static class VocabularyHooks {
    [ModuleInitializer]
    internal static void Install() {
        MutationKindVocabularyHook.Describe = WorldMutationKindCatalog.DescribeMask;
        MutationKindVocabularyHook.TryParse = WorldMutationKindCatalog.TryParseMask;
        WorldPostProcessVocabularyHook.PostProcessPackageCheck = static _ => null;
        WorldPostProcessVocabularyHook.PostProcessConfigCheck = static (_, _, _) => null;
        WorldProbeVocabularyHook.ProbeKindCheck = static _ => true;
    }
}
