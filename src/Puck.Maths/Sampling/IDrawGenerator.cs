namespace Puck.Maths;

/// <summary>
/// The seekable, allocation-free draw shape every 32-bit generator in this wing exposes — the surface a consumer's
/// own sampling code (<c>Puck.State.GeneratorEngine</c>'s <c>DrawEntry</c>/<c>TryDrawNumeric</c> family, and
/// <see cref="AliasTable{TElement}"/>'s own sampling) runs against GENERICALLY, so substituting
/// <see cref="Pcg32Extended"/> for <see cref="Pcg32XshRr"/> reaches the identical sampling code rather than a second
/// copy of it. Implemented by both generators; never boxed — a caller constrains its own generic parameter
/// <c>where TGenerator : struct, IDrawGenerator</c> and threads it by <c>ref</c>, so the JIT specializes one
/// non-virtual body per concrete generator with no interface dispatch at the call site.
/// </summary>
public interface IDrawGenerator {
    /// <summary>Draws the next 32 uniformly random bits.</summary>
    /// <returns>A uniformly distributed 32-bit value.</returns>
    uint NextUInt32();
    /// <summary>Draws a uniformly random value from an inclusive range.</summary>
    /// <param name="minimum">One end of the inclusive range.</param>
    /// <param name="maximum">The other end of the inclusive range; the bounds may be given in either order.</param>
    /// <returns>A uniformly distributed value in <c>[min(minimum, maximum), max(minimum, maximum)]</c>.</returns>
    uint NextUInt32(uint minimum, uint maximum);
    /// <summary>Skips the generator forward by <paramref name="count"/> draws, in logarithmic time.</summary>
    /// <param name="count">The number of single-draw advances to apply.</param>
    void Advance(ulong count);
}
