namespace Puck.World;

/// <summary>The schema's post-process package vocabulary hook: which render graph package ids a <c>views.post</c> row may
/// name. A composition root installs the host's catalog before any document loads.</summary>
public static class WorldPostProcessVocabularyHook {
    /// <summary>Gets or sets the check against the host's render graph package catalog. A <see langword="null"/> answer
    /// reports a host with no catalog; an unset delegate is a composition error.</summary>
    public static Func<string, bool?>? PostProcessPackageCheck { get; set; }

    /// <summary>Determines whether a package id names a post-process package the host offers.</summary>
    /// <param name="package">The render graph package id.</param>
    /// <returns><see langword="true"/> or <see langword="false"/> for a host with a catalog, or
    /// <see langword="null"/> when this host has none.</returns>
    /// <exception cref="InvalidOperationException">The composition root did not install the hook.</exception>
    public static bool? IsPostProcessPackage(string package) =>
        ((PostProcessPackageCheck is { } check)
            ? check(package)
            : throw new InvalidOperationException(message: "WorldPostProcessVocabularyHook.PostProcessPackageCheck was never installed; views.post packages cannot be validated.")
        );
}
