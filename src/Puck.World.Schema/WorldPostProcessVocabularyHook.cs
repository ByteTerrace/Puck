using System.Text.Json;

namespace Puck.World;

/// <summary>The schema's post-process package vocabulary hook: which render graph package ids a <c>views.post</c> row may
/// name, and whether a row's config binds against its package's schema. A composition root installs the host's catalog
/// before any document loads.</summary>
public static class WorldPostProcessVocabularyHook {
    /// <summary>Gets or sets the check against the host's render graph package catalog. A <see langword="null"/> answer
    /// reports a host with no catalog; an unset delegate is a composition error.</summary>
    public static Func<string, bool?>? PostProcessPackageCheck { get; set; }
    /// <summary>Gets or sets the binding of a row's config against its package's schema, given the package id, the row's
    /// name and its config: the reason it does not bind, or <see langword="null"/> when it binds or the host has no
    /// catalog to bind it against. An unset delegate is a composition error.</summary>
    public static Func<string, string, JsonElement?, string?>? PostProcessConfigCheck { get; set; }

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
    /// <summary>Binds a post pass's config against its package's schema, as the graph compiler binds it when the root
    /// graph is composed.</summary>
    /// <param name="package">The post-process package id the row names.</param>
    /// <param name="pass">The row's name, which a refusal names.</param>
    /// <param name="config">The row's config, or <see langword="null"/> for every default.</param>
    /// <returns>Why the config does not bind, or <see langword="null"/> when it binds or this host has no catalog.</returns>
    /// <exception cref="InvalidOperationException">The composition root did not install the hook.</exception>
    public static string? ConfigRefusal(string package, string pass, JsonElement? config) =>
        ((PostProcessConfigCheck is { } check)
            ? check(package, pass, config)
            : throw new InvalidOperationException(message: "WorldPostProcessVocabularyHook.PostProcessConfigCheck was never installed; views.post configs cannot be validated.")
        );
}
