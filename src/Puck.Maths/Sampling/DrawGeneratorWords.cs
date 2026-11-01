namespace Puck.Maths;

/// <summary>
/// Presents a bare <see cref="IDrawGenerator"/> as an <see cref="IRawWordSource"/> that never refuses, so a
/// generator reaches the budgeted loops in <see cref="BoundedRandomSampling"/> through the same seam a fallible
/// stream does. The wrapper owns a copy of the generator: seed it with the caller's generator and read
/// <see cref="Generator"/> back afterwards.
/// </summary>
/// <typeparam name="TGenerator">The generator type.</typeparam>
/// <param name="generator">The generator to draw words from, copied into the wrapper.</param>
public struct DrawGeneratorWords<TGenerator>(TGenerator generator) : IRawWordSource where TGenerator : struct, IDrawGenerator {
    /// <summary>The generator, positioned after every word drawn through this wrapper.</summary>
    public TGenerator Generator = generator;

    /// <inheritdoc/>
    public bool TryNextWord(out uint word, out string reason) {
        word = Generator.NextUInt32();
        reason = string.Empty;
        return true;
    }
}
