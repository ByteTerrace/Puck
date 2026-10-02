namespace Puck.World.Client;

/// <summary>The opacities a marker row's style presents: its chip's, and its ring's when the row declares a ring. Each
/// is read through the state mirror and mapped into its field's declared domain.</summary>
/// <param name="Chip">The icon chip's opacity, in <c>[0, 1]</c>.</param>
/// <param name="Ring">The ring's opacity, in <c>[0, 1]</c>; zero when the row declares no ring or no ring alpha.</param>
public readonly record struct WorldMarkerAlphas(float Chip, float Ring) {
    /// <summary>Resolves a marker row's opacities.</summary>
    /// <param name="mirror">The state mirror the opacities read through.</param>
    /// <param name="marker">The marker row.</param>
    /// <param name="index">The row's index in the document's <c>markers</c> section, which a report names.</param>
    /// <param name="domains">The guard that holds the last valid value of a bound opacity and reports its transitions.</param>
    /// <returns>The opacities.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="mirror"/> or <paramref name="marker"/> is
    /// <see langword="null"/>.</exception>
    public static WorldMarkerAlphas Resolve(WorldStateMirror mirror, WorldMarkerRow marker, int index, WorldValueDomainGuard domains) {
        ArgumentNullException.ThrowIfNull(argument: mirror);
        ArgumentNullException.ThrowIfNull(argument: marker);
        ArgumentNullException.ThrowIfNull(argument: domains);

        var site = new WorldValueSite(
            Index: index,
            Inner: "style",
            Section: "markers"
        );
        var chip = marker.Style.ChipAlpha;
        var ring = 0f;

        if ((marker.Ring is not null) && (marker.Style.RingAlpha is { } ringAlpha)) {
            ring = domains.Resolve(
                fallback: 0f,
                field: WorldValueFields.MarkerRingAlpha,
                mirror: mirror,
                scalar: in ringAlpha,
                site: in site,
                value: mirror.Scalar(
                    fallback: 0f,
                    scalar: in ringAlpha
                )
            );
        }

        return new WorldMarkerAlphas(
            Chip: domains.Resolve(
                fallback: 0f,
                field: WorldValueFields.MarkerChipAlpha,
                mirror: mirror,
                scalar: in chip,
                site: in site,
                value: mirror.Scalar(
                    fallback: 0f,
                    scalar: in chip
                )
            ),
            Ring: ring
        );
    }
}
