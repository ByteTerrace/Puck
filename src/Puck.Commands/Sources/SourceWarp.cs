namespace Puck.Commands;

/// <summary>A warp pass a placement draws its face through, such as a screen's glass curvature or its bezel. The pass
/// is drawn whether or not it declares an inverse, but only a warp with an <see cref="Inverse"/> lets a hit on the face
/// reach the source: a warp without one refuses as an input path.</summary>
/// <param name="Pass">The pass that draws the warp.</param>
/// <param name="Inverse">The exact map from a point on the drawn face back to the face point the pass sampled, or
/// <see langword="null"/> when the pass declares none.</param>
public sealed record SourceWarp(string Pass, SourceWarpInverse? Inverse = null);
/// <summary>A warp pass's declared exact inverse: the map from a point on the drawn face, in face coordinates, to the
/// face point the pass sampled. A new kind of inverse is a new arm here, evaluated in fixed point by
/// <see cref="SourceMapping"/>.</summary>
public abstract record SourceWarpInverse {
    private protected SourceWarpInverse() {
    }

    /// <summary>An affine map <c>(u, v) → (M11·u + M12·v + M13, M21·u + M22·v + M23)</c>, which covers a scale about
    /// the face's centre such as a bezel's inset, a shift, a rotation and a shear.</summary>
    /// <param name="M11">The coefficient of <c>u</c> in the sampled <c>u</c>.</param>
    /// <param name="M12">The coefficient of <c>v</c> in the sampled <c>u</c>.</param>
    /// <param name="M13">The constant term of the sampled <c>u</c>.</param>
    /// <param name="M21">The coefficient of <c>u</c> in the sampled <c>v</c>.</param>
    /// <param name="M22">The coefficient of <c>v</c> in the sampled <c>v</c>.</param>
    /// <param name="M23">The constant term of the sampled <c>v</c>.</param>
    public sealed record Affine(float M11, float M12, float M13, float M21, float M22, float M23) : SourceWarpInverse {
        /// <summary>Creates the inverse of an inset that shows the whole face inside a border <paramref name="border"/>
        /// wide on every side: a scale of <c>1 / (1 − 2·border)</c> about the face's centre.</summary>
        /// <param name="border">The border's width as a fraction of the face, in <c>[0, 0.5)</c>.</param>
        /// <returns>The inverse.</returns>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="border"/> is not finite or lies outside
        /// <c>[0, 0.5)</c>.</exception>
        public static Affine Inset(float border) {
            if (
                !float.IsFinite(f: border) ||
                (border < 0f) ||
                (border >= 0.5f)
            ) {
                throw new ArgumentOutOfRangeException(
                    actualValue: border,
                    message: "The border must be finite and lie in [0, 0.5).",
                    paramName: nameof(border)
                );
            }

            var scale = (1f / (1f - (2f * border)));
            var offset = (0.5f - (0.5f * scale));

            return new Affine(
                M11: scale,
                M12: 0f,
                M13: offset,
                M21: 0f,
                M22: scale,
                M23: offset
            );
        }
    }
}
