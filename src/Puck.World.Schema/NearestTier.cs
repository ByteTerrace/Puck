namespace Puck.World;

/// <summary>The one reverse lookup from a continuous lever value to the document's tiered home for it, shared by
/// <see cref="ShadowTiers.Tier"/> and <see cref="WorldRenderScaleTiers.Nearest"/>.</summary>
internal static class NearestTier {
    /// <summary>Returns the tier whose scale lies nearest <paramref name="value"/>, the tier declared first on a tie.
    /// A tier's own scale maps back to that tier exactly.</summary>
    /// <typeparam name="TTier">The tier enumeration.</typeparam>
    /// <param name="value">The continuous value.</param>
    /// <param name="scale">The tier's own scale.</param>
    /// <param name="unordered">The tier a value no scale compares against (NaN) resolves to.</param>
    /// <returns>The nearest tier.</returns>
    public static TTier Of<TTier>(float value, Func<TTier, float> scale, TTier unordered) where TTier : struct, Enum {
        var best = unordered;
        var bestDelta = float.MaxValue;

        foreach (var tier in Enum.GetValues<TTier>()) {
            var delta = MathF.Abs(x: (value - scale(arg: tier)));

            if (delta < bestDelta) {
                best = tier;
                bestDelta = delta;
            }
        }

        return best;
    }
}
