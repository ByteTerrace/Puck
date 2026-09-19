namespace Puck.State.Rules;

/// <summary>Names the draw-site descriptor a row's seed ladder and stream id fold, so a host that carries draw
/// sites in more than one document section decides the naming rather than the arena.</summary>
/// <param name="arena">The arena holding the site.</param>
/// <param name="rowOrdinal">The site row's catalog ordinal.</param>
/// <returns>The site descriptor.</returns>
public delegate string ArenaDrawSite(StateArena arena, int rowOrdinal);

/// <summary>Everything a transform kernel needs beyond the transform itself: the store, the clocks a live read or
/// write is evaluated against, and the draw context a random selection samples from.</summary>
/// <param name="Arena">The store every read and write addresses.</param>
/// <param name="Time">The clocks a live read or write inside the transform is evaluated against.</param>
/// <param name="Generators">The section's declared draw sources, which a site's row may name instead of inlining
/// one.</param>
/// <param name="DocumentSeed">The document's own reroll lever, folded into every draw site's seed.</param>
/// <param name="InstanceIdentity">The running instance's identity, folded into every draw site's seed.</param>
/// <param name="Site">What names a draw site, or <see langword="null"/> for the row's own catalog name.</param>
/// <remarks>The caller's scope is what a refusal rewinds. The scalar transforms open none of their own; the four
/// vector kernels open one nested inside the caller's and close it before they return, so a caller sees the scope
/// depth it left.</remarks>
public readonly record struct ArenaTransformContext(StateArena Arena, ArenaTime Time, IReadOnlyList<GeneratorRow>? Generators = null, ulong DocumentSeed = 0UL, string InstanceIdentity = "", ArenaDrawSite? Site = null) {
    /// <summary>Returns the descriptor a draw site's seed ladder and stream id fold.</summary>
    /// <param name="rowOrdinal">The site row's catalog ordinal.</param>
    /// <returns>The site descriptor.</returns>
    public string SiteOf(int rowOrdinal) => ((Site is { } name)
        ? name(
            arena: Arena,
            rowOrdinal: rowOrdinal
        )
        : Arena.Catalog.Descriptors[rowOrdinal].Name
    );
}
