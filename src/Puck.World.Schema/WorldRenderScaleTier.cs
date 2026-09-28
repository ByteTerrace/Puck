using System.Text.Json.Serialization;
using Puck.Abstractions.Documents;

namespace Puck.World;

/// <summary>
/// The enumerated world render-scale tiers a player or a quality preset picks, never a free numeric value, over the
/// continuous render scale a view carries (<c>SdfViewSnapshot.RenderScale</c>). A view renders its output at its rect
/// times its render scale, rounded up on each axis to a step of the render graph's extent quantization
/// (<c>RenderGraphExtent.Quantize</c>, sixteen steps per power-of-two octave), and the root's <c>place</c> pass
/// reconstructs that output into the rect at <c>world.upscale-sharpness</c>. The continuous scale stays reachable in
/// code (layout transitions); the enumerated set lives only at the user surface. <see cref="WorldRenderScaleTiers"/> is
/// the one definition of the names and scales, which the world document's quality presets, the console
/// <c>world.render-scale</c> verb and the boot resolution read. Each extent below is a lone whole-display view at
/// 1280x800.
/// </summary>
[JsonConverter(typeof(StrictEnumConverter<WorldRenderScaleTier>))]
public enum WorldRenderScaleTier {
    /// <summary>Native resolution (scale 1): the view renders at its rect's own extent, 1280x800, and a lone
    /// whole-display view is not placed at all, so the root shows the world's output itself.</summary>
    Native,

    /// <summary>Scale 221/255 (about 0.867), which the extent quantization rounds up to 0.875: 1120x700, about three
    /// quarters of the native pixel work.</summary>
    ThreeQuarter,

    /// <summary>Scale 181/255 (about 0.710), which the extent quantization rounds up to 0.75: 960x600, about 0.56 of the
    /// native pixel work.</summary>
    Half,

    /// <summary>Scale 128/255 (about 0.502), which the extent quantization rounds up to 0.5625: 720x450, about 0.32 of the
    /// native pixel work.</summary>
    Quarter,

    /// <summary>Scale 90/255 (about 0.353), which the extent quantization rounds up to 0.375: 480x300, about 0.14 of the
    /// native pixel work; the most aggressive tier.</summary>
    Eighth,
}
/// <summary>
/// The name ↔ tier ↔ pinned scale mapping for <see cref="WorldRenderScaleTier"/> — the ONE place the safe tier set is
/// defined. The world document's quality presets (<c>WorldQualityPreset.RenderScale</c>), the live
/// <c>world.render-scale</c> verb, and the boot resolution all read it, so the accepted names and the fed float scale
/// never fork.
/// </summary>
public static class WorldRenderScaleTiers {
    private const byte EighthQ = 90;
    private const byte HalfQ = 181;
    // Each tier's scale is a numerator over 255; the extent quantization then rounds the scaled rect up to its next step.
    private const byte NativeQ = 255;
    private const byte QuarterQ = 128;
    private const byte ThreeQuarterQ = 221;

    /// <summary>The canonical tier names, in descending quality order — the valid set the validator and the verb echo.</summary>
    public static readonly IReadOnlyList<string> Names = ["native", "three-quarter", "half", "quarter", "eighth"];

    /// <summary>The valid names joined for an error / echo message.</summary>
    public static string ValidNames => string.Join(
        separator: ", ",
        values: Names
    );

    /// <summary>Returns the tier whose <see cref="Scale"/> lies nearest a continuous render scale, the reverse of
    /// <see cref="Scale"/>: a tier's own scale maps back to that tier exactly, and a continuous scale between tiers
    /// quantizes to the closer one (the tier declared first on a tie).</summary>
    /// <param name="scale">The continuous render scale, where 1 is native.</param>
    /// <returns>The nearest tier.</returns>
    public static WorldRenderScaleTier Nearest(float scale) => NearestTier.Of(
        scale: static tier => Scale(tier: tier),
        unordered: WorldRenderScaleTier.Native,
        value: scale
    );
    /// <summary>The canonical (lower-case) name of a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The canonical name.</returns>
    public static string Name(WorldRenderScaleTier tier) => tier switch {
        WorldRenderScaleTier.ThreeQuarter => "three-quarter",
        WorldRenderScaleTier.Half => "half",
        WorldRenderScaleTier.Quarter => "quarter",
        WorldRenderScaleTier.Eighth => "eighth",
        _ => "native",
    };
    /// <summary>The render scale a tier feeds to <c>SdfViewSnapshot.RenderScale</c>: <see cref="ScaleQ"/> over 255, which
    /// is exactly 1 for <see cref="WorldRenderScaleTier.Native"/>.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The render scale, in (0, 1].</returns>
    public static float Scale(WorldRenderScaleTier tier) => (ScaleQ(tier: tier) / 255f);
    /// <summary>The numerator over 255 of a tier's render scale (1..255; 255 is native).</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The numerator.</returns>
    public static byte ScaleQ(WorldRenderScaleTier tier) => tier switch {
        WorldRenderScaleTier.ThreeQuarter => ThreeQuarterQ,
        WorldRenderScaleTier.Half => HalfQ,
        WorldRenderScaleTier.Quarter => QuarterQ,
        WorldRenderScaleTier.Eighth => EighthQ,
        _ => NativeQ,
    };
    /// <summary>Resolves a tier name (case-insensitive, trimmed) to its <see cref="WorldRenderScaleTier"/>.</summary>
    /// <param name="name">The tier name (a run-doc value or a typed console argument); null/whitespace is unknown.</param>
    /// <param name="tier">The resolved tier when the return is true (else <see cref="WorldRenderScaleTier.Native"/>).</param>
    /// <returns>Whether the name named a known tier.</returns>
    public static bool TryParse(string? name, out WorldRenderScaleTier tier) {
        switch ((name ?? "").Trim().ToLowerInvariant()) {
            case "native":
                tier = WorldRenderScaleTier.Native;

                return true;
            case "three-quarter":
                tier = WorldRenderScaleTier.ThreeQuarter;

                return true;
            case "half":
                tier = WorldRenderScaleTier.Half;

                return true;
            case "quarter":
                tier = WorldRenderScaleTier.Quarter;

                return true;
            case "eighth":
                tier = WorldRenderScaleTier.Eighth;

                return true;
            default:
                tier = WorldRenderScaleTier.Native;

                return false;
        }
    }
}
