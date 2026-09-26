using System.Runtime.CompilerServices;

namespace Puck.World.Transpiler.Tests;

/// <summary>Installs the post-process package hook the validator requires before a document naming <c>views.post</c>
/// validates. This suite references no render graph package catalog, so the hook answers <see langword="null"/> and the
/// validator defers the check, as the browser host's does.</summary>
internal static class TestHookInstaller {
    [ModuleInitializer]
    internal static void Install() =>
        WorldPostProcessVocabularyHook.PostProcessPackageCheck = static _ => null;
}
