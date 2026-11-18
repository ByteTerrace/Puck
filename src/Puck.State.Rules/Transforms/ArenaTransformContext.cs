namespace Puck.State.Rules;

/// <summary>Everything a transform kernel needs beyond the transform itself: the store, the clocks a live read or
/// write is evaluated against, and the draw context a random selection samples from.</summary>
/// <param name="Arena">The store every read and write addresses.</param>
/// <param name="Time">The clocks a live read or write inside the transform is evaluated against.</param>
/// <param name="Generators">The section's declared draw sources, which a site's row may name instead of inlining
/// one.</param>
/// <param name="Seeds">The seed of every draw site, folded once by the host, or <see langword="null"/> when the host
/// draws for no site; a random transfer or a shuffle then refuses by name.</param>
/// <param name="Draws">The draw declaration of every catalog ordinal the evaluation samples with, or
/// <see langword="null"/> for the arena's own: a hypothetical evaluation binds each private site to a sampler it owns
/// (<see cref="Draw.Imagined"/>) so it never reads the authority's stream.</param>
/// <remarks>The caller's scope is what a refusal rewinds. The scalar transforms open none of their own; the four
/// vector kernels open one nested inside the caller's and close it before they return, so a caller sees the scope
/// depth it left.</remarks>
public readonly record struct ArenaTransformContext(StateArena Arena, ArenaTime Time, IReadOnlyList<GeneratorRow>? Generators = null, ArenaDrawSeeds? Seeds = null, IReadOnlyList<Draw?>? Draws = null) {
    /// <summary>Returns the draw declaration the evaluation samples one site with.</summary>
    /// <param name="rowOrdinal">The site row's catalog ordinal; must address a row of <see cref="Arena"/>.</param>
    /// <returns>The site's declaration, bound to the evaluation's own sampler when it supplies one, or
    /// <see langword="null"/> when the row declares no draw.</returns>
    public Draw? DrawAt(int rowOrdinal) => ((Draws is { } draws) ? draws[rowOrdinal] : Arena.Rows[rowOrdinal].Draw);
}
