using System.Numerics;

namespace Puck.Overlays;

/// <summary>
/// A single flat color in the design-token system: components in <c>[0, 1]</c> so a value plugs straight into a
/// push-constant or token-block float slot, or a <see cref="Vector3"/>/<see cref="Vector4"/> accessor for CPU-side math.
/// </summary>
/// <param name="R">The red channel, 0-1.</param>
/// <param name="G">The green channel, 0-1.</param>
/// <param name="B">The blue channel, 0-1.</param>
/// <param name="A">The alpha channel, 0-1.</param>
public readonly record struct RgbaColor(float R, float G, float B, float A) {
    /// <summary>Gets the color as an opaque <see cref="Vector3"/> (<see cref="A"/> dropped).</summary>
    public Vector3 Rgb => new(
        x: R,
        y: G,
        z: B
    );
    /// <summary>Gets the color as a <see cref="Vector4"/>.</summary>
    public Vector4 Rgba => new(
        x: R,
        y: G,
        z: B,
        w: A
    );
}
/// <summary>
/// One bloom hue's ring + halo pair (the <c>bloom.*</c> tier-1 lit-state recipe; see
/// <see cref="OverlayThemeValues.ElevationSet"/>). Composite: a 1px lit ring plus an outer distance-falloff halo.
/// </summary>
/// <param name="Ring">The 1px lit ring color (<c>bloom.ring.alpha</c> baked into its own alpha, except neutral).</param>
/// <param name="Halo">The outer distance-falloff halo color (<c>bloom.halo.alpha</c> baked into its own alpha, except neutral).</param>
public readonly record struct BloomHue(RgbaColor Ring, RgbaColor Halo);
/// <summary>A CSS-style cubic-bezier easing curve's four control-point components.</summary>
/// <param name="X1">The first control point's x.</param>
/// <param name="Y1">The first control point's y.</param>
/// <param name="X2">The second control point's x.</param>
/// <param name="Y2">The second control point's y.</param>
public readonly record struct CubicBezier(float X1, float Y1, float X2, float Y2);
/// <summary>
/// The overlay's design-token MECHANISM: the CPU-side color/curve shapes every writer draws through, and the two
/// rendering-correctness icon constants that stay engine-side regardless of theme. Every actual palette/spacing/
/// type/elevation/diegetic/motion VALUE is document data now — the authored <c>theme</c> section
/// (<c>Puck.World.Schema.WorldThemeSection</c>, which this project cannot reference), resolved by the composition
/// root into <see cref="OverlayThemeValues"/> and read live through <see cref="OverlayThemeStore"/>. This file is
/// the mechanism the resolved values ride in, never their source.
/// </summary>
public static class DesignTokens {
    /// <summary>The fixed-cell text-run grid's own rendering-correctness constant (<see cref="OverlayFrameBuilder.CellHeight"/>'s
    /// universal size-to-cell conversion) — NOT authored: several authored type ranks each declare an independent
    /// line-height, and deriving the one universal size→cell conversion every writer shares from any single rank's
    /// ratio would make every writer's layout drift whenever a world retunes only that one rank.</summary>
    public static class Glyph {
        /// <summary>The on-screen glyph-cell height to type-size ratio (line-height ÷ size) <see cref="OverlayFrameBuilder.CellHeight"/>
        /// applies to any authored size.</summary>
        public const float CellAspectRatio = 1.5f;
    }
    /// <summary>The procedural icon grammar's engine-side rendering-correctness constants (the World icon language
    /// in <c>overlay-unified.frag.hlsl</c> — hairline capsule strokes on a shared glyph grid). Unlike every other
    /// former section, these are NOT authored: an AA ramp is a rasterization tuning constant, not a look choice.</summary>
    public static class Icon {
        /// <summary>The procedural glyph/icon anti-alias ramp, in glyph-local units.</summary>
        public const float AaRamp = 0.10f;
        /// <summary>The anti-alias ramp for hairline/rounded-rect edges, px.</summary>
        public const float EdgeAaRamp = 1.25f;
    }
}
