using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>The <c>generation</c> section — the seed ladder's WORLD rung (see
/// <c>WorldGeneratorEngine.ComputeSeedState</c>). One authored value that moves EVERY draw in the document at once
/// (an author's explicit "reroll the world" lever), distinct from the running instance's identity, which is not
/// document data at all, and from a site's own descriptor, which is what separates one site from another.</summary>
/// <param name="WorldSeed">Folded into every site's <c>Pcg32XshRr</c> starting state. Defaults to <c>0</c>.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldGenerationDefaults(ulong WorldSeed = 0UL) {
    /// <summary>Gets the section's default — world seed 0.</summary>
    public static WorldGenerationDefaults Default { get; } = new WorldGenerationDefaults(WorldSeed: 0UL);
}
/// <summary>
/// WHEN a <see cref="WorldDraw"/> site's value is decided, and whether it may ever be decided again.
/// </summary>
/// <remarks>
/// <para>A <see cref="Boot"/> site draws exactly once, at first fill (process load or a fresh
/// <c>world.instance.start</c>), and refuses a later <c>generate</c> by name. <see cref="TickPeriod"/> and
/// <see cref="Event"/> sites also draw at first fill but stay redrawable through the same <c>generate</c>
/// effect/mutation; the engine draws no operational distinction between the two — cadence or event gating is
/// authored separately with the ordinary <c>rules</c> vocabulary (a <c>$tick</c>-scheduled Edge rule, or an
/// event-flag-gated one). The split is authored intent made legible at the site.</para>
/// </remarks>
[JsonConverter(typeof(StrictEnumConverter<WorldDrawTiming>))]
public enum WorldDrawTiming : byte {
    /// <summary>Drawn once at first fill; a later <c>generate</c> against this site refuses by name.</summary>
    Boot,

    /// <summary>Drawn at first fill and redrawable via <c>generate</c>; the author gates cadence with an ordinary
    /// <c>$tick</c>-scheduled rule.</summary>
    TickPeriod,

    /// <summary>Drawn at first fill and redrawable via <c>generate</c>; the author gates redraw with an ordinary
    /// event-flag rule.</summary>
    Event,
}
/// <summary>
/// The AUTHORED-RANDOMNESS facet: the declaration that a SITE's value is DRAWN rather than literal. One facet, one
/// source family, one engine — an NPC-bark text site, a loot cell, a random census, and a drawn host backend are the
/// SAME mechanism pointed at different sites.
/// </summary>
/// <remarks>
/// <para>Exactly one of <see cref="Source"/> (naming a row of the document's <c>generators</c> section) and
/// <see cref="Generator"/> (an inline source) is declared. The inline form compiles to an anonymous source of the
/// identical <see cref="WorldGenerator"/> family, so nothing is expressible one way and not the other.</para>
/// <para>A referenced source draws on the site's own stream: two sites naming one source draw independent
/// sequences, since the seed ladder folds the site descriptor and the position
/// (<see cref="WorldStateRow.DrawCursor"/>) and drawn masks (<see cref="WorldStateRow.DrawnMasks"/>) live on the
/// site. Sharing a source shares its shape and never its position, so pointing a second site at an existing table
/// can never perturb the first site's sequence.</para>
/// <para>A reference refuses at validate, before any draw runs, when it names a source that does not exist, names
/// one whose emission kind the site cannot hold, or names one the site's own timing cannot drive (a dealing source
/// at a settle-and-clear boot site has no second draw to deal into).</para>
/// </remarks>
/// <param name="Source">The declared source to draw from, by name — or <see langword="null"/> when
/// <see cref="Generator"/> inlines one.</param>
/// <param name="Generator">An inline anonymous source — or <see langword="null"/> when <see cref="Source"/> names a
/// declared one.</param>
/// <param name="Timing">When this site draws and whether it may be redrawn (see <see cref="WorldDrawTiming"/>).</param>
/// <param name="Secret">An authority-provisioned 256-bit secret for an independently keyed streamDraw sample at each cursor. Never sent in observations.</param>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WorldDraw(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldCellName? Source = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] WorldGenerator? Generator = null,
    WorldDrawTiming Timing = WorldDrawTiming.Boot,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ClosedBitset256? Secret = null
);
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
    /// <summary>Returns the descriptor a <see cref="WorldStateRow"/>'s own <see cref="WorldStateRow.Draw"/> resolves
    /// under.</summary>
    /// <param name="rowName">The site row's name.</param>
    /// <returns>The site descriptor.</returns>
    public static string StateRow(WorldCellName rowName) => $"state.{rowName}";
}
