namespace Puck.State.Rules;

/// <summary>Everything a transform kernel needs beyond the transform itself: the store, the clocks a live read or
/// write is evaluated against, and the draw context a random selection samples from.</summary>
/// <param name="Arena">The store every read and write addresses.</param>
/// <param name="Time">The clocks a live read or write inside the transform is evaluated against.</param>
/// <param name="Generators">The section's declared draw sources, which a site's row may name instead of inlining
/// one.</param>
/// <param name="Seeds">The seed of every draw site, folded once by the host, or <see langword="null"/> when the host
/// draws for no site; a random transfer or a shuffle then refuses by name.</param>
/// <param name="Imagined">The sampler a hypothetical evaluation draws every private site from in place of the site's
/// own, or <see langword="null"/> to draw a private site only through <paramref name="DrawAuthority"/>.</param>
/// <param name="DrawAuthority">The authority for the private sites this transform may consume.</param>
/// <remarks>The caller's scope is what a refusal rewinds. The scalar transforms open none of their own; the four
/// vector kernels open one nested inside the caller's and close it before they return, so a caller sees the scope
/// depth it left.</remarks>
public readonly record struct ArenaTransformContext(StateArena Arena, ArenaTime Time, IReadOnlyList<GeneratorRow>? Generators = null, ArenaDrawSeeds? Seeds = null, PrivateDrawSample? Imagined = null, PrivateDrawAuthority? DrawAuthority = null);
