namespace Puck.World;

/// <summary>The schema's post-render extension vocabulary hook. Machine admission uses an explicitly supplied
/// <see cref="Abstractions.Machines.IMachineValidationCatalog"/> and has no process-global hook.</summary>
public static class WorldExtensionVocabularyHook {
    /// <summary>Checks the installed post-render catalog. A null answer reports an unavailable catalog; an unset
    /// delegate is a composition error. Machine catalogs are supplied to validation per invocation.</summary>
    public static Func<string, bool?>? PostRenderExtensionCheck { get; set; }

    /// <summary>Determines whether a post-render extension is installed.</summary>
    /// <param name="extensionId">The extension identifier.</param>
    /// <returns>True or false for a known catalog, or null when this environment has no catalog.</returns>
    /// <exception cref="InvalidOperationException">The composition root did not install the hook.</exception>
    public static bool? IsRegisteredPostRenderExtension(string extensionId) =>
        PostRenderExtensionCheck is { } check
            ? check(extensionId)
            : throw new InvalidOperationException("WorldExtensionVocabularyHook.PostRenderExtensionCheck was never installed; post-render extensions cannot be validated.");
}
