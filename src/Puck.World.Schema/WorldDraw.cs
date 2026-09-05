using System.Text.Json.Serialization;

namespace Puck.World;

/// <summary>The <c>generation</c> section — the seed ladder's document rung (see
/// <c>GeneratorEngine.ComputeSeedState</c>). One authored value that moves every draw in the document at once (an
/// author's explicit "reroll the world" lever), distinct from the running instance's identity, which is not document
/// data at all, and from a site's own descriptor, which is what separates one site from another.</summary>
/// <param name="WorldSeed">Folded into every site's <c>Pcg32XshRr</c> starting state. Defaults to <c>0</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGenerationDefaults(ulong WorldSeed = 0UL) {
    /// <summary>Gets the section's default — world seed 0.</summary>
    public static WorldGenerationDefaults Default { get; } = new WorldGenerationDefaults(WorldSeed: 0UL);
}
/// <summary>
/// The document's SITE vocabulary — the descriptors the seed ladder's last rung folds, and the two rules that follow
/// from a site's class.
/// </summary>
/// <remarks>
/// <para>A boot-only site is a document field read exactly once at composition (<c>population.capacity</c>,
/// <c>host.backend</c>): the boot resolver draws it, writes the settled value into the ordinary literal field, and
/// clears the facet — a settled field is indistinguishable from an authored one thereafter, so stderr narration at
/// settlement time is the only surface that can say the value was random at all. A state site is a
/// <see cref="WorldStateRow"/>: its facet is never cleared, its cursor and drawn masks persist in the document, and a
/// save/reload resumes the sequence exactly where it stopped.</para>
/// <para>A descriptor is an identity, never a position: a positional ordinal would renumber under ordinary
/// operation (a settled facet clearing, a <c>world.row.remove state</c> retiring a row, an <c>UpsertStateRow</c>
/// adding one), silently re-pointing a live site's stream while its cursor kept counting.</para>
/// </remarks>
public static class WorldDrawSites {
    /// <summary>The descriptor <c>host.backendRow</c> boot read narrates under.</summary>
    public const string HostBackend = "host.backend";
    /// <summary>The descriptor <c>bodies.capacityRow</c> boot read narrates under.</summary>
    public const string PopulationCapacity = "bodies.capacity";

    /// <summary>Determines whether <paramref name="site"/> is a BOOT-ONLY document field — drawn once at composition, settled
    /// into an ordinary literal, and cleared (see this type's remarks).</summary>
    /// <param name="site">The site descriptor.</param>
    /// <returns><see langword="true"/> for a boot-only field site.</returns>
    public static bool IsBootOnly(string site) =>
        (string.Equals(
            a: site,
            b: PopulationCapacity,
            comparisonType: StringComparison.Ordinal
        ) ||
        string.Equals(
            a: site,
            b: HostBackend,
            comparisonType: StringComparison.Ordinal
        ));
    /// <summary>Returns the descriptor a <see cref="WorldStateRow"/>'s own <see cref="StateRow.Draw"/> resolves
    /// under.</summary>
    /// <param name="rowName">The site row's name.</param>
    /// <returns>The site descriptor.</returns>
    public static string StateRow(CellName rowName) => $"state.{rowName}";
}
