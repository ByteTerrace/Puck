using System.Runtime.CompilerServices;

namespace Puck.World.Transpiler.Tests;

/// <summary>Installs the post-process package and config hooks the validator requires before a document naming
/// <c>views.post</c> validates. This suite references no render graph package catalog, so each hook answers
/// <see langword="null"/> and the validator defers the check, as the browser host's does.</summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() {
        WorldPostProcessVocabularyHook.PostProcessPackageCheck = static _ => null;
        WorldPostProcessVocabularyHook.PostProcessConfigCheck = static (_, _, _) => null;
    }
}
