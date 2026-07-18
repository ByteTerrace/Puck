namespace Puck.Scene;

/// <summary>
/// The SAFE, enumerated world render-scale tiers a run pins for the settled REVEALED room view — the demo/user-facing
/// quality option layered over the engine's CONTINUOUS render-scale knob (<c>SdfViewSnapshot.RenderScale</c>, quantized
/// to <c>SdfWorldEngine.RenderScaleQ</c>). The player picks one of these KNOWN-GOOD steps, never a free numeric value;
/// each tier is pinned to an exact quantized numerator so the reduced render extent (<c>worldRenderDims</c>) is a stable
/// integer at any window size and the bilinear upsample stays artifact-free. The continuous knob stays reachable
/// PROGRAMMATICALLY (layout eases, dev tooling, the Post render-scale lever) — the enumerated policy lives only here at
/// the user surface. Names + scales are the ONE definition (<see cref="WorldRenderScaleTiers"/>); the run-document field
/// validator, the console <c>render-scale</c> verb, and the boot resolution all read them there.
/// </summary>
public enum WorldRenderScaleTier {
    /// <summary>Full native resolution (q = 255, scale 1.0): the bit-exact fast path (Stage 2's exact-copy). 100% of the
    /// native pixel work — 1280x800 at the demo default.</summary>
    Native,

    /// <summary>~three-quarter the native pixel work (q = 221, scale ~0.867): render extent ~1109x693 at 1280x800.</summary>
    ThreeQuarter,

    /// <summary>~half the native pixel work (q = 181, scale ~0.710 ≈ 1/√2): render extent ~909x568 at 1280x800.</summary>
    Half,

    /// <summary>~quarter the native pixel work (q = 128, scale ~0.502): render extent ~643x402 at 1280x800 — exactly the
    /// reduction the Post <c>world-render-scale</c> lever pins as within the resampling-blur envelope.</summary>
    Quarter,

    /// <summary>~eighth the native pixel work (q = 90, scale ~0.353 ≈ 1/(2√2)): render extent ~452x282 at 1280x800 — the
    /// most aggressive tier, sized (measured wave-2 tier-refit) so the settled revealed room's views+beam GPU cost fits
    /// under the 120 Hz present-rate tier's ~8.33 ms frame budget, which <see cref="Quarter"/> still lands above.</summary>
    Eighth,
}

/// <summary>
/// The name ↔ tier ↔ pinned scale mapping for <see cref="WorldRenderScaleTier"/> — the ONE place the safe tier set is
/// defined. The run-document validator (<c>OverworldNode.Validate</c>), the demo's live <c>render-scale</c> verb, and
/// the boot resolution all read it, so the accepted names and the fed float scale never fork.
/// </summary>
public static class WorldRenderScaleTiers {
    // The pinned RenderScaleQ numerators (1..255; 255 = native). Chosen so (a) worldRenderDims = max(1,(dim*q+127)/255)
    // is a stable integer at any window size, and (b) the fed float scale (q/255) re-quantizes back to EXACTLY this q
    // through SdfWorldEngine.RenderScaleQ. Cost ~ (q/255)^2 of the native pixel work: 1.00 / 0.75 / 0.50 / 0.25 / 0.125.
    private const byte NativeQ = 255;
    private const byte EighthQ = 90;
    private const byte HalfQ = 181;
    private const byte QuarterQ = 128;
    private const byte ThreeQuarterQ = 221;

    /// <summary>The canonical tier names, in descending quality order — the valid set the validator and the verb echo.</summary>
    public static readonly IReadOnlyList<string> Names = ["native", "three-quarter", "half", "quarter", "eighth"];

    /// <summary>The valid names joined for an error / echo message.</summary>
    public static string ValidNames => string.Join(separator: ", ", values: Names);

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

    /// <summary>The pinned RenderScaleQ numerator (1..255; 255 = native) for a tier.</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The quantized numerator.</returns>
    public static byte ScaleQ(WorldRenderScaleTier tier) => tier switch {
        WorldRenderScaleTier.ThreeQuarter => ThreeQuarterQ,
        WorldRenderScaleTier.Half => HalfQ,
        WorldRenderScaleTier.Quarter => QuarterQ,
        WorldRenderScaleTier.Eighth => EighthQ,
        _ => NativeQ,
    };

    /// <summary>The float render scale (= q/255) fed to <c>SdfViewSnapshot.RenderScale</c>, chosen so the engine's
    /// RenderScaleQ re-quantizes it back to exactly <see cref="ScaleQ"/>. Native returns 1.0 (the bit-exact path).</summary>
    /// <param name="tier">The tier.</param>
    /// <returns>The float render scale.</returns>
    public static float Scale(WorldRenderScaleTier tier) => (ScaleQ(tier: tier) / 255f);
}
